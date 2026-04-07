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

        // Sensible default logging - quiet the per-request ASP.NET Core chatter
        // (4-5 lines per request swamps real signal at any traffic) but keep the
        // single 'Request finished ...' summary line. Override via env vars or
        // appsettings.json the standard ASP.NET way:
        //   Logging__LogLevel__Microsoft.AspNetCore=Information   (re-enable noise)
        //   Logging__LogLevel__Microsoft.AspNetCore=None          (silence entirely)
        builder.Logging
            .AddFilter("Microsoft.AspNetCore", Microsoft.Extensions.Logging.LogLevel.Warning)
            .AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", Microsoft.Extensions.Logging.LogLevel.Information);

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

        // Issue #66 Phase 1: every database now lives inside a registry, so
        // future phases (Studio "+" button, /databases CRUD, lazy open) can
        // grow without touching the route map. For Phase 1 the registry
        // holds exactly one entry — "default" — pointed at data.dfdb. All
        // existing flat routes resolve through registry.GetDefault(), so
        // single-DB clients written before this change see zero difference.
        var registry = new DatabaseRegistry();
        var db = registry.AttachOrCreate("default", dbPath);

        // Optional replication wiring - leader / follower / none
        var replicationSummary = StartReplication(db, config);

        // Optional bearer-token middleware.
        // /health is always public (load balancers, Render, Docker HEALTHCHECK all probe it
        // without any auth context - if we gate it, the platform thinks we're unhealthy).
        // CORS preflights (OPTIONS) are also exempt so browser callers work.
        if (!string.IsNullOrEmpty(config.Security?.ApiKey))
        {
            var expected = config.Security.ApiKey;
            app.Use(async (ctx, next) =>
            {
                if (ctx.Request.Method == "OPTIONS") { await next(); return; }

                var path = ctx.Request.Path.Value ?? "";
                if (string.Equals(path, "/health", StringComparison.OrdinalIgnoreCase))
                {
                    await next();
                    return;
                }

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

        PrintBanner(config, bindUrl, replicationSummary);
        MapEndpoints(app, db);
        MapAdminEndpoints(app, db, config);
        MapReplicationEndpoints(app, db, config);
        // Issue #66 Phase 2: multi-database admin verbs (list/attach/create/
        // detach/drop/set-default). The registry holds the engines; flat
        // data-plane routes still resolve to registry.GetDefault() so
        // single-DB clients see no change.
        DatabaseEndpoints.Map(app, registry, config.DataDir);

        // Dispose the registry on shutdown — it cascades to every attached
        // database, including the Phase 1 single "default" one.
        app.Lifetime.ApplicationStopping.Register(() => registry.Dispose());
        app.Run();
        return 0;
    }

    private static void PrintBanner(NodeConfig config, string bindUrl, string replicationSummary)
    {
        Console.WriteLine();
        Console.WriteLine("  \x1b[36mdfdb serve\x1b[0m");
        Console.WriteLine($"  node:       {config.NodeName}");
        Console.WriteLine($"  data dir:   {Path.GetFullPath(config.DataDir)}");
        Console.WriteLine($"  listening:  {bindUrl}");
        Console.WriteLine($"  security:   API key {(string.IsNullOrEmpty(config.Security?.ApiKey) ? "\x1b[90mOFF (dev mode)\x1b[0m" : "\x1b[32mON\x1b[0m")}" +
                          $"  |  TLS {(config.Security?.Tls is null ? "\x1b[90mOFF\x1b[0m" : "\x1b[32mON\x1b[0m")}" +
                          $"  |  replication-secret {(string.IsNullOrEmpty(config.Security?.ReplicationSecret) ? "\x1b[90mOFF\x1b[0m" : "\x1b[32mON\x1b[0m")}");
        Console.WriteLine($"  replication: {replicationSummary}");
        Console.WriteLine();
        Console.WriteLine("  app API:   POST /query | GET /stats | GET /collections | POST /collections/{name}");
        Console.WriteLine("             POST /collections/{name}/bulk");
        Console.WriteLine("             GET|DELETE|PUT /collections/{name}/{id}                (by internal _id)");
        Console.WriteLine("             GET|DELETE|PUT /collections/{name}/by/{field}/{value}  (by any field)");
        Console.WriteLine("             DELETE /collections/{name} | GET /indexes/{collection} | POST /index");
        Console.WriteLine("             POST /tx/batch                  (atomic multi-doc transaction)");
        Console.WriteLine("             POST /seed | GET /health | GET /version");
        Console.WriteLine("  databases: GET  /databases | POST /databases | DELETE /databases/{name}");
        Console.WriteLine("             POST /databases/{name}/set-default");
        Console.WriteLine("  admin:     POST /admin/flush | POST /admin/checkpoint | POST /admin/snapshot");
        Console.WriteLine("             POST /admin/compact/{collection}");
        Console.WriteLine("             POST /admin/rebuild-indexes/{collection}");
        Console.WriteLine("             POST /admin/rebuild-index/{collection}/{indexName}");
        Console.WriteLine("  replication:");
        Console.WriteLine("             GET  /replication/status");
        Console.WriteLine("             POST /replication/start-leader | /replication/start-follower");
        Console.WriteLine("             POST /replication/promote | /replication/read-only | /replication/read-write");
        Console.WriteLine("             POST /replication/auto-failover/enable | /disable");
        Console.WriteLine();
        Console.WriteLine($"  admin UI:  http://localhost:3000  (set NEXT_PUBLIC_DFDB_URL={bindUrl})");
        Console.WriteLine();
    }

    /// <summary>
    /// Stands up the replication listener / follower loop based on config.Replication.
    /// Returns a short human-readable summary for the startup banner.
    /// </summary>
    private static string StartReplication(DocumentForgeDb db, NodeConfig config)
    {
        var rep = config.Replication;
        var secret = config.Security?.ReplicationSecret;

        if (rep is null || string.IsNullOrEmpty(rep.Role))
            return "\x1b[90mOFF (single node)\x1b[0m";

        if (rep.IsLeader)
        {
            var port = rep.Port ?? 5500;
            if (port == config.Port)
                throw new InvalidOperationException(
                    $"Replication port ({port}) must differ from the HTTP port ({config.Port}).");

            db.StartLogicalReplicationServer(port, secret);
            return $"\x1b[32mLEADER\x1b[0m  listening on :{port}" +
                   (string.IsNullOrEmpty(secret) ? "" : "  (shared-secret required)");
        }

        if (rep.IsFollower)
        {
            if (string.IsNullOrEmpty(rep.LeaderHost) || rep.LeaderPort is null)
                throw new InvalidOperationException(
                    "Follower role requires --leader-host and --leader-port (or the matching node.json fields).");

            // Issue #51 — advertise our own HTTP base URL so the leader can
            // expose it in /replication/status. Falls back to a derived
            // host:port pair when Network.PublicBaseUrl isn't set.
            db.StartLogicalReplicationFollower(rep.LeaderHost!, rep.LeaderPort!.Value, secret, ownHttpEndpoint: config.ResolveHttpEndpoint());

            var detail = $"following {rep.LeaderHost}:{rep.LeaderPort}";

            if (rep.AutoFailover?.SilenceSeconds is int silenceSeconds && silenceSeconds > 0)
            {
                var newPort = rep.AutoFailover.NewLeaderPort ?? rep.LeaderPort!.Value;
                db.EnableAutoFailover(
                    newLeaderPort: newPort,
                    silenceTimeout: TimeSpan.FromSeconds(silenceSeconds),
                    onPromoted: p => Console.WriteLine($"[dfdb] auto-failover: promoted to leader on :{p}"));
                detail += $"  |  auto-failover after {silenceSeconds}s (new port :{newPort})";
            }

            return $"\x1b[32mFOLLOWER\x1b[0m  {detail}";
        }

        throw new InvalidOperationException(
            $"Unknown replication role '{rep.Role}'. Expected 'leader' or 'follower'.");
    }

    private static void MapEndpoints(WebApplication app, DocumentForgeDb db)
    {
        // POST /query - materialised JSON response (default, back-compat).
        // Pass ?stream=true OR Accept: application/x-ndjson for an NDJSON stream:
        // first line is the metadata envelope, then one document per line.
        app.MapPost("/query", async (QueryRequest request, HttpRequest httpReq, HttpResponse httpRes, bool? stream) =>
        {
            if (string.IsNullOrWhiteSpace(request.Sql))
                return Results.BadRequest(new { error = "Missing 'sql' field" });

            var wantsStream = stream == true
                || httpReq.Headers.Accept.ToString().Contains("application/x-ndjson", StringComparison.OrdinalIgnoreCase);

            var result = db.Execute(request.Sql);
            if (!result.Success) return Results.BadRequest(new { error = result.Message });

            if (!wantsStream)
            {
                var docs = result.Documents.Select(d => JsonDocument.Parse(d.ToJson()).RootElement).ToList();
                return Results.Ok(new
                {
                    success = true, count = result.Documents.Count, affected = result.AffectedCount,
                    plan = result.QueryPlan, executionTimeMs = result.ExecutionTime.TotalMilliseconds,
                    message = result.Message, documents = docs
                });
            }

            // NDJSON: line 1 = envelope, lines 2..N = one doc per line.
            httpRes.ContentType = "application/x-ndjson";
            httpRes.Headers["X-DFDB-Plan"] = result.QueryPlan;
            httpRes.Headers["X-DFDB-Count"] = result.Documents.Count.ToString();
            httpRes.Headers["X-DFDB-ExecutionMs"] = result.ExecutionTime.TotalMilliseconds.ToString("F2");

            var envelope = JsonSerializer.Serialize(new
            {
                kind = "meta",
                count = result.Documents.Count,
                affected = result.AffectedCount,
                plan = result.QueryPlan,
                executionTimeMs = result.ExecutionTime.TotalMilliseconds,
                message = result.Message
            });
            await httpRes.WriteAsync(envelope);
            await httpRes.WriteAsync("\n");
            await httpRes.Body.FlushAsync();

            foreach (var doc in result.Documents)
            {
                await httpRes.WriteAsync(doc.ToJson());
                await httpRes.WriteAsync("\n");
            }
            await httpRes.Body.FlushAsync();
            return Results.Empty;
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
            if (!Guid.TryParse(id, out var guid))
                return Results.BadRequest(new { error = "This endpoint expects DocumentForge's internal _id. To delete by a business key, use DELETE /collections/{name}/by/{field}/{value}." });
            var docId = new DocumentId(guid);
            var doc = coll.FindById(docId);
            if (doc is null) return Results.NotFound();
            if (coll.Delete(docId)) db.NotifyDocDeleted(name, docId, doc);
            return Results.Ok(new { success = true });
        });

        // Replace a document by internal _id. Body is the full new document
        // (the original _id is always preserved — you don't need to include it).
        //
        // Issue #18 — optimistic concurrency: if the request carries an
        // `If-Match: <etag>` header, ReplaceIfEtag is used and a mismatch
        // returns 412 Precondition Failed with the current ETag in both
        // the response header and body. Without the header it's
        // last-write-wins (back-compat with pre-#18 callers).
        app.MapPut("/collections/{name}/{id}", async (string name, string id, HttpRequest request) =>
        {
            if (!Guid.TryParse(id, out var guid))
                return Results.BadRequest(new { error = "This endpoint expects DocumentForge's internal _id. To update by a business key, use PUT /collections/{name}/by/{field}/{value}." });

            using var reader = new StreamReader(request.Body);
            var json = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(json)) return Results.BadRequest(new { error = "Empty body" });

            try
            {
                var docId = new DocumentId(guid);
                // ETag passed via the standard `If-Match` HTTP header. We
                // accept both quoted (`"<etag>"`, RFC 9110 strict) and
                // unquoted forms — clients hand-rolling the header often
                // forget the quotes, and for an opaque-string token there's
                // no semantic difference.
                var ifMatch = NormaliseIfMatch(request.Headers["If-Match"].ToString());

                if (!string.IsNullOrEmpty(ifMatch))
                {
                    string? newEtag;
                    try { newEtag = db.ReplaceIfEtag(name, docId, json, ifMatch); }
                    catch (EtagMismatchException ex)
                    {
                        return Results.Json(new
                        {
                            error = ex.Message,
                            expected = ex.ExpectedEtag,
                            actual = ex.ActualEtag,
                        }, statusCode: StatusCodes.Status412PreconditionFailed);
                    }
                    if (newEtag is null) return Results.NotFound();
                    return Results.Ok(new
                    {
                        success = true,
                        id = docId.ToString(),
                        collection = name,
                        etag = newEtag,
                    });
                }

                // Last-write-wins path.
                var ok = db.Replace(name, docId, json);
                if (!ok) return Results.NotFound();
                return Results.Ok(new { success = true, id = docId.ToString(), collection = name });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Replace by business key. Finds the first document where {field} = {value}
        // and replaces its content with the request body. Field name and value are
        // sanitised the same way as the GET/DELETE by-field routes.
        app.MapPut("/collections/{name}/by/{field}/{value}", async (string name, string field, string value, HttpRequest request) =>
        {
            if (!IsValidFieldPath(field))
                return Results.BadRequest(new { error = "Field name must match [a-zA-Z_][a-zA-Z0-9_.\\[\\]]*" });

            using var reader = new StreamReader(request.Body);
            var json = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(json)) return Results.BadRequest(new { error = "Empty body" });

            // Look up the doc id via SQL so indexes are honoured.
            var safeValue = value.Replace("'", "''");
            var lookupSql = $"SELECT * FROM {name} WHERE {field} = '{safeValue}' LIMIT 1";
            var found = db.Execute(lookupSql);
            if (!found.Success) return Results.BadRequest(new { error = found.Message });
            if (found.Documents.Count == 0) return Results.NotFound();

            try
            {
                var docId = found.Documents[0].GetId();
                var ok = db.Replace(name, docId, json);
                if (!ok) return Results.NotFound();
                return Results.Ok(new
                {
                    success = true,
                    id = docId.ToString(),
                    collection = name,
                    matchedBy = new { field, value },
                    plan = found.QueryPlan
                });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Find a single document by id. Issue #18: returns the current ETag
        // in an `ETag:` response header so clients can store it and use it
        // in a subsequent If-Match PUT.
        app.MapGet("/collections/{name}/{id}", (string name, string id, HttpResponse response) =>
        {
            var coll = db.GetCollection(name);
            if (coll is null) return Results.NotFound();
            if (!Guid.TryParse(id, out var guid))
                return Results.BadRequest(new { error = "This endpoint expects DocumentForge's internal _id (a Guid-formatted 16-byte value returned from POST /collections/{name}). To look up by your own business key, use GET /collections/{name}/by/{field}/{value}." });
            var doc = coll.FindById(new DocumentId(guid));
            if (doc is null) return Results.NotFound();

            var etag = doc.GetEtag();
            if (!string.IsNullOrEmpty(etag))
                response.Headers.ETag = $"\"{etag}\"";

            return Results.Ok(JsonDocument.Parse(doc.ToJson()).RootElement);
        });

        // Look up by any field - e.g. GET /collections/orders/by/pnr/ABC123
        // Uses an index when one exists on the field; falls back to collection scan otherwise.
        // The field name is validated against a whitelist to prevent SQL injection;
        // the value is escaped by doubling single quotes.
        app.MapGet("/collections/{name}/by/{field}/{value}", (string name, string field, string value) =>
        {
            if (!IsValidFieldPath(field))
                return Results.BadRequest(new { error = "Field name must match [a-zA-Z_][a-zA-Z0-9_.\\[\\]]*" });

            var coll = db.GetCollection(name);
            if (coll is null) return Results.NotFound();

            var safeValue = value.Replace("'", "''");
            var sql = $"SELECT * FROM {name} WHERE {field} = '{safeValue}' LIMIT 1";
            var r = db.Execute(sql);
            if (!r.Success) return Results.BadRequest(new { error = r.Message });
            if (r.Documents.Count == 0) return Results.NotFound();

            return Results.Ok(new
            {
                collection = name,
                plan = r.QueryPlan,
                executionTimeMs = r.ExecutionTime.TotalMilliseconds,
                document = JsonDocument.Parse(r.Documents[0].ToJson()).RootElement
            });
        });

        // Delete by any field - same semantics as the GET-by-field above.
        // Deletes at most the matching documents (no LIMIT clause means all matches).
        app.MapDelete("/collections/{name}/by/{field}/{value}", (string name, string field, string value) =>
        {
            if (!IsValidFieldPath(field))
                return Results.BadRequest(new { error = "Field name must match [a-zA-Z_][a-zA-Z0-9_.\\[\\]]*" });

            var coll = db.GetCollection(name);
            if (coll is null) return Results.NotFound();

            var safeValue = value.Replace("'", "''");
            var sql = $"DELETE FROM {name} WHERE {field} = '{safeValue}'";
            var r = db.Execute(sql);
            if (!r.Success) return Results.BadRequest(new { error = r.Message });

            return Results.Ok(new
            {
                success = true,
                collection = name,
                deletedCount = r.AffectedCount,
                plan = r.QueryPlan,
                executionTimeMs = r.ExecutionTime.TotalMilliseconds
            });
        });

        // Bulk insert: accepts a JSON array of documents.
        //
        // Response shape:
        //   {
        //     success: bool,         // true iff every doc landed (and !rolledBack)
        //     count: N,              // number of inserted ids
        //     ids: [docId, ...],     // _id strings, in input order, of every inserted doc
        //     errors: [{ index, error }, ...],   // per-doc failures (omitted if atomic+success)
        //     rolledBack: bool,      // true iff atomic=true and we rolled back on first failure
        //     atomic: bool, indexesRebuilt: N, indexesSkipped: bool, timeSeconds: f
        //   }
        //
        // Query flags:
        //   ?atomic=true       all-or-nothing - first error rolls back every previous insert
        //                      in the same lock window. Returns 400 with rolledBack=true.
        //   ?skipIndexes=true  cold-load mode: don't rebuild indexes after the batch.
        //                      You then MUST call POST /admin/rebuild-indexes/{name}
        //                      before any indexed query, or results will be wrong.
        app.MapPost("/collections/{name}/bulk", async (string name, HttpRequest request, bool? skipIndexes, bool? atomic) =>
        {
            using var reader = new StreamReader(request.Body);
            var json = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(json)) return Results.BadRequest(new { error = "Empty body" });

            List<BsonDocument>? docs;
            try
            {
                using var parsed = JsonDocument.Parse(json);
                if (parsed.RootElement.ValueKind != JsonValueKind.Array)
                    return Results.BadRequest(new { error = "Body must be a JSON array of documents." });

                docs = new List<BsonDocument>(parsed.RootElement.GetArrayLength());
                foreach (var el in parsed.RootElement.EnumerateArray())
                    docs.Add(BsonDocument.FromJson(el.GetRawText()));
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }

            var atomicMode = atomic == true;
            var skipped = skipIndexes == true;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = db.BulkInsertTracked(name, docs, atomic: atomicMode);

            // Rebuild indexes after the batch unless explicitly asked not to.
            // Skip the rebuild on a rolled-back atomic batch (nothing inserted to index).
            var indexesRebuilt = 0;
            if (!skipped && !result.RolledBack)
            {
                var existingIndexes = db.GetIndexes(name);
                if (existingIndexes.Count > 0)
                {
                    db.RebuildIndexes(name);
                    indexesRebuilt = existingIndexes.Count;
                }
            }

            sw.Stop();

            var responseBody = new
            {
                success = result.Errors.Count == 0 && !result.RolledBack,
                count = result.InsertedIds.Count,
                ids = result.InsertedIds.Select(i => i.ToString()).ToArray(),
                errors = result.Errors.Select(e => new { index = e.Index, error = e.Error }).ToArray(),
                rolledBack = result.RolledBack,
                atomic = atomicMode,
                indexesRebuilt,
                indexesSkipped = skipped,
                timeSeconds = Math.Round(sw.Elapsed.TotalSeconds, 3)
            };

            // Atomic mode that rolled back -> 400 (caller asked us to fail the whole thing).
            // Non-atomic with some errors -> still 200, the response carries the per-doc errors.
            return result.RolledBack
                ? Results.BadRequest(responseBody)
                : Results.Ok(responseBody);
        });

        // Drop an entire collection (destructive - requires explicit X-Confirm: true header)
        app.MapDelete("/collections/{name}", (string name, HttpRequest request) =>
        {
            if (request.Headers["X-Confirm"].ToString() != "true")
                return Results.BadRequest(new { error = "Destructive op. Include header 'X-Confirm: true' to proceed." });
            var dropped = db.DropCollection(name);
            return dropped
                ? Results.Ok(new { success = true, dropped = name })
                : Results.NotFound();
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

        // Atomic multi-document transaction. Body is a JSON array of ops:
        //   { "op": "insert",        "collection": "users", "doc": {...} }
        //   { "op": "replace",       "collection": "users", "id": "<guid>", "doc": {...} }
        //   { "op": "delete",        "collection": "users", "id": "<guid>" }
        //   { "op": "deleteByField", "collection": "users", "field": "email", "value": "a@b.com" }
        // The whole batch commits or none of it does. A unique-index conflict
        // anywhere in the batch (including conflicts the batch itself creates)
        // returns 400 with the failing op index. Cross-shard transactions and
        // imperative session-style transactions are tracked separately as
        // Phase 2/3 work.
        app.MapPost("/tx/batch", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(body))
                return Results.BadRequest(new { error = "Empty body" });

            JsonDocument? parsed;
            try { parsed = JsonDocument.Parse(body); }
            catch (Exception ex) { return Results.BadRequest(new { error = $"Invalid JSON: {ex.Message}" }); }

            using (parsed)
            {
                if (parsed.RootElement.ValueKind != JsonValueKind.Array)
                    return Results.BadRequest(new { error = "Body must be a JSON array of ops." });

                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var tx = db.BeginTransaction();
                var results = new List<object>();
                int opIndex = 0;

                try
                {
                    foreach (var el in parsed.RootElement.EnumerateArray())
                    {
                        var op = el.TryGetProperty("op", out var opEl) ? opEl.GetString() : null;
                        var collection = el.TryGetProperty("collection", out var cEl) ? cEl.GetString() : null;
                        if (string.IsNullOrEmpty(op) || string.IsNullOrEmpty(collection))
                        {
                            return Results.BadRequest(new { error = $"op[{opIndex}]: 'op' and 'collection' are required." });
                        }

                        switch (op)
                        {
                            case "insert":
                            {
                                if (!el.TryGetProperty("doc", out var docEl))
                                    return Results.BadRequest(new { error = $"op[{opIndex}] insert: 'doc' is required." });
                                var doc = BsonDocument.FromJson(docEl.GetRawText());
                                var id = tx.Insert(collection!, doc);
                                results.Add(new { op, collection, id = id.ToString() });
                                break;
                            }
                            case "replace":
                            {
                                if (!el.TryGetProperty("id", out var idEl) || !Guid.TryParse(idEl.GetString(), out var rguid))
                                    return Results.BadRequest(new { error = $"op[{opIndex}] replace: valid 'id' is required." });
                                if (!el.TryGetProperty("doc", out var docEl))
                                    return Results.BadRequest(new { error = $"op[{opIndex}] replace: 'doc' is required." });
                                var rid = new DocumentId(rguid);
                                var doc = BsonDocument.FromJson(docEl.GetRawText());
                                var ok = tx.Replace(collection!, rid, doc);
                                if (!ok)
                                    return Results.BadRequest(new { error = $"op[{opIndex}] replace: id '{rid}' not found in {collection}." });
                                results.Add(new { op, collection, id = rid.ToString() });
                                break;
                            }
                            case "delete":
                            {
                                if (!el.TryGetProperty("id", out var idEl) || !Guid.TryParse(idEl.GetString(), out var dguid))
                                    return Results.BadRequest(new { error = $"op[{opIndex}] delete: valid 'id' is required." });
                                var did = new DocumentId(dguid);
                                var ok = tx.Delete(collection!, did);
                                results.Add(new { op, collection, id = did.ToString(), deleted = ok });
                                break;
                            }
                            case "deleteByField":
                            {
                                var field = el.TryGetProperty("field", out var fEl) ? fEl.GetString() : null;
                                var value = el.TryGetProperty("value", out var vEl) ? vEl.GetString() : null;
                                if (string.IsNullOrEmpty(field) || value is null)
                                    return Results.BadRequest(new { error = $"op[{opIndex}] deleteByField: 'field' and 'value' are required." });
                                if (!IsValidFieldPath(field))
                                    return Results.BadRequest(new { error = $"op[{opIndex}] deleteByField: 'field' must match [a-zA-Z_][a-zA-Z0-9_.\\[\\]]*" });
                                int n = tx.DeleteByField(collection!, field, value);
                                results.Add(new { op, collection, field, value, deleted = n });
                                break;
                            }
                            default:
                                return Results.BadRequest(new { error = $"op[{opIndex}]: unknown op '{op}' (expected insert|replace|delete|deleteByField)." });
                        }
                        opIndex++;
                    }

                    tx.Commit();
                    sw.Stop();
                    return Results.Ok(new
                    {
                        success = true,
                        transactionId = tx.Id,
                        operationCount = results.Count,
                        results,
                        timeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 3)
                    });
                }
                catch (DocumentForgeException ex)
                {
                    sw.Stop();
                    // tx is auto-rolled back on dispose since Commit didn't reach.
                    return Results.BadRequest(new
                    {
                        success = false,
                        transactionId = tx.Id,
                        failedOpIndex = opIndex,
                        error = ex.Message,
                        rolledBack = true,
                        timeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 3)
                    });
                }
            }
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

    private static void MapAdminEndpoints(WebApplication app, DocumentForgeDb db, NodeConfig config)
    {
        // Liveness + identity
        app.MapGet("/health", () =>
        {
            // Issue #25: surface engine-wide health status. A Failed engine
            // returns 503 so load balancers / orchestrators (Render, k8s,
            // Docker HEALTHCHECK) take it out of rotation while the operator
            // restarts it (which runs recovery-log replay).
            bool healthy = db.HealthStatus == DocumentForge.Core.DatabaseHealthStatus.Healthy;
            var body = new
            {
                status = healthy ? "ok" : "degraded",
                node = config.NodeName,
                version = "0.1.0",
                readOnly = db.IsReadOnly,
                uptimeSeconds = Math.Round((DateTime.UtcNow - _startedAt).TotalSeconds, 1),
                health = healthy ? null : new
                {
                    state = db.HealthStatus.ToString(),
                    lastFailure = db.LastHealthFailure?.Message,
                },
            };
            return healthy
                ? Results.Ok(body)
                : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
        });

        // Build-identification info — returns the SHA, build timestamp, and
        // (when running in a container) the image identifier so deploy
        // verifiers can confirm what's running. See BuildInfo.cs for the
        // resolution chain (assembly metadata → env vars → fallbacks). Issue #36.
        app.MapGet("/version", () => Results.Ok(new
        {
            sha = BuildInfo.Sha,
            builtAt = BuildInfo.BuiltAtUtc,
            image = BuildInfo.Image,
            node = config.NodeName,
        }));

        // Force a cache flush (all dirty pages to disk, truncate recovery log)
        app.MapPost("/admin/flush", () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            db.Flush();
            sw.Stop();
            return Results.Ok(new { success = true, timeMs = sw.Elapsed.TotalMilliseconds });
        });

        // Synonym for flush - common DB ops term
        app.MapPost("/admin/checkpoint", () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            db.Checkpoint();
            sw.Stop();
            return Results.Ok(new { success = true, timeMs = sw.Elapsed.TotalMilliseconds });
        });

        // Take a consistent snapshot of the data file. Blocks writes briefly
        // (the duration of FlushAll + the file copy) and returns when the
        // snapshot is durable. The result file at targetPath is a
        // self-contained .dfdb that DocumentForgeDb.Open can load directly.
        //
        // For multi-GB datasets the copy dominates wall time; consider
        // running this against a follower instead so the leader's writes
        // aren't paused.
        app.MapPost("/admin/snapshot", (SnapshotRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.TargetPath))
                return Results.BadRequest(new { error = "targetPath is required." });
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                db.Snapshot(req.TargetPath);
                sw.Stop();
                long bytes = 0;
                try { bytes = new FileInfo(req.TargetPath).Length; } catch { }
                return Results.Ok(new
                {
                    success = true,
                    targetPath = req.TargetPath,
                    bytesCopied = bytes,
                    timeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1)
                });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Rebuild every index on a collection from scratch.
        // Needed after a bulk insert that used ?skipIndexes=true, or any time
        // an operator suspects index corruption.
        app.MapPost("/admin/rebuild-indexes/{collection}", (string collection) =>
        {
            try
            {
                var indexes = db.GetIndexes(collection);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                db.RebuildIndexes(collection);
                sw.Stop();
                return Results.Ok(new
                {
                    success = true,
                    collection,
                    indexesRebuilt = indexes.Count,
                    timeMs = sw.Elapsed.TotalMilliseconds
                });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Rebuild ONE named index. Surgical recovery for single-index drift
        // (e.g. issue #1 - a unique index out of sync with the collection).
        app.MapPost("/admin/rebuild-index/{collection}/{indexName}", (string collection, string indexName) =>
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var ok = db.RebuildIndex(collection, indexName);
                sw.Stop();
                if (!ok)
                    return Results.NotFound(new { error = $"Collection '{collection}' or index '{indexName}' not found." });
                return Results.Ok(new
                {
                    success = true,
                    collection,
                    indexName,
                    timeMs = sw.Elapsed.TotalMilliseconds
                });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Compact (reclaim space from deletes) on a single collection
        app.MapPost("/admin/compact/{collection}", (string collection) =>
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = db.Compact(collection);
                db.Flush();
                sw.Stop();
                return Results.Ok(new
                {
                    success = true,
                    collection,
                    pagesCompacted = r.PagesCompacted,
                    bytesReclaimed = r.BytesReclaimed,
                    timeMs = sw.Elapsed.TotalMilliseconds
                });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
    }

    private static void MapReplicationEndpoints(WebApplication app, DocumentForgeDb db, NodeConfig config)
    {
        // Current role + observability - safe for routine polling.
        //
        // The follower list and the follower's `leader.endpoint` are both
        // populated from socket-level state the engine already tracks
        // (FollowerCount, RemoteEndPoint, the configured _host:_port).
        // Admin UIs use these to draw the topology graph without operators
        // having to hand-tag each connection with a shard id (issue #12).
        //
        // Note: per-follower live ack tracking (`lastAckSeq`, `lagSeq`) is
        // intentionally not here. The wire protocol is fire-and-forget today
        // — we know each follower's seq AT HANDSHAKE only — and surfacing
        // a stale value as if it were live would be worse than omitting it.
        // Phase 2 of the multi-doc tx work (issue #13) is the natural place
        // to add ack framing; the lag fields land then.
        app.MapGet("/replication/status", () =>
        {
            var rep = config.Replication;
            var currentSeq = db.LeaderCurrentSeq;
            var followers = db.GetLogicalFollowers();
            return Results.Ok(new
            {
                node = config.NodeName,
                role = rep?.NormalizedRole ?? "none",
                readOnly = db.IsReadOnly,
                // Issue #51 — this node's own HTTP base URL. The admin-UI's
                // "Discover network" walk uses this to find peers without
                // having to guess the HTTP port from the replication
                // endpoint. Sourced from Network.PublicBaseUrl when set,
                // else derived from the bind address + port.
                httpEndpoint = config.ResolveHttpEndpoint(),
                leader = new
                {
                    currentSeq,
                    followerCount = db.GetLogicalFollowerCount(),
                    followers = followers.Select(f => new
                    {
                        endpoint = f.Endpoint,
                        // Issue #51 — null when the follower runs an older
                        // build that doesn't advertise its HTTP endpoint.
                        // Admin-UI falls back to its existing port-guess in
                        // that case.
                        httpEndpoint = f.HttpEndpoint,
                        connectedAt = f.ConnectedAtUtc,
                        handshakeSeq = f.HandshakeSeq,
                        // Worst-case lag: how far behind the follower was when it
                        // handshaked. If the link has stayed up, real lag is at
                        // most this; we can't claim less without acks.
                        worstCaseLagSeq = currentSeq > f.HandshakeSeq
                            ? currentSeq - f.HandshakeSeq : 0UL,
                    }).ToArray(),
                },
                follower = new
                {
                    lastAppliedSeq = db.FollowerLastSeq,
                    opsApplied = db.LogicallyReplicatedOps(),
                    gapsDetected = db.GapsDetected,
                    autoFailoverPromoted = db.WasAutoFailoverPromoted,
                    leader = db.LogicalFollowerLeaderEndpoint is null
                        ? null
                        // Issue #51 — leader.httpEndpoint is null until the
                        // bidirectional handshake exchange ships in a
                        // follow-up. For now the admin-UI falls back to
                        // probing the leader's HTTP port directly.
                        : (object)new { endpoint = db.LogicalFollowerLeaderEndpoint, httpEndpoint = (string?)null },
                }
            });
        });

        app.MapPost("/replication/start-leader", (StartLeaderRequest req) =>
        {
            try
            {
                db.StartLogicalReplicationServer(req.Port, req.SharedSecret ?? config.Security?.ReplicationSecret);
                return Results.Ok(new { success = true, role = "leader", port = req.Port });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/replication/start-follower", (StartFollowerRequest req) =>
        {
            try
            {
                db.StartLogicalReplicationFollower(req.Host, req.Port,
                    req.SharedSecret ?? config.Security?.ReplicationSecret,
                    ownHttpEndpoint: config.ResolveHttpEndpoint());
                return Results.Ok(new { success = true, role = "follower", leader = $"{req.Host}:{req.Port}" });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Manual promotion (planned handover step 2 or recovery after leader crash)
        app.MapPost("/replication/promote", (PromoteRequest req) =>
        {
            try
            {
                db.PromoteToLeader(req.Port);
                return Results.Ok(new { success = true, newRole = "leader", port = req.Port });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Read-only toggle (step 1 of planned handover - stops accepting writes)
        app.MapPost("/replication/read-only", () =>
        {
            db.EnterReadOnlyMode();
            return Results.Ok(new { success = true, readOnly = true });
        });

        app.MapPost("/replication/read-write", () =>
        {
            db.ExitReadOnlyMode();
            return Results.Ok(new { success = true, readOnly = false });
        });

        app.MapPost("/replication/auto-failover/enable", (AutoFailoverRequest req) =>
        {
            try
            {
                var silence = TimeSpan.FromSeconds(req.SilenceSeconds);
                db.EnableAutoFailover(req.NewLeaderPort, silence,
                    onPromoted: p => Console.WriteLine($"[dfdb] auto-failover: promoted to leader on :{p}"));
                return Results.Ok(new { success = true, silenceSeconds = req.SilenceSeconds, newLeaderPort = req.NewLeaderPort });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/replication/auto-failover/disable", () =>
        {
            db.DisableAutoFailover();
            return Results.Ok(new { success = true });
        });
    }

    private static readonly DateTime _startedAt = DateTime.UtcNow;

    private static bool ConstantTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    /// <summary>
    /// Whitelists the characters allowed in a JSON field path coming from a URL.
    /// Accepts names, dot notation, and bracket indices: passenger, passenger.lastName,
    /// flights[0].airport. Blocks everything that could form SQL syntax.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex _fieldPathRegex =
        new("^[a-zA-Z_][a-zA-Z0-9_.\\[\\]]*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool IsValidFieldPath(string field) =>
        !string.IsNullOrEmpty(field) && _fieldPathRegex.IsMatch(field);

    /// <summary>
    /// Normalise an HTTP If-Match header value. RFC 9110 says ETags are quoted
    /// strong tokens (<c>"abc"</c>) or weak (<c>W/"abc"</c>); in practice
    /// hand-rolled clients often send the raw token. We accept both, strip the
    /// quotes / weak prefix, and let <see cref="EtagMismatchException"/> compare
    /// the unwrapped opaque string.
    /// </summary>
    private static string NormaliseIfMatch(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = raw.Trim();
        // Strip the weak-validator prefix.
        if (s.StartsWith("W/", StringComparison.Ordinal)) s = s[2..];
        // Strip surrounding quotes if present.
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1];
        return s;
    }
}

// ---- DTOs ----
public record QueryRequest(string Sql);
public record SeedRequest(int? Orders);
public record CreateIndexRequest(string Collection, string Path, string? Name = null, bool Unique = false);
public record SnapshotRequest(string TargetPath);
public record StartLeaderRequest(int Port, string? SharedSecret = null);
public record StartFollowerRequest(string Host, int Port, string? SharedSecret = null);
public record PromoteRequest(int Port);
public record AutoFailoverRequest(int SilenceSeconds, int NewLeaderPort);
