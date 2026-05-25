using System.Text.Json;
using DocumentForge.Cli.Auth;
using DocumentForge.Core;
using DocumentForge.Engine;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace DocumentForge.Cli.Commands;

/// <summary>
/// Issue #66 Phase 2: multi-database admin REST surface. Lives in its own
/// class (rather than buried in <see cref="ServeCommand"/>) so it can be
/// mounted on test fixtures that boot a minimal Kestrel host and assert
/// against the wire shape directly. The data plane keeps its current flat
/// routes; these endpoints just manage which databases are attached and
/// which one is the default that flat routes resolve to.
///
/// <para>
/// Wire shape mirrors the engine API verbatim — Attach for existing files,
/// Create for new ones, Detach for unregister-but-keep, Drop for delete-files.
/// The convenience verb <see cref="DatabaseRegistry.AttachOrCreate"/> powers
/// <c>POST /databases</c> by default so a Studio "+" button works without the
/// caller knowing whether the file pre-existed.
/// </para>
/// </summary>
public static class DatabaseEndpoints
{
    /// <summary>
    /// Mount the verbs on the supplied route builder. <paramref name="defaultDataDir"/>
    /// is where a path-less <c>POST /databases</c> drops the new <c>.dfdb</c>
    /// (defaults to <c>{dataDir}/{name}.dfdb</c>).
    /// </summary>
    public static void Map(IEndpointRouteBuilder app, DatabaseRegistry registry, string defaultDataDir, DatabaseCatalog? catalog = null)
    {
        // List every attached database. Stable shape across phases.
        // Admin-scoped: enumerating tenants would leak names to a
        // restricted key, which is what scoped keys exist to prevent.
        // The _system DB (Issue #73) is hidden from this view — it's
        // an implementation detail surfaced through /admin/* instead.
        app.MapGet("/databases", (HttpContext ctx) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            var entries = registry.List()
                .Where(e => !IsInternalDatabase(e.Name))
                .ToList();
            return Results.Ok(new
            {
                @default = registry.DefaultDatabaseName,
                count = entries.Count,
                databases = entries.Select(e => new
                {
                    name = e.Name,
                    filePath = e.FilePath,
                    isDefault = e.IsDefault,
                })
            });
        });

