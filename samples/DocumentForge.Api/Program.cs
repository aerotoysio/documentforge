using System.Text.Json;
using DocumentForge.Engine;
using DocumentForge.Document;
using DocumentForge.Index;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(opts => opts.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
var app = builder.Build();
app.UseCors();

// Create/open the database
var dbPath = Path.Combine(Directory.GetCurrentDirectory(), "api_database.dfdb");
var db = DocumentForgeDb.OpenOrCreate(dbPath);

Console.WriteLine("=============================================================");
Console.WriteLine("  DocumentForge REST API");
Console.WriteLine($"  Database: {dbPath}");
Console.WriteLine($"  Listening on: http://localhost:5000   (change via --urls or ASPNETCORE_URLS)");
Console.WriteLine("  Endpoints:");
Console.WriteLine("    POST   /query              - Execute SQL query");
Console.WriteLine("    GET    /collections         - List collections");
Console.WriteLine("    POST   /collections/{name}  - Insert document (JSON body)");
Console.WriteLine("    GET    /collections/{name}  - Get all docs (with ?limit=N)");
Console.WriteLine("    GET    /stats               - Database statistics");
Console.WriteLine("    POST   /seed                - Seed sample airline data");
Console.WriteLine("    POST   /index               - Create an index");
Console.WriteLine("    DELETE /collections/{name}/{id} - Delete by ID");
Console.WriteLine("    GET    /indexes/{collection} - List indexes");
Console.WriteLine();
Console.WriteLine("  Admin UI:  http://localhost:3000  (run: cd admin-ui && npm run dev)");
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
