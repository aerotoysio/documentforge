using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

namespace DocumentForge.Cli;

/// <summary>
/// Issue #66 Phase 5: spawn child <c>dfdb serve</c> processes on demand
/// so a single Studio session can stand up an entire local cluster
/// without juggling terminals. Each managed service is a real OS process,
/// owns its own data directory + ports, and dies when this manager is
/// disposed (parent shutdown).
///
/// <para>
/// Scope: <em>local dev orchestration</em>. Production setups want a real
/// supervisor (systemd, Docker, Kubernetes). The features that would make
/// this safe in production — capability-gated auth, binary allow-listing,
/// per-child resource limits — are explicit non-goals for v1.
/// </para>
///
/// <para>
/// Cross-platform: works on Windows / Linux / macOS because the .NET
/// host binary (resolved via <see cref="Environment.ProcessPath"/>) is
/// the same binary that's spawning. Children inherit the same .NET 9
/// runtime, no extra config required.
/// </para>
/// </summary>
public sealed class ServiceManager : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<int, ManagedService> _services = new();
    private bool _disposed;

    /// <summary>
    /// Spawn a <c>dfdb router</c> child against the given cluster
    /// config. Used by the Studio "Spawn router" UI. Routers are
    /// tracked the same way as serve children — same port allocation,
    /// same log capture, same reap-on-shutdown — but their args differ
    /// and they don't take a data directory.
    /// </summary>
    public ManagedService SpawnRouter(SpawnRouterOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ConfigPath))
            throw new ArgumentException("configPath is required.", nameof(options));
        if (!File.Exists(options.ConfigPath))
            throw new ArgumentException(
                $"configPath '{options.ConfigPath}' does not exist.", nameof(options));

        var binary = options.BinaryPath ?? Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "Cannot determine the dfdb binary path — set SpawnRouterOptions.BinaryPath.");

        var binaryFileName = Path.GetFileNameWithoutExtension(binary);
        var isDotnetHost = string.Equals(binaryFileName, "dotnet", StringComparison.OrdinalIgnoreCase);
        var preArgs = new List<string>();
        if (isDotnetHost)
        {
            var dll = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrEmpty(dll))
                throw new InvalidOperationException(
                    "Parent runs under dotnet host but entry assembly path is unresolved.");
            preArgs.Add(dll);
        }

        var port = options.Port > 0 ? options.Port : FindFreePort();
        lock (_lock)
        {
            ThrowIfDisposed();
            if (_services.ContainsKey(port))
                throw new InvalidOperationException($"Port {port} is already managed.");
        }

        var logDir = options.LogDir
            ?? Path.GetDirectoryName(Path.GetFullPath(options.ConfigPath))!;
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, $"router-{port}.log");

        var args = new List<string>(preArgs)
        {
            "router",
            "--config", options.ConfigPath,
            "--port", port.ToString(),
        };

        var psi = new ProcessStartInfo
        {
            FileName = binary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var process = new Process { StartInfo = psi };
        var logWriter = new StreamWriter(File.Open(logPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) logWriter.WriteLine(e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data is not null) logWriter.WriteLine("[stderr] " + e.Data); };

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start router child on port {port}.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var service = new ManagedService
        {
            Port = port,
            DataDir = Path.GetFullPath(options.ConfigPath),  // for routers, "data dir" = config path (for display)
            NodeName = options.Name ?? $"router-{port}",
            Process = process,
            LogPath = logPath,
            StartedAt = DateTime.UtcNow,
            BaseUrl = $"http://localhost:{port}",
            LogWriter = logWriter,
            Kind = "router",
        };

        lock (_lock) _services[port] = service;
        service.Ready = WaitForReady(service, TimeSpan.FromSeconds(10));
        return service;
    }

    /// <summary>
    /// Spawn a new <c>dfdb serve</c> on <paramref name="port"/> (or pick a
    /// free port if 0) with its own <paramref name="dataDir"/>. Returns
    /// the new service's HTTP base URL once Kestrel reports ready.
    /// Throws if the binary can't be found or the child fails to start.
    /// </summary>
    public ManagedService Spawn(SpawnOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.DataDir))
            throw new ArgumentException("dataDir is required.", nameof(options));

        var binary = options.BinaryPath ?? Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "Cannot determine the dfdb binary path — set SpawnOptions.BinaryPath or run under a host that exposes Environment.ProcessPath.");

        // Two invocation modes coexist in practice:
        //   1. Apphost: dfdb.exe spawned directly. Environment.ProcessPath
        //      is dfdb.exe and we just re-launch it with the serve verb.
        //   2. Framework-dependent: `dotnet dfdb.dll serve …`. Environment.ProcessPath
        //      is dotnet.exe (or just `dotnet` on Unix); we must pass the
        //      entry-assembly dll as the FIRST argument so dotnet runs it.
        // Apphost mode is the production shape; framework-dependent is what
        // `dotnet run` and explicit `dotnet path/to.dll` produce, and what
        // CI / local-dev workflows hit when launching without publish.
        var binaryFileName = Path.GetFileNameWithoutExtension(binary);
        var isDotnetHost = string.Equals(binaryFileName, "dotnet", StringComparison.OrdinalIgnoreCase);
        var preArgs = new List<string>();
        if (isDotnetHost)
        {
            var dll = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrEmpty(dll))
                throw new InvalidOperationException(
                    "Parent is running under the dotnet host but the entry assembly path could not be resolved. " +
                    "Set SpawnOptions.BinaryPath to the dfdb dll or apphost explicitly.");
            preArgs.Add(dll);
        }

        // Validate or assign a port. 0 means "pick something free."
        var port = options.Port > 0 ? options.Port : FindFreePort();

        lock (_lock)
        {
            ThrowIfDisposed();
            if (_services.ContainsKey(port))
                throw new InvalidOperationException($"Port {port} is already managed by this service.");
        }

        Directory.CreateDirectory(options.DataDir);
        var logPath = Path.Combine(options.DataDir, "serve.log");

        var args = new List<string>(preArgs)
        {
            "serve",
            "--data-dir", options.DataDir,
            "--port", port.ToString(),
        };
        if (!string.IsNullOrEmpty(options.NodeName))
            args.AddRange(new[] { "--node", options.NodeName });

        // Issue #101: serve is deny-by-default, so the child must be given
        // its security posture explicitly or it will refuse to start.
        if (!string.IsNullOrEmpty(options.ApiKey))
            args.AddRange(new[] { "--api-key", options.ApiKey });
        else if (options.InsecureDevMode)
            args.Add("--insecure-dev-mode");

        var psi = new ProcessStartInfo
        {
            FileName = binary,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var process = new Process { StartInfo = psi };
        // Tee output to a per-child log file so operators can inspect
        // crashes after the fact. The pipe is unbounded if we don't
        // drain it, which would deadlock the child.
        var logWriter = new StreamWriter(File.Open(logPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) logWriter.WriteLine(e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data is not null) logWriter.WriteLine("[stderr] " + e.Data); };

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start child process for port {port}.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var service = new ManagedService
        {
            Port = port,
            DataDir = Path.GetFullPath(options.DataDir),
            NodeName = options.NodeName,
            Process = process,
            LogPath = logPath,
            StartedAt = DateTime.UtcNow,
            BaseUrl = $"http://localhost:{port}",
            LogWriter = logWriter,
            Kind = "serve",
        };

        lock (_lock) _services[port] = service;

        // Best-effort readiness probe. /health responds in <100ms typically;
        // give up to 10s for slow startups (cold .NET, disk-bound recovery).
        // The caller gets a usable URL even if the probe times out — the
        // child may yet come up; we just can't promise it's ready.
        service.Ready = WaitForReady(service, TimeSpan.FromSeconds(10));
        return service;
    }

    /// <summary>
    /// Stop a managed service. Sends a kill signal and reaps the process
    /// (we don't have a cross-platform graceful SIGINT today). Returns
    /// false if no service is managed on that port.
    /// </summary>
    public bool Stop(int port)
    {
        ManagedService? service;
        lock (_lock)
        {
            ThrowIfDisposed();
            if (!_services.TryGetValue(port, out service))
                return false;
            _services.Remove(port);
        }
        StopService(service);
        return true;
    }

    /// <summary>Snapshot of every managed service — safe to enumerate.</summary>
    public IReadOnlyList<ManagedServiceInfo> List()
    {
        lock (_lock)
        {
            return _services.Values
                .Select(s => new ManagedServiceInfo(
                    s.Port,
                    s.DataDir,
                    s.NodeName,
                    s.BaseUrl,
                    s.StartedAt,
                    !s.Process.HasExited,
                    s.Process.HasExited ? (int?)s.Process.ExitCode : null,
                    s.LogPath,
                    s.Kind))
                .ToList();
        }
    }

    /// <summary>Read the tail of a managed service's log file.</summary>
    public string ReadLog(int port, int maxBytes = 16384)
    {
        ManagedService? service;
        lock (_lock)
        {
            if (!_services.TryGetValue(port, out service)) return string.Empty;
        }
        try
        {
            using var fs = File.Open(service.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length > maxBytes) fs.Seek(-maxBytes, SeekOrigin.End);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }
        catch (IOException) { return string.Empty; }
    }

    public void Dispose()
    {
        List<ManagedService> toStop;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            toStop = _services.Values.ToList();
            _services.Clear();
        }
        foreach (var s in toStop) StopService(s);
    }

    // ---------------------------------------------------------------- helpers

    private static void StopService(ManagedService s)
    {
        try
        {
            if (!s.Process.HasExited)
            {
                s.Process.Kill(entireProcessTree: true);
                s.Process.WaitForExit(2_000);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ServiceManager] error stopping :{s.Port} (PID {s.Process.Id}): {ex.Message}");
        }
        try { s.LogWriter.Dispose(); } catch { }
        try { s.Process.Dispose(); } catch { }
    }

    private static int FindFreePort()
    {
        // Bind to port 0 — OS hands us a free ephemeral port, we read it
        // off, immediately release the socket. There's an inherent race
        // where another process snags the port between our release and
        // the child's bind; in practice this is fine for dev orchestration.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool WaitForReady(ManagedService s, TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (s.Process.HasExited) return false;
            try
            {
                var resp = http.GetAsync($"{s.BaseUrl}/health").GetAwaiter().GetResult();
                if (resp.IsSuccessStatusCode) return true;
            }
            catch { /* not yet listening */ }
            Thread.Sleep(150);
        }
        return false;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ServiceManager));
    }
}

