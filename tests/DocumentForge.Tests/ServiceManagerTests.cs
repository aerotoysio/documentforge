using DocumentForge.Cli;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// Issue #66 Phase 5: the <see cref="ServiceManager"/> spawns real
/// child <c>dfdb serve</c> processes. These tests exercise the
/// lifecycle (spawn, ready-probe, list, stop, dispose) against the
/// actual binary built into the CLI project's bin output — so the
/// path-resolution + arg-passing + process-tree-kill semantics
/// are covered, not mocked away.
///
/// <para>
/// Cost: each spawn boots a .NET 9 host (~1-2s cold) and binds Kestrel
/// on a free port. The full suite stays under ~10s by sharing a manager
/// across the test class and minimising spawns.
/// </para>
/// </summary>
public sealed class ServiceManagerTests : IDisposable
{
    private readonly List<string> _dirs = new();
    private readonly ServiceManager _manager = new();

    public void Dispose()
    {
        _manager.Dispose();
        foreach (var d in _dirs)
        {
            try { Directory.Delete(d, recursive: true); } catch { }
        }
    }

    private string FreshDir(string label)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"svc_{label}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    // Resolve the dfdb binary built into the CLI project's bin output.
    // Under `dotnet test`, Environment.ProcessPath points at the test
    // runner, not dfdb — so we discover the apphost via a relative path
    // from the test assembly's location.
    private static string ResolveBinary()
    {
        var asmDir = Path.GetDirectoryName(typeof(ServiceManagerTests).Assembly.Location)!;
        // tests/DocumentForge.Tests/bin/<conf>/net9.0 → src/DocumentForge.Cli/bin/<conf>/net9.0/dfdb.exe
        var conf = new DirectoryInfo(asmDir).Parent!.Name; // Debug or Release
        var candidate = Path.GetFullPath(Path.Combine(
            asmDir, "..", "..", "..", "..", "..",
            "src", "DocumentForge.Cli", "bin", conf, "net9.0",
            OperatingSystem.IsWindows() ? "dfdb.exe" : "dfdb"));
        if (!File.Exists(candidate))
            throw new InvalidOperationException(
                $"Could not find dfdb binary at {candidate}. " +
                $"Build src/DocumentForge.Cli before running ServiceManager tests.");
        return candidate;
    }

    [Fact]
    public void Spawn_ProducesRunningServiceOnFreePort()
    {
        var dir = FreshDir("spawn");
        var svc = _manager.Spawn(new SpawnOptions
        {
            DataDir = dir,
            BinaryPath = ResolveBinary(),
            InsecureDevMode = true,
        });

        Assert.True(svc.Port > 0, "spawn must allocate a port");
        Assert.StartsWith("http://localhost:", svc.BaseUrl);
        Assert.True(svc.Ready, $"child did not become ready within timeout. Log:\n{_manager.ReadLog(svc.Port)}");

        // /health on the child must answer 200.
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var resp = http.GetAsync($"{svc.BaseUrl}/health").GetAwaiter().GetResult();
        Assert.True(resp.IsSuccessStatusCode);
    }

    [Fact]
    public void Spawn_WithoutAuthOrOptIn_ChildRefusesToStart()
    {
        // Issue #101 — serve is deny-by-default. A child spawned with no
        // ApiKey and no InsecureDevMode must exit instead of coming up open.
        var dir = FreshDir("noauth");
        var svc = _manager.Spawn(new SpawnOptions { DataDir = dir, BinaryPath = ResolveBinary() });

        Assert.False(svc.Ready, "an unauthenticated child must not become ready");
        Assert.Contains("no authentication configured", _manager.ReadLog(svc.Port));
    }

    [Fact]
    public void Spawn_WithApiKey_ChildStartsAndRequiresBearer()
    {
        var dir = FreshDir("withkey");
        var svc = _manager.Spawn(new SpawnOptions
        {
            DataDir = dir,
            BinaryPath = ResolveBinary(),
            ApiKey = "test-child-key",
        });

        Assert.True(svc.Ready, $"child did not become ready within timeout. Log:\n{_manager.ReadLog(svc.Port)}");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        // Unauthenticated data-plane request → 401.
        var anon = http.GetAsync($"{svc.BaseUrl}/collections").GetAwaiter().GetResult();
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, anon.StatusCode);

        // Correct bearer → 200.
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{svc.BaseUrl}/collections");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "test-child-key");
        var authed = http.SendAsync(req).GetAwaiter().GetResult();
        Assert.True(authed.IsSuccessStatusCode);
    }

    [Fact]
    public void List_ReflectsRunningChildren()
    {
        var dir = FreshDir("list");
        var svc = _manager.Spawn(new SpawnOptions { DataDir = dir, BinaryPath = ResolveBinary(), InsecureDevMode = true });

        var entries = _manager.List();
        Assert.Contains(entries, e => e.Port == svc.Port && e.Running);
    }

    [Fact]
    public void Stop_TerminatesAndReaps()
    {
        var dir = FreshDir("stop");
        var svc = _manager.Spawn(new SpawnOptions { DataDir = dir, BinaryPath = ResolveBinary(), InsecureDevMode = true });

        Assert.True(_manager.Stop(svc.Port));
        Assert.DoesNotContain(_manager.List(), e => e.Port == svc.Port);

        // After stop, the child's HTTP port stops listening (give the OS
        // a beat to release it before asserting).
        Thread.Sleep(500);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        var caught = false;
        try { _ = http.GetAsync($"{svc.BaseUrl}/health").GetAwaiter().GetResult(); }
        catch { caught = true; }
        Assert.True(caught, "child still answered /health after Stop");
    }

    [Fact]
    public void Stop_UnknownPort_ReturnsFalse()
    {
        Assert.False(_manager.Stop(63999));  // arbitrary unmanaged port
    }

    [Fact]
    public void Spawn_RejectsDuplicatePort()
    {
        var dir = FreshDir("dupport");
        var svc = _manager.Spawn(new SpawnOptions { DataDir = dir, BinaryPath = ResolveBinary(), InsecureDevMode = true });

        // Same explicit port → ServiceManager refuses to track a second.
        var dir2 = FreshDir("dupport2");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            _manager.Spawn(new SpawnOptions { DataDir = dir2, Port = svc.Port, BinaryPath = ResolveBinary(), InsecureDevMode = true }));
        Assert.Contains("already managed", ex.Message);
    }

    [Fact]
    public void ReadLog_CapturesChildOutput()
    {
        // The child writes its banner ("dfdb serve" + listening URL) to
        // stdout right after Kestrel starts. The manager tees output to a
        // per-child log; ReadLog should surface those lines.
        var dir = FreshDir("logs");
        var svc = _manager.Spawn(new SpawnOptions { DataDir = dir, BinaryPath = ResolveBinary(), InsecureDevMode = true });

        // Give the log writer a moment to flush — the redirected pipe
        // event handlers are best-effort.
        Thread.Sleep(500);
        var log = _manager.ReadLog(svc.Port);
        Assert.Contains("dfdb serve", log, StringComparison.OrdinalIgnoreCase);
    }
}