        // Issue #83 — list .dfdb files in (or under) the data dir that
        // AREN'T currently attached. Powers the Studio "Browse & attach"
        // panel: the operator drops files into a mounted volume, the UI
        // shows the leftovers, one click attaches them by path. Useful
        // even with the #82 auto-discover because:
        //   * recursive scan finds files in subfolders auto-discover skips
        //   * surfaces tombstoned files (so the operator can intentionally
        //     re-attach a previously-detached DB)
        //   * fills the gap when the file lives OUTSIDE data-dir entirely
        //     (handled via a follow-up "attach by absolute path" UX)
        app.MapGet("/databases/unattached", (HttpContext ctx, bool? recursive) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            if (!Directory.Exists(defaultDataDir))
                return Results.Ok(new { dataDir = defaultDataDir, count = 0, files = Array.Empty<object>() });

            // Names already taken by attached engines — case-insensitive
            // because that's how the registry stores them. Skip the
            // implicit ones (data.dfdb, _system.dfdb) since they're
            // never user-attached anyway.
            var attachedNames = registry.List()
                .Select(e => e.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var attachedPaths = registry.List()
                .Select(e => Path.GetFullPath(e.FilePath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var searchOpt = recursive == true
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;

            var rows = new List<object>();
            foreach (var path in Directory.EnumerateFiles(defaultDataDir, "*.dfdb", searchOpt))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (name == "data" || IsInternalDatabase(name)) continue;
                if (attachedPaths.Contains(Path.GetFullPath(path))) continue;

                var info = new FileInfo(path);
                rows.Add(new
                {
                    suggestedName = name,
                    nameConflict = attachedNames.Contains(name),
                    path = Path.GetFullPath(path),
                    sizeBytes = info.Length,
                    modifiedUtc = info.LastWriteTimeUtc.ToString("O"),
                });
            }
            return Results.Ok(new
            {
                dataDir = defaultDataDir,
                recursive = recursive == true,
                count = rows.Count,
                files = rows,
            });
        });

        // Issue #84 — runtime rescan + auto-attach. The boot-time
        // auto-discover in DatabaseCatalog.Bootstrap only fires once at
        // startup. After that, files dropped into data-dir are invisible
        // until the next restart. This endpoint is the "I dropped 10
        // tenant.dfdb files into the volume, attach them all now"
        // workflow — same logic as the bootstrap, just at runtime.
        //
        // Respects:
        //   * already-attached files (skipped, no double-attach error)
        //   * implicit names (data.dfdb / _system.dfdb)
        //   * Detach tombstones (catalog?.TombstonedNames())
        //
        // For one-off manual attach with a custom name, use the existing
        // /databases/unattached + per-row POST /databases flow.
        app.MapPost("/databases/discover", (HttpContext ctx, bool? recursive) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            if (!Directory.Exists(defaultDataDir))
                return Results.Ok(new
                {
                    dataDir = defaultDataDir,
                    discovered = 0,
                    skippedTombstoned = 0,
                    errors = Array.Empty<string>(),
                    attached = Array.Empty<object>(),
                });

            var attachedPaths = registry.List()
                .Select(e => Path.GetFullPath(e.FilePath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var tombstoned = catalog?.TombstonedNames()
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var searchOpt = recursive == true
                ? SearchOption.AllDirectories
                : SearchOption.TopDirectoryOnly;

            var attached = new List<object>();
            var errors = new List<string>();
            var skippedTombstoned = 0;

            foreach (var path in Directory.EnumerateFiles(defaultDataDir, "*.dfdb", searchOpt))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (name == "data" || IsInternalDatabase(name)) continue;
                var fullPath = Path.GetFullPath(path);
                if (attachedPaths.Contains(fullPath)) continue;
                if (tombstoned.Contains(name))
                {
                    skippedTombstoned++;
                    continue;
                }
                try
                {
                    registry.AttachOrCreate(name, fullPath);
                    catalog?.RecordAttach(name, fullPath);
                    attached.Add(new { name, path = fullPath });
                }
                catch (Exception ex)
                {
                    errors.Add($"'{name}' @ {path}: {ex.Message}");
                }
            }

            return Results.Ok(new
            {
                dataDir = defaultDataDir,
                recursive = recursive == true,
                discovered = attached.Count,
                skippedTombstoned,
                errors,
                attached,
            });
        });

        // Create-or-attach (Studio "+ Add Database").
        //   { "name": "acme" }                            -> {dataDir}/acme.dfdb, create-or-attach
        //   { "name": "acme", "path": "/abs/p.dfdb" }     -> use the supplied path, create-or-attach
        //   { "name": "acme", "createIfMissing": false }  -> Attach (existing file only)
        app.MapPost("/databases", (HttpContext ctx, CreateDatabaseRequest req) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            try
            {
                if (string.IsNullOrWhiteSpace(req.Name))
                    return Results.BadRequest(new { error = "Missing 'name' field." });

                var path = string.IsNullOrWhiteSpace(req.Path)
                    ? Path.Combine(defaultDataDir, $"{req.Name}.dfdb")
                    : req.Path!;

                var createIfMissing = req.CreateIfMissing ?? true;
                if (createIfMissing)
                    registry.AttachOrCreate(req.Name, path);
                else
                    registry.Attach(req.Name, path);

                // Issue #82 — persist the attach so the next restart
                // wires it back up automatically.
                catalog?.RecordAttach(req.Name, path);

                // Resolve the entry we just registered so we return the
                // canonical casing + canonical path back to the caller.
                var info = registry.List().First(e =>
                    string.Equals(e.Name, req.Name, StringComparison.OrdinalIgnoreCase));
                return Results.Created($"/databases/{info.Name}", new
                {
                    name = info.Name,
                    filePath = info.FilePath,
                    isDefault = info.IsDefault,
                });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (DocumentForgeException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Detach (default) or Drop (delete-files=true). 404 when not
        // registered so Studio's idempotent retries get a clear signal.
        app.MapDelete("/databases/{name}", (HttpContext ctx, string name, bool? deleteFiles) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            if (IsInternalDatabase(name))
                return Results.BadRequest(new { error = $"Database '{name}' is a system DB and cannot be detached or dropped." });
            try
            {
                var drop = deleteFiles == true;
                var removed = drop ? registry.Drop(name) : registry.Detach(name);
                if (!removed)
                    return Results.NotFound(new { error = $"Database '{name}' is not attached." });
                // Issue #82 — Detach is now PERSISTENT via a tombstone
                // record (auto-discover honours it on next boot, even
                // if the .dfdb file is still in data-dir). Drop wipes
                // the record entirely because the file is gone too.
                if (drop) catalog?.RecordDrop(name);
                else      catalog?.RecordDetach(name);
                return Results.Ok(new
                {
                    name,
                    action = drop ? "dropped" : "detached",
                    defaultAfter = registry.DefaultDatabaseName,
                });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Issue #85 — per-DB health & diagnostics. Surfaces enough state
        // for Studio to render an "is this DB OK?" panel without the
        // operator having to ssh in and `ls -la /data/*.dfdb*`.
        //
        // Returns sidecar-file sizes, lock holder metadata, collection
        // count, total document count, and a coarse recommendation
        // ("healthy" / "rebuild-catalog" / "recovery-pending" / etc).
        // Cheap — reads file sizes and one collection enumeration; no
        // page walks.
        app.MapGet("/databases/{name}/health", (HttpContext ctx, string name) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            var db = registry.TryGet(name);
            if (db is null)
                return Results.NotFound(new { error = $"Database '{name}' is not attached." });

            var filePath = db.FilePath;
            var dataInfo = new FileInfo(filePath);
            var recoveryInfo = new FileInfo(filePath + ".recovery");
            var walInfo = new FileInfo(filePath + ".wal");
            var lockInfo = new FileInfo(filePath + ".lock");
            var snapshotMarker = new FileInfo(filePath + ".snapshot.incoming.seq");

            // Engine-side state via GetStatistics — gives us doc counts
            // per collection from the catalog page metadata (no page
            // scans). Cheap; safe to call on every health request.
            var collectionNames = db.GetCollectionNames();
            long totalDocs = 0;
            try
            {
                var stats = db.GetStatistics();
                foreach (var c in stats.Collections) totalDocs += c.DocumentCount;
            }
            catch { /* engine in a bad state — fall back to 0 */ }

            // Heuristic recommendation. The bad-state we most want to
            // surface to the operator is "catalog empty but file has
            // way more bytes than an empty DB ever would" — that's the
            // classic Issue #85 scenario (catalog page corrupted, data
            // pages intact, rebuild can recover it).
            const long EmptyDbApproxBytes = 32 * 1024; // 2 pages of overhead
            string recommendation;
            string? recommendationDetail;
            if (collectionNames.Count == 0 && dataInfo.Length > EmptyDbApproxBytes)
            {
                recommendation = "rebuild-catalog";
                recommendationDetail =
                    $"Catalog reports 0 collections but the data file is {dataInfo.Length:N0} bytes " +
                    "(an empty database is ~32 KB). Document pages are probably intact but the " +
                    "catalog page that names them is empty or zeroed. POST /databases/" + name +
                    "/rebuild-catalog scans the data pages and reconstructs the catalog.";
            }
            else if (recoveryInfo.Exists && recoveryInfo.Length > 0)
            {
                recommendation = "recovery-pending";
                recommendationDetail =
                    $"Recovery log has {recoveryInfo.Length:N0} bytes of un-replayed page writes. " +
                    "These get applied automatically on the next service restart.";
            }
            else if (db.HealthStatus != Core.DatabaseHealthStatus.Healthy)
            {
                recommendation = "engine-degraded";
                recommendationDetail = $"Engine health status is {db.HealthStatus}.";
            }
            else
            {
                recommendation = "healthy";
                recommendationDetail = null;
            }

            return Results.Ok(new
            {
                database = name,
                filePath,
                attached = true,
                healthStatus = db.HealthStatus.ToString(),
                readOnly = db.IsReadOnly,
                collections = new
                {
                    count = collectionNames.Count,
                    names = collectionNames,
                    totalDocuments = totalDocs,
                },
                files = new
                {
                    dataSizeBytes = dataInfo.Length,
                    recoveryLogBytes = recoveryInfo.Exists ? recoveryInfo.Length : 0L,
                    walBytes = walInfo.Exists ? walInfo.Length : 0L,
                    snapshotMarkerPresent = snapshotMarker.Exists,
                },
                lockHolder = lockInfo.Exists ? TryReadLockHolder(lockInfo.FullName) : null,
                recommendation,
                recommendationDetail,
            });
        });

        // Phase 2 stopgap for "I'm working on DB X now" — Phase 4 will
        // replace this with auth-scoped Bearer tokens.
        app.MapPost("/databases/{name}/set-default", (HttpContext ctx, string name) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            try
            {
                registry.SetDefault(name);
                return Results.Ok(new { name, isDefault = true });
            }
            catch (DocumentForgeException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // ----------------------------------------------------------------
        // Issue #66 Phase 2.5 — per-DB replication. Each attached DB has
        // its own _logicalServer / _logicalFollower; until this lands the
        // flat /replication/* routes only operate on whichever DB was the
        // initial default at service startup.
        //
        // With these routes, "DB A leads, DB B follows A" works inside
        // one service:
        //   POST /db/A/replication/start-leader   {"port": 5500}
        //   POST /db/B/replication/start-follower {"host":"localhost","port":5500}
        // ----------------------------------------------------------------

        // Per-DB replication status — derived from live engine state, not
        // startup config. Returns 404 when the named DB isn't attached.
        // Replication status reveals followers, current seq, etc. —
        // admin-only since these wire topology details would help an
        // attacker reconstruct the cluster shape from a leaked
        // tenant-scoped key.
        app.MapGet("/db/{name}/replication/status", (HttpContext ctx, string name) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            var db = registry.TryGet(name);
            if (db is null)
                return Results.NotFound(new { error = $"Database '{name}' is not attached." });
            var currentSeq = db.LeaderCurrentSeq;
            var followers = db.GetLogicalFollowers();
            return Results.Ok(new
            {
                database = name,
                role = db.LogicalReplicationRole,
                readOnly = db.IsReadOnly,
                leader = new
                {
                    currentSeq,
                    // Phase 2.5 — the port this DB is leading on. Studio
                    // matches it against followers' leader-endpoint to
                    // draw intra-service replication edges.
                    port = db.LogicalLeaderPort,
                    followerCount = db.GetLogicalFollowerCount(),
                    followers = followers.Select(f => new
                    {
                        endpoint = f.Endpoint,
                        httpEndpoint = f.HttpEndpoint,
                        connectedAt = f.ConnectedAtUtc,
                        handshakeSeq = f.HandshakeSeq,
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
                        : (object)new { endpoint = db.LogicalFollowerLeaderEndpoint, httpEndpoint = (string?)null },
                }
            });
        });

        // Mount this DB as a replication leader on a dedicated TCP port.
        // The port has to differ from the service's HTTP port and from
        // any other DB's replication port in the same service.
        app.MapPost("/db/{name}/replication/start-leader", (HttpContext ctx, string name, StartLeaderBody body) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            var db = registry.TryGet(name);
            if (db is null)
                return Results.NotFound(new { error = $"Database '{name}' is not attached." });
            try
            {
                db.StartLogicalReplicationServer(body.Port, body.SharedSecret);
                return Results.Ok(new { database = name, role = "leader", port = body.Port });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Mount this DB as a follower of another DB (same service or remote).
        // First action on a fresh follower triggers a snapshot transfer from
        // the leader — any existing data in this DB is replaced.
        app.MapPost("/db/{name}/replication/start-follower", (HttpContext ctx, string name, StartFollowerBody body) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            var db = registry.TryGet(name);
            if (db is null)
                return Results.NotFound(new { error = $"Database '{name}' is not attached." });
            try
            {
                db.StartLogicalReplicationFollower(body.Host, body.Port, body.SharedSecret,
                    ownHttpEndpoint: null);
                return Results.Ok(new
                {
                    database = name,
                    role = "follower",
                    leader = $"{body.Host}:{body.Port}",
                });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Manual promotion — same semantics as the flat /replication/promote
        // but scoped to a named DB. Use during planned handover, or after a
        // leader-side crash, to flip a follower into leader on its own port.
        app.MapPost("/db/{name}/replication/promote", (HttpContext ctx, string name, PromoteBody body) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            var db = registry.TryGet(name);
            if (db is null)
                return Results.NotFound(new { error = $"Database '{name}' is not attached." });
            try
            {
                db.PromoteToLeader(body.Port);
                return Results.Ok(new { database = name, role = "leader", port = body.Port });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // ----------------------------------------------------------------
        // Issue #66 Phase 3a — scoped data plane. Studio (and any client)
        // can now run SQL against any attached DB without flipping the
        // service's default. The flat /query route still targets the
        // default DB; this route targets the named one. Same response
        // shape so the client-side handler stays simple.
        // ----------------------------------------------------------------
        app.MapPost("/db/{name}/query", (HttpContext ctx, string name, QueryBody body) =>
        {
            // Treat as read — SELECT is the dominant workload. INSERT
            // statements run through /collections POSTs which use write
            // scope; running them via /query bypasses that gate is a
            // known limitation called out in #72.
            if (ScopeCheck.RequireDbRead(ctx, name) is { } deny) return deny;
            var db = registry.TryGet(name);
            if (db is null)
                return Results.NotFound(new { error = $"Database '{name}' is not attached." });
            if (string.IsNullOrWhiteSpace(body.Sql))
                return Results.BadRequest(new { error = "Missing 'sql' field." });

            var result = db.Execute(body.Sql);
            if (!result.Success)
                return Results.BadRequest(new { error = result.Message });

            var docs = result.Documents
                .Select(d => JsonDocument.Parse(d.ToJson()).RootElement)
                .ToList();
            return Results.Ok(new
            {
                database = name,
                success = true,
                count = result.Documents.Count,
                affected = result.AffectedCount,
                plan = result.QueryPlan,
                executionTimeMs = result.ExecutionTime.TotalMilliseconds,
                message = result.Message,
                documents = docs,
            });
        });

        // Scoped collections list — Studio's Explorer pane uses this when
        // a tab is pinned to a non-default DB. The flat /collections
        // continues to return the default DB's collections for back-compat.
        app.MapGet("/db/{name}/collections", (HttpContext ctx, string name) =>
        {
            if (ScopeCheck.RequireDbRead(ctx, name) is { } deny) return deny;
            var db = registry.TryGet(name);
            if (db is null)
                return Results.NotFound(new { error = $"Database '{name}' is not attached." });
            return Results.Ok(new { database = name, collections = db.GetCollectionNames() });
        });

        // Scoped insert. Required for the Phase 6 router to forward
        // /collections/{c} POSTs against a multi-DB-backed ring leader.
        // Body shape mirrors the flat POST /collections/{name}: raw JSON
        // document. Returns the new doc id on success.
        app.MapPost("/db/{name}/collections/{collection}", async (HttpContext ctx, string name, string collection, HttpRequest request) =>
        {
            if (ScopeCheck.RequireDbWrite(ctx, name) is { } deny) return deny;
            var db = registry.TryGet(name);
            if (db is null)
                return Results.NotFound(new { error = $"Database '{name}' is not attached." });
            try
            {
                using var reader = new StreamReader(request.Body);
                var body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body))
                    return Results.BadRequest(new { error = "Empty body — JSON document required." });
                var id = db.Insert(collection, body);
                return Results.Ok(new { database = name, success = true, id = id.ToString(), collection });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
    }

    // Issue #73 — names beginning with an underscore are reserved for
    // internal use (currently just "_system"). The check is in one place
    // so adding "_audit" / "_metrics" / etc. later only touches here.
    private static bool IsInternalDatabase(string name) =>
        !string.IsNullOrEmpty(name) && name.StartsWith("_", StringComparison.Ordinal);

    /// <summary>Issue #85 — read the diagnostic JSON inside a .lock file
    /// so the health endpoint can show who last held the lock. Defensive
    /// — the file may be missing, empty, or corrupted (older versions of
    /// the engine, partial writes, etc).</summary>
    private static object? TryReadLockHolder(string lockPath)
    {
        try
        {
            using var fs = new FileStream(lockPath, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var json = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(json)) return null;
            using var doc = JsonDocument.Parse(json);
            return new
            {
                pid = doc.RootElement.TryGetProperty("Pid", out var p) ? (object)p.GetInt32() : null!,
                host = doc.RootElement.TryGetProperty("Host", out var h) ? h.GetString() : null,
                openedAtUtc = doc.RootElement.TryGetProperty("OpenedAtUtc", out var o) ? o.GetString() : null,
            };
        }
        catch
        {
            return null;
        }
    }
}

// Phase 3a — scoped query body. Same field as the flat QueryRequest in
// ServeCommand, kept distinct so the per-DB API can evolve independently.
public record QueryBody(string Sql);

// ----------------------------------------------------------------
// Issue #66 Phase 2.5: per-DB replication request bodies. Distinct
// from the flat StartLeaderRequest/etc. records in ServeCommand so
// the scoped surface can evolve independently (e.g. add a `force`
// flag for re-attaching a follower to a different leader).
// ----------------------------------------------------------------
public record StartLeaderBody(int Port, string? SharedSecret = null);
public record StartFollowerBody(string Host, int Port, string? SharedSecret = null);
public record PromoteBody(int Port);

/// <summary>
/// Issue #66: payload for POST /databases. Path optional — if absent we
/// derive <c>{dataDir}/{name}.dfdb</c>. CreateIfMissing defaults to true so
/// the Studio "+ Add" UX works without a file existing yet.
/// </summary>
public record CreateDatabaseRequest(string Name, string? Path = null, bool? CreateIfMissing = null);
