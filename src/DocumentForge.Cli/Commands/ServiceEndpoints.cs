using DocumentForge.Cli;
using DocumentForge.Cli.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DocumentForge.Cli.Commands;

/// <summary>
/// Issue #66 Phase 5 — REST surface for spawning local <c>dfdb serve</c>
/// children from Studio. The "+ Spawn local service" button in the
/// Connections page calls POST /services; the new HTTP base URL comes
/// back and Studio auto-registers it as a Connection. End-to-end the
/// user gets a fresh sibling service in ~1 second.
///
/// <para>
/// Endpoints:
/// <code>
///   GET    /services                 — list managed children
///   POST   /services { dataDir?, port?, nodeName? } — spawn
///   DELETE /services/{port}          — stop + reap
///   GET    /services/{port}/logs     — tail the child's combined stdout/stderr
/// </code>
/// </para>
///
/// <para>
/// Auth: same Bearer middleware as the rest of the service. Dev mode (no
/// API key configured) gates nothing — that's deliberate for "click to
/// spawn" UX. Production deployments should never enable this surface
/// without an admin-scoped token; Phase 4's auth scope work tightens it.
/// </para>
/// </summary>
public static class ServiceEndpoints
{
    public static void Map(IEndpointRouteBuilder app, ServiceManager manager, string defaultDataDirRoot)
    {
        // All /services + /routers verbs are admin-only — spawning a
        // child process is way more privileged than data-plane access.
        app.MapGet("/services", (HttpContext ctx) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            var entries = manager.List();
            return Results.Ok(new { count = entries.Count, services = entries });
        });

        app.MapPost("/services", (HttpContext ctx, SpawnServiceRequest req) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            try
            {
                // Default data dir: a sibling of the parent's, named by port
                // (or "child-{nodeName}" if supplied). Operators can override
                // by passing an explicit path.
                string dataDir;
                if (!string.IsNullOrWhiteSpace(req.DataDir))
                {
                    dataDir = req.DataDir!;
                }
                else
                {
                    var label = !string.IsNullOrWhiteSpace(req.NodeName)
                        ? req.NodeName!
                        : (req.Port is int p && p > 0 ? $"port-{p}" : $"node-{DateTime.UtcNow:HHmmss}");
                    dataDir = Path.Combine(defaultDataDirRoot, "services", label);
                }

                var service = manager.Spawn(new SpawnOptions
                {
                    DataDir = dataDir,
                    Port = req.Port ?? 0,
                    NodeName = req.NodeName,
                });

                return Results.Created($"/services/{service.Port}", new
                {
                    port = service.Port,
                    baseUrl = service.BaseUrl,
                    dataDir = service.DataDir,
                    nodeName = service.NodeName,
                    startedAt = service.StartedAt,
                    ready = service.Ready,
                    logPath = service.LogPath,
                });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapDelete("/services/{port:int}", (HttpContext ctx, int port) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            var stopped = manager.Stop(port);
            if (!stopped)
                return Results.NotFound(new { error = $"No managed service on port {port}." });
            return Results.Ok(new { port, stopped = true });
        });

        app.MapGet("/services/{port:int}/logs", (HttpContext ctx, int port, int? maxBytes) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            var log = manager.ReadLog(port, maxBytes ?? 16384);
            return Results.Text(log, "text/plain");
        });

        // Phase 6b — POST /routers spawns a `dfdb router` child against a
        // cluster.json the caller provides inline. The router runs on a
        // free port (or the supplied one) and gets tracked by the same
        // ServiceManager that handles serve children. Studio's "Spawn
        // router" button hits this; the new HTTP base URL comes back
        // and Studio auto-registers it as a Connection.
        app.MapPost("/routers", (HttpContext ctx, SpawnRouterRequest req) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            try
            {
                if (string.IsNullOrWhiteSpace(req.ClusterConfigJson))
                    return Results.BadRequest(new { error = "Missing 'clusterConfigJson' body field." });

                // Drop the config to a file under {dataDir}/routers/{name}/
                // so each router has a stable home for its log + config.
                var label = !string.IsNullOrWhiteSpace(req.Name) ? req.Name! : $"router-{DateTime.UtcNow:HHmmss}";
                var routerDir = Path.Combine(defaultDataDirRoot, "routers", label);
                Directory.CreateDirectory(routerDir);
                var configPath = Path.Combine(routerDir, "cluster.json");
                File.WriteAllText(configPath, req.ClusterConfigJson);

                var service = manager.SpawnRouter(new SpawnRouterOptions
                {
                    ConfigPath = configPath,
                    Port = req.Port ?? 0,
                    Name = req.Name,
                    LogDir = routerDir,
                });

                return Results.Created($"/services/{service.Port}", new
                {
                    kind = "router",
                    port = service.Port,
                    baseUrl = service.BaseUrl,
                    configPath,
                    name = service.NodeName,
                    startedAt = service.StartedAt,
                    ready = service.Ready,
                    logPath = service.LogPath,
                });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
    }
}

// Phase 6b — POST /routers request body. The cluster config is
// inlined as a JSON string so Studio's editor can post the textarea
// contents verbatim without writing it to disk first.
public record SpawnRouterRequest(string ClusterConfigJson, int? Port = null, string? Name = null);

// Phase 5 spawn payload. All optional — POST /services with {} is valid
// and gives you a child on a free port with a derived data dir.
public record SpawnServiceRequest(string? DataDir = null, int? Port = null, string? NodeName = null);
