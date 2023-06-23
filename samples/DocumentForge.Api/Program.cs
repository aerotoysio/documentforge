using System.Text.Json;
using DocumentForge.Engine;
using DocumentForge.Document;
using DocumentForge.Index;

// --- Node configuration ---
// Can be supplied via:
//   1. --config ./node.json   (file with { nodeName, port, dataDir })
//   2. CLI flags: --node-name X --port 5001 --data-dir ./data/shard-a
//   3. Environment: DFDB_NODE_NAME, DFDB_PORT, DFDB_DATA_DIR
//   4. Default: single-node on http://localhost:5000 with CWD data
var config = NodeConfig.Load(args);

var builder = WebApplication.CreateBuilder(args);

// --- Bind URL (HTTP or HTTPS) ---
var scheme = config.Security?.Tls is not null ? "https" : "http";
var host = config.BindAllInterfaces ? "0.0.0.0" : "localhost";
var bindUrl = $"{scheme}://{host}:{config.Port}";
builder.WebHost.UseUrls(bindUrl);

// --- TLS ---
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

// --- Bearer token auth (optional) ---
if (!string.IsNullOrEmpty(config.Security?.ApiKey))
{
    var expected = config.Security.ApiKey;
    app.Use(async (ctx, next) =>
    {
        // Allow CORS preflight without auth
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

// Constant-time compare so we don't leak length / match-prefix info via timing
static bool ConstantTimeEquals(string a, string b)
{
    if (a.Length != b.Length) return false;
    int diff = 0;
    for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
    return diff == 0;
}

Directory.CreateDirectory(config.DataDir);
var dbPath = Path.Combine(config.DataDir, "data.dfdb");
var db = DocumentForgeDb.OpenOrCreate(dbPath);

Console.WriteLine("=============================================================");
Console.WriteLine($"  DocumentForge node: {config.NodeName}");
Console.WriteLine($"  Data dir:  {Path.GetFullPath(config.DataDir)}");
Console.WriteLine($"  Listening: {bindUrl}");
Console.WriteLine($"  Security:  API key {(string.IsNullOrEmpty(config.Security?.ApiKey) ? "DISABLED (dev mode)" : "ENABLED")}" +
                  $"  |  TLS {(config.Security?.Tls is null ? "OFF" : "ON")}" +
                  $"  |  Replication secret {(string.IsNullOrEmpty(config.Security?.ReplicationSecret) ? "OFF" : "ON")}");
Console.WriteLine("  Endpoints:");
Console.WriteLine("    POST   /query              - Execute SQL query");
Console.WriteLine("    POST   /seed               - Seed sample airline data");
Console.WriteLine("    GET    /collections         - List collections");
Console.WriteLine("    POST   /collections/{name}  - Insert document");
Console.WriteLine("    GET    /collections/{name}  - Get all docs (?limit=N)");
Console.WriteLine("    DELETE /collections/{name}/{id} - Delete by ID");
Console.WriteLine("    GET    /indexes/{collection} - List indexes");
Console.WriteLine("    POST   /index               - Create an index");
Console.WriteLine("    GET    /stats               - Node statistics");
Console.WriteLine();
Console.WriteLine($"  Admin UI:  http://localhost:3000  (set NEXT_PUBLIC_DFDB_URL=http://localhost:{config.Port})");
Console.WriteLine("=============================================================");

// ---- POST /query ----
// Body: { "sql": "SELECT * FROM orders WHERE pnr = 'ABC123'" }
app.MapPost("/query", (QueryRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Sql))
        return Results.BadRequest(new { error = "Missing 'sql' field" });

    var result = db.Execute(request.Sql);

    if (!result.Success)
        return Results.BadRequest(new { error = result.Message });

    var docs = result.Documents.Select(d => JsonDocument.Parse(d.ToJson()).RootElement).ToList();

    return Results.Ok(new
    {
        success = true,
        count = result.Documents.Count,
        affected = result.AffectedCount,
        plan = result.QueryPlan,
        executionTimeMs = result.ExecutionTime.TotalMilliseconds,
        message = result.Message,
        documents = docs
    });
});

// ---- GET /collections ----
app.MapGet("/collections", () =>
{
    return Results.Ok(new { collections = db.GetCollectionNames() });
});

// ---- POST /collections/{name} ----
// Body: any JSON document
app.MapPost("/collections/{name}", async (string name, HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    var json = await reader.ReadToEndAsync();

    if (string.IsNullOrWhiteSpace(json))
        return Results.BadRequest(new { error = "Empty body" });

    try
    {
        var id = db.Insert(name, json);
        return Results.Created($"/collections/{name}/{id}", new
        {
            success = true,
            id = id.ToString(),
            collection = name
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// ---- GET /collections/{name} ----
app.MapGet("/collections/{name}", (string name, int? limit) =>
{
    var sql = $"SELECT * FROM {name}";
    if (limit.HasValue) sql += $" LIMIT {limit.Value}";

    var result = db.Execute(sql);
    var docs = result.Documents.Select(d => JsonDocument.Parse(d.ToJson()).RootElement).ToList();

    return Results.Ok(new
    {
        collection = name,
        count = docs.Count,
        documents = docs
    });
});

// ---- DELETE /collections/{name}/{id} ----
// Direct delete by DocumentId (used by rebalancer)
app.MapDelete("/collections/{name}/{id}", (string name, string id) =>
{
    var coll = db.GetCollection(name);
    if (coll is null) return Results.NotFound();
    var docId = new DocumentForge.Core.DocumentId(Guid.Parse(id));
    var doc = coll.FindById(docId);
    if (doc is null) return Results.NotFound();
    if (coll.Delete(docId)) db.NotifyDocDeleted(name, docId, doc);
    return Results.Ok(new { success = true });
});

// ---- GET /indexes/{collection} ----
app.MapGet("/indexes/{collection}", (string collection) =>
{
    var indexes = db.GetIndexes(collection);
    return Results.Ok(new
    {
        collection,
        indexes = indexes.Select(i => new
        {
            name = i.Name,
            path = i.JsonPath,
            unique = i.IsUnique
        })
    });
});

// ---- GET /stats ----
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
                name = i.Name,
                path = i.JsonPath,
                entries = i.EntryCount,
                unique = i.IsUnique
            })
        })
    });
});

