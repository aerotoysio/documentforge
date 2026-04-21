using System.Text.Json;
using DocumentForge.Core;
using DocumentForge.Document;
using DocumentForge.Engine;
using DocumentForge.Index;

namespace DocumentForge.Cli.Commands;

/// <summary>
/// `dfdb serve` — run this node's REST API.
/// Reads configuration from a node.json file, CLI flags, or env vars.
/// </summary>
public static class ServeCommand
{
    public static int Run(string[] args)
    {
        var config = NodeConfig.Load(args);

        var builder = WebApplication.CreateBuilder(args);

        var scheme = config.Security?.Tls is not null ? "https" : "http";
        var host = config.BindAllInterfaces ? "0.0.0.0" : "localhost";
        var bindUrl = $"{scheme}://{host}:{config.Port}";
        builder.WebHost.UseUrls(bindUrl);

        if (config.Security?.Tls is { } tls)
        {
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.ConfigureHttpsDefaults(https =>
                {
                    var pwd = tls.ResolveCertPassword();
                    https.ServerCertificate = pwd is null
                        ? new System.Security.Cryptography.X509Certificates.X509Certificate2(tls.CertPath)
                        : new System.Security.Cryptography.X509Certificates.X509Certificate2(tls.CertPath, pwd);
                });
            });
        }

        builder.Services.AddCors(opts => opts.AddDefaultPolicy(p =>
            p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
        var app = builder.Build();
        app.UseCors();

        Directory.CreateDirectory(config.DataDir);
        var dbPath = Path.Combine(config.DataDir, "data.dfdb");
        var db = DocumentForgeDb.OpenOrCreate(dbPath);

        // Optional bearer-token middleware
        if (!string.IsNullOrEmpty(config.Security?.ApiKey))
        {
            var expected = config.Security.ApiKey;
            app.Use(async (ctx, next) =>
            {
                if (ctx.Request.Method == "OPTIONS") { await next(); return; }
                var header = ctx.Request.Headers["Authorization"].ToString();
                if (!header.StartsWith("Bearer ", StringComparison.Ordinal) ||
                    !ConstantTimeEquals(header.Substring(7), expected))
                {
                    ctx.Response.StatusCode = 401;
                    await ctx.Response.WriteAsJsonAsync(new { error = "Unauthorized. Provide Authorization: Bearer <apiKey>." });
                    return;
                }
                await next();
            });
        }

        PrintBanner(config, bindUrl);
        MapEndpoints(app, db);

        app.Lifetime.ApplicationStopping.Register(() => db.Dispose());
        app.Run();
        return 0;
    }

    private static void PrintBanner(NodeConfig config, string bindUrl)
    {
        Console.WriteLine();
        Console.WriteLine("  \x1b[36mdfdb serve\x1b[0m");
        Console.WriteLine($"  node:      {config.NodeName}");
        Console.WriteLine($"  data dir:  {Path.GetFullPath(config.DataDir)}");
        Console.WriteLine($"  listening: {bindUrl}");
        Console.WriteLine($"  security:  API key {(string.IsNullOrEmpty(config.Security?.ApiKey) ? "\x1b[90mOFF (dev mode)\x1b[0m" : "\x1b[32mON\x1b[0m")}" +
                          $"  |  TLS {(config.Security?.Tls is null ? "\x1b[90mOFF\x1b[0m" : "\x1b[32mON\x1b[0m")}" +
                          $"  |  replication-secret {(string.IsNullOrEmpty(config.Security?.ReplicationSecret) ? "\x1b[90mOFF\x1b[0m" : "\x1b[32mON\x1b[0m")}");
        Console.WriteLine();
        Console.WriteLine("  endpoints: POST /query | GET /stats | GET /collections | POST /collections/{name}");
        Console.WriteLine("             DELETE /collections/{name}/{id} | GET /indexes/{collection}");
        Console.WriteLine("             POST /index | POST /seed");
        Console.WriteLine();
        Console.WriteLine($"  admin UI:  http://localhost:3000  (set NEXT_PUBLIC_DFDB_URL={bindUrl})");
        Console.WriteLine();
    }

    private static void MapEndpoints(WebApplication app, DocumentForgeDb db)
    {
        app.MapPost("/query", (QueryRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Sql))
                return Results.BadRequest(new { error = "Missing 'sql' field" });
            var result = db.Execute(request.Sql);
            if (!result.Success) return Results.BadRequest(new { error = result.Message });
            var docs = result.Documents.Select(d => JsonDocument.Parse(d.ToJson()).RootElement).ToList();
            return Results.Ok(new
            {
                success = true, count = result.Documents.Count, affected = result.AffectedCount,
                plan = result.QueryPlan, executionTimeMs = result.ExecutionTime.TotalMilliseconds,
                message = result.Message, documents = docs
            });
        });

        app.MapGet("/collections", () => Results.Ok(new { collections = db.GetCollectionNames() }));

        app.MapPost("/collections/{name}", async (string name, HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            var json = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(json)) return Results.BadRequest(new { error = "Empty body" });
            try
            {
                var id = db.Insert(name, json);
                return Results.Created($"/collections/{name}/{id}", new { success = true, id = id.ToString(), collection = name });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/collections/{name}", (string name, int? limit) =>
        {
            var sql = $"SELECT * FROM {name}";
            if (limit.HasValue) sql += $" LIMIT {limit.Value}";
            var result = db.Execute(sql);
            var docs = result.Documents.Select(d => JsonDocument.Parse(d.ToJson()).RootElement).ToList();
            return Results.Ok(new { collection = name, count = docs.Count, documents = docs });
        });

        app.MapDelete("/collections/{name}/{id}", (string name, string id) =>
        {
            var coll = db.GetCollection(name);
            if (coll is null) return Results.NotFound();
            var docId = new DocumentId(Guid.Parse(id));
            var doc = coll.FindById(docId);
            if (doc is null) return Results.NotFound();
            if (coll.Delete(docId)) db.NotifyDocDeleted(name, docId, doc);
            return Results.Ok(new { success = true });
        });

        app.MapGet("/indexes/{collection}", (string collection) =>
        {
            var indexes = db.GetIndexes(collection);
            return Results.Ok(new { collection,
                indexes = indexes.Select(i => new { name = i.Name, path = i.JsonPath, unique = i.IsUnique }) });
        });

        app.MapGet("/stats", () =>
        {
            db.Flush();
            var stats = db.GetStatistics();
            return Results.Ok(new
            {
                filePath = stats.FilePath,
                fileSizeMb = Math.Round(stats.FileSize / 1024.0 / 1024.0, 2),
                pageCount = stats.PageCount,
                cachedPages = stats.CachedPages,
                dirtyPages = stats.DirtyPages,
                collections = stats.Collections.Select(c => new
                {
                    name = c.Name,
                    documentCount = c.DocumentCount,
                    indexes = c.Indexes.Select(i => new
                    {
                        name = i.Name, path = i.JsonPath, entries = i.EntryCount, unique = i.IsUnique
                    })
                })
            });
        });

        app.MapPost("/index", (CreateIndexRequest request) =>
        {
            try
            {
                db.CreateIndex(request.Collection, request.Path, request.Name, request.Unique);
                var indexes = db.GetIndexes(request.Collection);
                return Results.Ok(new { success = true,
                    message = $"Index '{request.Name}' on {request.Collection}({request.Path})", totalIndexes = indexes.Count });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/seed", (SeedRequest? request) =>
        {
            int count = request?.Orders ?? 500;
            var rng = new Random(42);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < count; i++)
                db.Insert("orders", DocumentForge.AirlineDemo.SeedData.GenerateOrder(rng, i));
            for (int i = 0; i < 100; i++)
            {
                string[] airlines = { "AA", "UA", "DL", "BA", "LH" };
                var al = airlines[rng.Next(airlines.Length)];
                var fn = $"{al}{rng.Next(100, 9999)}";
                var date = DateTime.Today.AddDays(rng.Next(0, 30)).ToString("yyyy-MM-dd");
                db.Insert("flights", DocumentForge.AirlineDemo.SeedData.GenerateFlight(rng, fn, date));
            }
            try { db.CreateIndex("orders", "pnr", "idx_orders_pnr", unique: true); } catch { }
            try { db.CreateIndex("orders", "passenger.lastName", "idx_orders_lastname"); } catch { }
            try { db.CreateIndex("orders", "status", "idx_orders_status"); } catch { }
            try { db.CreateIndex("flights", "flightNumber", "idx_flights_number"); } catch { }
            try { db.CreateIndex("flights", "departureAirport", "idx_flights_departure"); } catch { }
            sw.Stop();
            return Results.Ok(new { success = true, ordersSeeded = count, flightsSeeded = 100,
                indexesCreated = 5, timeSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2) });
        });
    }

    private static bool ConstantTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}

// ---- DTOs ----
public record QueryRequest(string Sql);
public record SeedRequest(int? Orders);
public record CreateIndexRequest(string Collection, string Path, string? Name = null, bool Unique = false);
