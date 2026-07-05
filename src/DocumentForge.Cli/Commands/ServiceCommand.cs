using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DocumentForge.Cli.Commands;

/// <summary>
/// `dfdb service ...` — install/manage DocumentForge as a native Windows
/// service (visible in services.msc, auto-start on boot). Wraps <c>sc.exe</c>,
/// so create/delete/start/stop require an elevated (admin) prompt.
///
/// <para>
/// The installed service runs <c>dfdb serve</c> with the port/data-dir/api-key
/// you pass here. Because `serve` calls <c>UseWindowsService()</c>, the process
/// speaks the Service Control Manager protocol properly (no wrapper needed).
/// </para>
/// </summary>
public static class ServiceCommand
{
    private const string DefaultServiceName = "DocumentForge";
    private const string DisplayName = "DocumentForge Database Service";

    public static int Run(string[] args)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Console.Error.WriteLine("`dfdb service` manages a Windows service and is only available on Windows.");
            Console.Error.WriteLine("On Linux/macOS, run `dfdb serve ...` under systemd/launchd instead.");
            return 1;
        }

        if (args.Length == 0)
        {
            PrintHelp();
            return 0;
        }

        var action = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();
        return action switch
        {
            "install" => Install(rest),
            "uninstall" or "remove" or "delete" => Uninstall(rest),
            "start" => Sc($"start \"{GetName(rest)}\""),
            "stop" => Sc($"stop \"{GetName(rest)}\""),
            "status" => Sc($"query \"{GetName(rest)}\""),
            "help" or "--help" or "-h" => PrintHelp(),
            _ => Unknown(action),
        };
    }

    private static int Install(string[] args)
    {
        var name = GetName(args);
        var exe = Environment.ProcessPath
                  ?? throw new InvalidOperationException("Could not resolve the dfdb executable path.");

        var port = GetOpt(args, "--port") ?? "4300";
        var dataDir = GetOpt(args, "--data-dir") ?? @"C:\data\documentForge";
        var apiKey = GetOpt(args, "--api-key");
        var bindAll = args.Contains("--bind-all-interfaces");

        Directory.CreateDirectory(dataDir);

        // The command line the service runs. sc.exe wants the whole thing as one
        // quoted binPath value, with inner quotes doubled.
        var serveArgs = $"serve --port {port} --data-dir \"{dataDir}\"";
        if (bindAll) serveArgs += " --bind-all-interfaces";
        if (!string.IsNullOrWhiteSpace(apiKey)) serveArgs += $" --api-key \"{apiKey}\"";
        var binPath = $"\"{exe}\" {serveArgs}";

        // sc create: escape inner quotes by doubling them inside the outer binPath= "...".
        var escaped = binPath.Replace("\"", "\\\"");
        var createArgs =
            $"create \"{name}\" binPath= \"{escaped}\" start= auto DisplayName= \"{DisplayName}\"";

        Console.WriteLine($"Installing service '{name}' → {binPath}");
        var rc = Sc(createArgs);
        if (rc != 0) return rc;

        Sc($"description \"{name}\" \"DocumentForge JSON document database (dfdb serve on port {port}).\"");
        // Restart the service on failure (3 attempts, 5s apart).
        Sc($"failure \"{name}\" reset= 86400 actions= restart/5000/restart/5000/restart/5000");

        // Start it now (best-effort — if it can't start, e.g. the data dir is
        // locked by another process, it's still installed and starts on boot).
        Console.WriteLine("Starting service…");
        if (Sc($"start \"{name}\"") != 0)
            Console.WriteLine("Service installed but did not start now — it will start on next boot, or run `dfdb service start`.");
        else
            Console.WriteLine("Installed and started.");
        return 0;
    }

    private static int Uninstall(string[] args)
    {
        var name = GetName(args);
        // Best-effort stop first so delete succeeds immediately.
        Sc($"stop \"{name}\"", ignoreErrors: true);
        return Sc($"delete \"{name}\"");
    }

    /// <summary>Runs sc.exe with the given argument string. Returns its exit code.</summary>
    private static int Sc(string arguments, bool ignoreErrors = false)
    {
        var psi = new ProcessStartInfo("sc.exe", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();

        if (!string.IsNullOrWhiteSpace(stdout)) Console.Write(stdout);
        if (!ignoreErrors && p.ExitCode != 0)
        {
            if (!string.IsNullOrWhiteSpace(stderr)) Console.Error.Write(stderr);
            if (p.ExitCode == 5)
                Console.Error.WriteLine("Access denied — run this from an elevated (Administrator) prompt.");
        }
        return ignoreErrors ? 0 : p.ExitCode;
    }

    private static string GetName(string[] args) => GetOpt(args, "--name") ?? DefaultServiceName;

    private static string? GetOpt(string[] args, string flag)
    {
        var i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int Unknown(string action)
    {
        Console.Error.WriteLine($"Unknown service action: '{action}'.");
        PrintHelp();
        return 1;
    }

    private static int PrintHelp()
    {
        Console.WriteLine();
        Console.WriteLine("  dfdb service — run DocumentForge as a Windows service (needs admin)");
        Console.WriteLine();
        Console.WriteLine("    install   [--name N] [--port 4300] [--data-dir DIR] [--api-key KEY] [--bind-all-interfaces]");
        Console.WriteLine("    uninstall [--name N]");
        Console.WriteLine("    start     [--name N]");
        Console.WriteLine("    stop      [--name N]");
        Console.WriteLine("    status    [--name N]");
        Console.WriteLine();
        Console.WriteLine("  Default service name: DocumentForge   ·   default port: 4300");
        Console.WriteLine();
        Console.WriteLine("  EXAMPLE (elevated prompt):");
        Console.WriteLine("    dfdb service install --port 4300 --data-dir C:\\data\\documentForge --api-key s3cret");
        Console.WriteLine("    dfdb service start");
        Console.WriteLine();
        return 0;
    }
}