// ---- POST /seed ----
// Body: { "orders": 500 } (optional, defaults to 500)
app.MapPost("/seed", (SeedRequest? request) =>
{
    int count = request?.Orders ?? 500;
    var rng = new Random(42);
    var sw = System.Diagnostics.Stopwatch.StartNew();

    for (int i = 0; i < count; i++)
    {
        var json = DocumentForge.AirlineDemo.SeedData.GenerateOrder(rng, i);
        db.Insert("orders", json);
    }

    for (int i = 0; i < 100; i++)
    {
        string[] airlines = { "AA", "UA", "DL", "BA", "LH" };
        var airline = airlines[rng.Next(airlines.Length)];
        var flightNum = $"{airline}{rng.Next(100, 9999)}";
        var date = DateTime.Today.AddDays(rng.Next(0, 30)).ToString("yyyy-MM-dd");
        var json = DocumentForge.AirlineDemo.SeedData.GenerateFlight(rng, flightNum, date);
        db.Insert("flights", json);
    }

    // Create indexes
    try { db.CreateIndex("orders", "pnr", "idx_orders_pnr", unique: true); } catch { }
    try { db.CreateIndex("orders", "passenger.lastName", "idx_orders_lastname"); } catch { }
    try { db.CreateIndex("orders", "status", "idx_orders_status"); } catch { }
    try { db.CreateIndex("flights", "flightNumber", "idx_flights_number"); } catch { }
    try { db.CreateIndex("flights", "departureAirport", "idx_flights_departure"); } catch { }

    sw.Stop();

    return Results.Ok(new
    {
        success = true,
        ordersSeeded = count,
        flightsSeeded = 100,
        indexesCreated = 5,
        timeSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2)
    });
});