public sealed class SpawnOptions
{
    /// <summary>Working directory for the new service's data file + sidecars.
    /// Each spawn must use its own directory — they each take a lock file.</summary>
    public required string DataDir { get; init; }

    /// <summary>HTTP port. 0 = let the manager pick a free ephemeral port.</summary>
    public int Port { get; init; }

    /// <summary>Optional human label exposed via /health.node — Studio shows it
    /// in the connection picker. Defaults to "node-{port}" inside the child.</summary>
    public string? NodeName { get; init; }

    /// <summary>Override the binary used to spawn the child. Production sets
    /// this to <see cref="Environment.ProcessPath"/> implicitly; tests pass
    /// the path to the built dfdb host because under `dotnet test`
    /// <see cref="Environment.ProcessPath"/> points at the test runner.</summary>
    public string? BinaryPath { get; init; }

    /// <summary>Admin API key for the child. Issue #101 made `dfdb serve`
    /// deny-by-default, so every spawn needs either a key or an explicit
    /// <see cref="InsecureDevMode"/> opt-in or the child exits immediately.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Start the child with --insecure-dev-mode (no auth, loopback
    /// only). Ignored when <see cref="ApiKey"/> is set.</summary>
    public bool InsecureDevMode { get; init; }
}

/// <summary>Internal record of a running child. Studio sees the projection
/// via <see cref="ManagedServiceInfo"/>.</summary>
public sealed class ManagedService
{
    public int Port { get; init; }
    public string DataDir { get; init; } = "";
    public string? NodeName { get; init; }
    public required Process Process { get; init; }
    public string LogPath { get; init; } = "";
    public DateTime StartedAt { get; init; }
    public string BaseUrl { get; init; } = "";
    public bool Ready { get; set; }
    public required StreamWriter LogWriter { get; init; }

