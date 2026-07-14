using System.Text.Json;
using DocumentForge.Cli.Router;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DocumentForge.Cli.Commands;

/// <summary>
/// REST surface for the <c>dfdb router</c> proxy. Endpoints mirror the
/// subset of <c>dfdb serve</c>'s surface that clients use directly so
/// applications can swap their connection string from
/// <c>http://service:5000</c> to <c>http://router:5000</c> with no
/// code changes for insert + query workloads.
/// </summary>
public static class RouterEndpoints
{
    /// <param name="routerKey">Issue #134 — admin Bearer key for the router's
    /// cluster-admin surface. When set, <c>GET/PUT /cluster/config</c> and
    /// <c>GET /cluster/health</c> require it. When null, those GETs stay open
    /// (back-compat) but the mutating <c>PUT</c> is restricted to loopback so
    /// an unauthenticated router can't be reconfigured remotely.</param>
    public static void Map(IEndpointRouteBuilder app, ClusterRouter router, string configPath, string? routerKey = null)
    {
        // POST /collections/{name} — route an insert per strategy.
        app.MapPost("/collections/{name}", async (string name, HttpRequest request) =>
        {
            try
            {
                using var reader = new StreamReader(request.Body);
                var body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body))
                    return Results.BadRequest(new { error = "Empty body — JSON document required." });
                using var doc = JsonDocument.Parse(body);
                var upstream = await router.InsertAsync(name, doc.RootElement);
                var status = (int)upstream.StatusCode;
                var responseBody = await upstream.Content.ReadAsStringAsync();
                return Results.Content(responseBody, "application/json", statusCode: status);
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // POST /query — fan-out for hash collections, single ring otherwise.
        app.MapPost("/query", async (HttpRequest request) =>
        {
            try
            {
                using var reader = new StreamReader(request.Body);
                var body = await reader.ReadToEndAsync();
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("sql", out var sqlEl) ||
                    sqlEl.ValueKind != JsonValueKind.String)
                    return Results.BadRequest(new { error = "Missing 'sql' field." });
                var sql = sqlEl.GetString()!;
                var result = await router.QueryAsync(sql);
                return Results.Text(result.GetRawText(), "application/json");
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // GET /cluster/config — surface the loaded config so Studio can
        // render the topology + collection strategies without re-parsing
        // the JSON on disk. Gated behind the router admin key when one is set.
        app.MapGet("/cluster/config", (HttpContext ctx) =>
        {
            if (GateClusterAdmin(ctx, routerKey, mutating: false) is { } deny) return deny;
            return Results.Text(router.Config.ToJson(), "application/json");
        });

        // PUT /cluster/config — issue #134. Push a validated config to a live
        // router and hot-swap the ring atomically, then persist it back to the
        // --config file so a restart matches the live state. Mutating, so it is
        // always protected: it requires the router admin key, or (when none is
        // configured) a loopback connection.
        app.MapPut("/cluster/config", async (HttpContext ctx) =>
        {
            if (GateClusterAdmin(ctx, routerKey, mutating: true) is { } deny) return deny;

            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(body))
                return Results.BadRequest(new { error = "Empty body — a cluster config JSON is required." });

            ClusterConfig incoming;
            try
            {
                // FromJson runs Validate() (shards non-empty, hash collections
                // have a shardKeyPath, endpoints parse) — a bad config is a 400,
                // and the live ring is never touched.
                incoming = ClusterConfig.FromJson(body);
            }
            catch (Exception ex) { return Results.BadRequest(new { error = $"Invalid cluster config: {ex.Message}" }); }

            int newVersion;
            try
            {
                // Atomic swap + version bump. Note: the router config schema has
                // no in-flight migration block today, so there is nothing to
                // guard against here (open-question #2). If one is added later,
                // this is where a 409 "migration in progress" check belongs.
                newVersion = router.ApplyConfig(incoming);
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

            // Persist the applied config (with the bumped version) so a restart
            // is consistent with the live state. A persistence failure doesn't
            // roll back the live swap — surface it so the operator re-saves.
            string? persistWarning = null;
            try { await File.WriteAllTextAsync(configPath, router.Config.ToJson()); }
            catch (Exception ex) { persistWarning = $"Config applied live but could not be written to '{configPath}': {ex.Message}"; }

            return Results.Ok(new
            {
                applied = true,
                version = newVersion,
                shards = router.Config.Shards.Count,
                collections = router.Config.Collections.Count,
                persisted = persistWarning is null,
                warning = persistWarning,
            });
        });

        // GET /cluster/health — probe every ring in parallel; return a
        // status object the admin UI can render in real time. Gated behind
        // the router admin key when one is set.
        app.MapGet("/cluster/health", async (HttpContext ctx) =>
        {
            if (GateClusterAdmin(ctx, routerKey, mutating: false) is { } deny) return deny;
            var checks = await Task.WhenAll(router.Rings.Select(async kv => new
            {
                shard = kv.Key,
                baseUrl = kv.Value.BaseUrl,
                database = kv.Value.Endpoint.Database,
                healthy = await kv.Value.IsHealthyAsync(),
            }));
            var anyUnhealthy = checks.Any(c => !c.healthy);
            return Results.Json(new
            {
                status = anyUnhealthy ? "degraded" : "ok",
                shards = checks,
            });
        });

        // GET /health — root-level health for load balancers + Studio's
        // connection picker. Matches the dfdb serve shape on purpose.
        app.MapGet("/health", () => Results.Ok(new
        {
            status = "ok",
            node = "router",
            role = "router",
            version = DocumentForge.Engine.BuildInfo.Version,
            configPath = Path.GetFullPath(configPath),
            shards = router.Config.Shards.Count,
            collections = router.Config.Collections.Count,
        }));

        // GET /collections — list every distinct collection declared in
        // the cluster config. Studio's Explorer pane uses this when
        // pointed at a router endpoint.
        app.MapGet("/collections", () =>
        {
            return Results.Ok(new
            {
                collections = router.Config.Collections.Select(c => c.Name).ToArray(),
            });
        });
    }

    /// <summary>
    /// Issue #134 — access control for the router's cluster-admin surface.
    /// Returns null when the request is allowed, or a 401/403 result when not.
    /// <list type="bullet">
    ///   <item>Router key configured → a matching <c>Authorization: Bearer</c>
    ///   is required (both GET and PUT).</item>
    ///   <item>No router key → GETs are open (back-compat); a mutating request
    ///   is allowed only from a loopback client, so an unauthenticated router
    ///   can never be reconfigured remotely.</item>
    /// </list>
    /// </summary>
    private static IResult? GateClusterAdmin(HttpContext ctx, string? routerKey, bool mutating)
    {
        if (!string.IsNullOrEmpty(routerKey))
        {
            var presented = ExtractBearer(ctx.Request);
            if (presented is null || !FixedTimeEquals(presented, routerKey))
                return Results.Json(
                    new { error = "Router admin key required (Authorization: Bearer <key>)." },
                    statusCode: StatusCodes.Status401Unauthorized);
            return null;
        }

        // No key configured: reads stay open; writes must be local.
        if (mutating)
        {
            var ip = ctx.Connection.RemoteIpAddress;
            if (ip is null || !System.Net.IPAddress.IsLoopback(ip))
                return Results.Json(
                    new { error = "PUT /cluster/config requires a router admin key (start the router with --router-key) or a loopback connection." },
                    statusCode: StatusCodes.Status403Forbidden);
        }
        return null;
    }

    private static string? ExtractBearer(HttpRequest request)
    {
        var raw = request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        const string prefix = "Bearer ";
        return raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? raw[prefix.Length..].Trim()
            : null;
    }

    private static bool FixedTimeEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a), System.Text.Encoding.UTF8.GetBytes(b));
}