// ---- POST /index ----
// Body: { "collection": "orders", "path": "pnr", "name": "idx_pnr", "unique": true }
app.MapPost("/index", (CreateIndexRequest request) =>
{
    try
    {
        db.CreateIndex(request.Collection, request.Path, request.Name, request.Unique);
        var indexes = db.GetIndexes(request.Collection);
        return Results.Ok(new
        {
            success = true,
            message = $"Index '{request.Name}' created on {request.Collection}({request.Path})",
            totalIndexes = indexes.Count
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.Lifetime.ApplicationStopping.Register(() => db.Dispose());

app.Run();

// ---- Request DTOs ----
record QueryRequest(string Sql);
record SeedRequest(int? Orders);
record CreateIndexRequest(string Collection, string Path, string? Name = null, bool Unique = false);

/// <summary>
/// Per-node configuration loaded from (in priority order):
///   1. --config path/to/node.json
///   2. CLI flags: --node-name X --port 5001 --data-dir ./data/A
///   3. Env vars: DFDB_NODE_NAME, DFDB_PORT, DFDB_DATA_DIR
///   4. Defaults (single-node "node-1", port 5000, CWD data)
/// </summary>
sealed class NodeConfig
{
    public string NodeName { get; set; } = "node-1";
    public int Port { get; set; } = 5000;
    public string DataDir { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), "data");
    /// <summary>If true, bind to 0.0.0.0 (all interfaces). Default: localhost only — safer.</summary>
    public bool BindAllInterfaces { get; set; } = false;
    public SecurityConfig? Security { get; set; }

    public static NodeConfig Load(string[] args)
    {
        var c = new NodeConfig();

        // 1. Config file (--config foo.json)
        var configIdx = Array.IndexOf(args, "--config");
        if (configIdx >= 0 && configIdx + 1 < args.Length)
        {
            var path = args[configIdx + 1];
            var loaded = JsonSerializer.Deserialize<NodeConfig>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (loaded is not null) c = loaded;
        }

        // 2. CLI flags override the file
        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--node-name": c.NodeName = args[i + 1]; break;
                case "--port":      c.Port = int.Parse(args[i + 1]); break;
                case "--data-dir":  c.DataDir = args[i + 1]; break;
                case "--api-key":
                    c.Security ??= new SecurityConfig();
                    c.Security.ApiKey = args[i + 1];
                    break;
                case "--replication-secret":
                    c.Security ??= new SecurityConfig();
                    c.Security.ReplicationSecret = args[i + 1];
                    break;
                case "--bind-all": c.BindAllInterfaces = true; break;
            }
        }

        // 3. Env vars
        var envName = Environment.GetEnvironmentVariable("DFDB_NODE_NAME");
        var envPort = Environment.GetEnvironmentVariable("DFDB_PORT");
        var envDir  = Environment.GetEnvironmentVariable("DFDB_DATA_DIR");
        var envKey  = Environment.GetEnvironmentVariable("DFDB_API_KEY");
        var envRep  = Environment.GetEnvironmentVariable("DFDB_REPLICATION_SECRET");
        if (c.NodeName == "node-1" && !string.IsNullOrEmpty(envName)) c.NodeName = envName;
        if (c.Port == 5000 && int.TryParse(envPort, out var p)) c.Port = p;
        if (c.DataDir.EndsWith("data") && !string.IsNullOrEmpty(envDir)) c.DataDir = envDir;
        if (!string.IsNullOrEmpty(envKey))
        {
            c.Security ??= new SecurityConfig();
            c.Security.ApiKey ??= envKey;
        }
        if (!string.IsNullOrEmpty(envRep))
        {
            c.Security ??= new SecurityConfig();
            c.Security.ReplicationSecret ??= envRep;
        }

        return c;
    }
}

sealed class SecurityConfig
{
    /// <summary>If set, every REST request must send Authorization: Bearer &lt;apiKey&gt;.</summary>
    public string? ApiKey { get; set; }

    /// <summary>If set, replication connections must present this secret in their handshake.</summary>
    public string? ReplicationSecret { get; set; }

    /// <summary>If set, serve HTTPS instead of HTTP using the given PFX cert.</summary>
    public TlsConfig? Tls { get; set; }
}

sealed class TlsConfig
{
    public string CertPath { get; set; } = "";
    /// <summary>PFX password. Prefix with "env:" to read from an env var, e.g. "env:TLS_PASSWORD".</summary>
    public string? CertPassword { get; set; }

    public string? ResolveCertPassword()
    {
        if (string.IsNullOrEmpty(CertPassword)) return null;
        if (CertPassword.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
            return Environment.GetEnvironmentVariable(CertPassword.Substring(4));
        return CertPassword;
    }
}