    /// <summary>"serve" | "router". Studio shows different chrome
    /// (icons, connection-add behaviour) per kind.</summary>
    public string Kind { get; init; } = "serve";
}

/// <summary>
/// Options for spawning a <c>dfdb router</c> child. The router is
/// stateless beyond its config file, so unlike <see cref="SpawnOptions"/>
/// there's no DataDir — the config path is the only required identity.
/// </summary>
public sealed class SpawnRouterOptions
{
    /// <summary>Path to the cluster.json the router will load.</summary>
    public required string ConfigPath { get; init; }
    /// <summary>HTTP port. 0 = let the manager pick a free ephemeral port.</summary>
    public int Port { get; init; }
    /// <summary>Optional human label exposed in the managed-services
    /// list. Defaults to "router-{port}".</summary>
    public string? Name { get; init; }
    /// <summary>Directory to drop the router's stdout/stderr log into.
    /// Defaults to the config file's directory.</summary>
    public string? LogDir { get; init; }
    /// <summary>Override the dfdb binary for tests.</summary>
    public string? BinaryPath { get; init; }
}

/// <summary>Public, serializable view of a managed service.</summary>
public sealed record ManagedServiceInfo(
    int Port,
    string DataDir,
    string? NodeName,
    string BaseUrl,
    DateTime StartedAt,
    bool Running,
    int? ExitCode,
    string LogPath,
    string Kind = "serve");
