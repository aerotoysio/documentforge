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
    public static void Map(IEndpointRouteBuilder app, ClusterRouter router, string configPath)
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
        // the JSON on disk.
        app.MapGet("/cluster/config", () =>
        {
            return Results.Text(router.Config.ToJson(), "application/json");
        });

        // GET /cluster/health — probe every ring in parallel; return a
        // status object the admin UI can render in real time.
        app.MapGet("/cluster/health", async () =>
        {
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
            version = "1.3.0",
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
}
