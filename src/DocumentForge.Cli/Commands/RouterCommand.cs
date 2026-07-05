using System.Text.Json;
using DocumentForge.Cli.Router;

namespace DocumentForge.Cli.Commands;

/// <summary>
/// Issue #66 Phase 6 — <c>dfdb router</c> CLI verb. Boots the
/// stateless routing proxy described in <see cref="Router"/>.
///
/// <para>
/// Usage:
/// <code>
///   dfdb router --config cluster.json [--port 5000]
/// </code>
/// </para>
/// </summary>
public static class RouterCommand
{
    public static int Run(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintHelp();
            return 0;
        }

        var configPath = GetFlag(args, "--config") ?? GetFlag(args, "-c");
        if (string.IsNullOrEmpty(configPath))
        {
            Console.Error.WriteLine("ERROR: --config <cluster.json> is required.");
            PrintHelp();
            return 1;
        }
        if (!File.Exists(configPath))
        {
            Console.Error.WriteLine($"ERROR: cluster config not found at '{configPath}'.");
            return 1;
        }

        var config = ClusterConfig.FromJson(File.ReadAllText(configPath));

        // CLI --port takes precedence over config.router.port; both must
        // resolve to *something* or we can't bind Kestrel.
        var portFlag = GetFlag(args, "--port") ?? GetFlag(args, "-p");
        int port = portFlag is not null && int.TryParse(portFlag, out var p)
            ? p
            : (config.Router.Port ?? 5000);

        var bindAll = args.Contains("--bind-all");
        var host = bindAll ? "0.0.0.0" : "localhost";
        var bindUrl = $"http://{host}:{port}";

        // Issue #134 — admin key for the router's cluster-admin surface
        // (GET/PUT /cluster/config, GET /cluster/health). Flag wins over env.
        var routerKey = GetFlag(args, "--router-key")
            ?? Environment.GetEnvironmentVariable("DFDB_ROUTER_KEY");
        if (string.IsNullOrWhiteSpace(routerKey)) routerKey = null;

        var router = new ClusterRouter(config);

        // Build the Kestrel host. Same defaults as `dfdb serve` —
        // quiet ASP.NET request chatter, CORS-open for browser callers.
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging
            .AddFilter("Microsoft.AspNetCore", Microsoft.Extensions.Logging.LogLevel.Warning);
        builder.WebHost.UseUrls(bindUrl);
        builder.Services.AddCors(opts => opts.AddDefaultPolicy(p =>
            p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
        var app = builder.Build();
        app.UseCors();

        PrintBanner(config, configPath, bindUrl, routerKey is not null);
        RouterEndpoints.Map(app, router, configPath, routerKey);

        app.Lifetime.ApplicationStopping.Register(() => router.Dispose());
        app.Run();
        return 0;
    }

    private static void PrintBanner(ClusterConfig config, string configPath, string bindUrl, bool authEnabled)
    {
        Console.WriteLine();
        Console.WriteLine("  \x1b[36mdfdb router\x1b[0m");
        Console.WriteLine($"  config:     {Path.GetFullPath(configPath)}  (v{config.Version})");
        Console.WriteLine($"  listening:  {bindUrl}");
        Console.WriteLine($"  admin auth: {(authEnabled ? "Bearer key required" : "none (PUT /cluster/config is loopback-only)")}");
        Console.WriteLine($"  shards:     {config.Shards.Count}  ({string.Join(", ", config.Shards.Select(s => s.Name))})");
        Console.WriteLine($"  collections:{config.Collections.Count}  (" +
            string.Join(", ", config.Collections.Select(c => $"{c.Name}:{c.Strategy}")) + ")");
        Console.WriteLine();
        Console.WriteLine("  endpoints:  POST /collections/{name}   — insert (routed per strategy)");
        Console.WriteLine("              POST /query                — SELECT (fan-out for hash, single for replicated)");
        Console.WriteLine("              GET  /cluster/config       — read the active config");
        Console.WriteLine("              PUT  /cluster/config       — hot-swap the config live (admin)");
        Console.WriteLine("              GET  /cluster/health       — per-ring health probe");
        Console.WriteLine();
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Usage: dfdb router --config <path/to/cluster.json> [--port N] [--bind-all] [--router-key <key>]");
        Console.WriteLine();
        Console.WriteLine("Boots a stateless proxy that fronts an entire DocumentForge cluster as");
        Console.WriteLine("one HTTP endpoint. The cluster.json declares shard rings + collections;");
        Console.WriteLine("the router applies hash / replicated routing on every request.");
        Console.WriteLine();
        Console.WriteLine("  --router-key <key>   Require this Bearer key on the cluster-admin");
        Console.WriteLine("                       endpoints (GET/PUT /cluster/config, /cluster/health).");
        Console.WriteLine("                       Also settable via DFDB_ROUTER_KEY. When unset, the");
        Console.WriteLine("                       mutating PUT is restricted to loopback callers.");
    }

    private static string? GetFlag(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
