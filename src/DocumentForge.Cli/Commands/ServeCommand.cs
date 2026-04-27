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

        app.Lifetime.ApplicationStopping.Register(() => db.Dispose());
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
        Console.WriteLine("             POST /seed | GET /health");
        Console.WriteLine("  admin:     POST /admin/flush | POST /admin/checkpoint");
        Console.WriteLine("             POST /admin/compact/{collection} | POST /admin/rebuild-indexes/{collection}");
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

            db.StartLogicalReplicationFollower(rep.LeaderHost!, rep.LeaderPort!.Value, secret);

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

            var existingId = found.Documents[0]["_id"];
            if (existingId.IsNull)
                return Results.BadRequest(new { error = "Matched document has no _id - cannot replace." });

            try
            {
                var docId = new DocumentId(Guid.Parse(existingId.ToJson().Trim('"')));
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

        // Find a single document by id
        app.MapGet("/collections/{name}/{id}", (string name, string id) =>
        {
            var coll = db.GetCollection(name);
            if (coll is null) return Results.NotFound();
            if (!Guid.TryParse(id, out var guid))
                return Results.BadRequest(new { error = "This endpoint expects DocumentForge's internal _id (a Guid-formatted 16-byte value returned from POST /collections/{name}). To look up by your own business key, use GET /collections/{name}/by/{field}/{value}." });
            var doc = coll.FindById(new DocumentId(guid));
            if (doc is null) return Results.NotFound();
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
        // By default, rebuilds indexes afterwards so queries see the new rows.
        // Pass ?skipIndexes=true for raw throughput in cold-load scenarios;
        // you are then responsible for calling POST /admin/rebuild-indexes/{name}
        // before any index-using query, or results will be wrong.
        app.MapPost("/collections/{name}/bulk", async (string name, HttpRequest request, bool? skipIndexes) =>
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

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var inserted = db.BulkInsert(name, docs);

            var indexesRebuilt = 0;
            var skipped = skipIndexes == true;
            if (!skipped)
            {
                var existingIndexes = db.GetIndexes(name);
                if (existingIndexes.Count > 0)
                {
                    db.RebuildIndexes(name);
                    indexesRebuilt = existingIndexes.Count;
                }
            }

            sw.Stop();
            return Results.Ok(new
            {
                success = true,
                inserted,
                indexesRebuilt,
                indexesSkipped = skipped,
                timeSeconds = Math.Round(sw.Elapsed.TotalSeconds, 3)
            });
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
        app.MapGet("/health", () => Results.Ok(new
        {
            status = "ok",
            node = config.NodeName,
            version = "0.1.0",
            readOnly = db.IsReadOnly,
            uptimeSeconds = Math.Round((DateTime.UtcNow - _startedAt).TotalSeconds, 1)
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
        // Current role + observability - safe for routine polling
        app.MapGet("/replication/status", () =>
        {
            var rep = config.Replication;
            return Results.Ok(new
            {
                node = config.NodeName,
                role = rep?.NormalizedRole ?? "none",
                readOnly = db.IsReadOnly,
                leader = new
                {
                    currentSeq = db.LeaderCurrentSeq,
                    followerCount = db.GetLogicalFollowerCount()
                },
                follower = new
                {
                    lastAppliedSeq = db.FollowerLastSeq,
                    opsApplied = db.LogicallyReplicatedOps(),
                    gapsDetected = db.GapsDetected,
                    autoFailoverPromoted = db.WasAutoFailoverPromoted
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
                    req.SharedSecret ?? config.Security?.ReplicationSecret);
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
}

// ---- DTOs ----
public record QueryRequest(string Sql);
public record SeedRequest(int? Orders);
public record CreateIndexRequest(string Collection, string Path, string? Name = null, bool Unique = false);
public record StartLeaderRequest(int Port, string? SharedSecret = null);
public record StartFollowerRequest(string Host, int Port, string? SharedSecret = null);
public record PromoteRequest(int Port);
public record AutoFailoverRequest(int SilenceSeconds, int NewLeaderPort);
