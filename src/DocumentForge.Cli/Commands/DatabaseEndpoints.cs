using System.Text.Json;
using DocumentForge.Cli.Auth;
using DocumentForge.Core;
using DocumentForge.Engine;
using DocumentForge.Storage;
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
    public static void Map(IEndpointRouteBuilder app, DatabaseRegistry registry, string defaultDataDir, DatabaseCatalog? catalog = null, BackupManager? backupManager = null, WalArchiver? walArchiver = null, PointInTimeRestore? pitr = null)
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

        // ----------------------------------------------------------------
        // Issue #87 — backup + recovery. Per-DB snapshots, all-at-once
        // backups, list/restore/delete, plus settings (backup dir +
        // retention count) persisted to _system.backup_config.
        // ----------------------------------------------------------------

        app.MapGet("/admin/backup/config", (HttpContext ctx) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            if (backupManager is null)
                return Results.NotFound(new { error = "Backup manager not configured." });
            var cfg = backupManager.GetConfig();
            return Results.Ok(new
            {
                backupDir = backupManager.EffectiveBackupDir,
                backupDirConfigured = cfg?.BackupDir,
                retentionCount = backupManager.EffectiveRetentionCount,
                scheduleCron = cfg?.ScheduleCron,
            });
        });

        app.MapPut("/admin/backup/config", (HttpContext ctx, BackupConfigBody body) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            if (backupManager is null)
                return Results.NotFound(new { error = "Backup manager not configured." });
            try
            {
                backupManager.SaveConfig(new BackupConfig
                {
                    BackupDir = string.IsNullOrWhiteSpace(body.BackupDir) ? null : body.BackupDir,
                    RetentionCount = body.RetentionCount ?? 10,
                    ScheduleCron = string.IsNullOrWhiteSpace(body.ScheduleCron) ? null : body.ScheduleCron,
                });
                return Results.Ok(new { saved = true });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/databases/{name}/backup", (HttpContext ctx, string name) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            if (backupManager is null)
                return Results.NotFound(new { error = "Backup manager not configured." });
            try
            {
                var record = backupManager.BackupOne(name);
                return Results.Created($"/admin/backups/{record.Id}", BackupToWire(record));
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/admin/backup/all", (HttpContext ctx) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            if (backupManager is null)
                return Results.NotFound(new { error = "Backup manager not configured." });
            var result = backupManager.BackupAll();
            return Results.Ok(new
            {
                count = result.Backups.Count,
                errors = result.Errors,
                backups = result.Backups.Select(BackupToWire).ToList(),
            });
        });

        app.MapGet("/admin/backups", (HttpContext ctx) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            if (backupManager is null)
                return Results.NotFound(new { error = "Backup manager not configured." });
            var rows = backupManager.ListBackups();
            return Results.Ok(new { count = rows.Count, backups = rows.Select(BackupToWire).ToList() });
        });

        app.MapGet("/databases/{name}/backups", (HttpContext ctx, string name) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            if (backupManager is null)
                return Results.NotFound(new { error = "Backup manager not configured." });
            var rows = backupManager.ListBackups(name);
            return Results.Ok(new { database = name, count = rows.Count, backups = rows.Select(BackupToWire).ToList() });
        });

        app.MapDelete("/admin/backups/{backupId}", (HttpContext ctx, string backupId) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            if (backupManager is null)
                return Results.NotFound(new { error = "Backup manager not configured." });
            var removed = backupManager.DeleteBackup(backupId);
            return removed
                ? Results.Ok(new { deleted = backupId })
                : Results.NotFound(new { error = $"Backup '{backupId}' not found." });
        });

        app.MapPost("/admin/backup/restore", (HttpContext ctx, RestoreBody body) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            if (backupManager is null)
                return Results.NotFound(new { error = "Backup manager not configured." });
            if (string.IsNullOrWhiteSpace(body.BackupId))
                return Results.BadRequest(new { error = "Missing 'backupId'." });
            if (string.IsNullOrWhiteSpace(body.NewDatabaseName))
                return Results.BadRequest(new { error = "Missing 'newDatabaseName'." });
            try
            {
                var path = backupManager.RestoreToNewDatabase(body.BackupId, body.NewDatabaseName);
                // Persist the new attach so it survives the next restart.
                catalog?.RecordAttach(body.NewDatabaseName, path);
                return Results.Ok(new
                {
                    database = body.NewDatabaseName,
                    filePath = path,
                    restoredFrom = body.BackupId,
                });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        static object BackupToWire(BackupRecord r) => new
        {
            id = r.Id,
            database = r.Database,
            path = r.Path,
            sizeBytes = r.SizeBytes,
            createdAtUtc = r.CreatedAtUtc.ToString("O"),
            kind = r.Kind,
        };

        // ----------------------------------------------------------------
        // Issue #88 phase 1 — WAL archiving (continuous PITR data source).
        // Per-DB enable/disable, status, list shipped segments. The
        // restore-with-target endpoint that consumes these segments is
        // phase 2 (next iteration).
        // ----------------------------------------------------------------

        app.MapPost("/databases/{name}/archive/enable", (HttpContext ctx, string name) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            if (walArchiver is null)
                return Results.NotFound(new { error = "WAL archiver not configured." });
            try
            {
                walArchiver.EnableForDatabase(name);
                return Results.Ok(new { database = name, enabled = true });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/databases/{name}/archive/disable", (HttpContext ctx, string name) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            if (walArchiver is null)
                return Results.NotFound(new { error = "WAL archiver not configured." });
            walArchiver.DisableForDatabase(name);
            return Results.Ok(new { database = name, enabled = false });
        });

        app.MapGet("/databases/{name}/archive/status", (HttpContext ctx, string name) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            if (walArchiver is null)
                return Results.NotFound(new { error = "WAL archiver not configured." });
            var s = walArchiver.GetStatus(name);
            return Results.Ok(new
            {
                database = s.Database,
                enabled = s.Enabled,
                nextSequence = s.NextSequence,
                lastShippedAtUtc = s.LastShippedAtUtc?.ToString("O"),
                segmentsThisSession = s.SegmentsThisSession,
            });
        });

        app.MapGet("/databases/{name}/archive/segments", (HttpContext ctx, string name) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            if (walArchiver is null)
                return Results.NotFound(new { error = "WAL archiver not configured." });
            var segs = walArchiver.ListSegments(name);
            return Results.Ok(new
            {
                database = name,
                count = segs.Count,
                totalBytes = segs.Sum(s => s.ByteCount),
                segments = segs.Select(s => new
                {
                    sequenceNumber = s.SequenceNumber,
                    archivedAtUtc = s.ArchivedAtUtc.ToString("O"),
                    byteCount = s.ByteCount,
                    recordCount = s.RecordCount,
                    segmentPath = s.SegmentPath,
                }),
            });
        });

        app.MapPost("/admin/archive/flush", (HttpContext ctx) =>
        {
            // Drive an immediate engine-Flush across every archive-
            // enabled database. The hook fires synchronously during
            // the flush, so by the time this returns, all dirty
            // pages have been shipped as new segments.
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            if (walArchiver is null)
                return Results.NotFound(new { error = "WAL archiver not configured." });
            walArchiver.FlushNow();
            return Results.Ok(new { flushed = true });
        });

        // ----------------------------------------------------------------
        // Issue #88 phase 2 — point-in-time restore. Preview + execute.
        // Preview shows the operator the segments that would be applied
        // BEFORE any filesystem mutations happen, so the Studio timeline
        // UI can render "X segments, Y MB" before the operator commits.
        // ----------------------------------------------------------------

        app.MapPost("/admin/backup/restore-pitr/preview", (HttpContext ctx, PitrPreviewBody body) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            if (pitr is null)
                return Results.NotFound(new { error = "PITR not configured." });
            if (string.IsNullOrWhiteSpace(body.BackupId))
                return Results.BadRequest(new { error = "Missing 'backupId'." });
            try
            {
                var plan = pitr.Plan(body.BackupId, body.TargetTimeUtc);
                return Results.Ok(new
                {
                    feasibleToRestore = plan.FeasibleToRestore,
                    refusalReason = plan.RefusalReason,
                    baseBackup = new
                    {
                        id = plan.BaseBackup.Id,
                        database = plan.BaseBackup.Database,
                        createdAtUtc = plan.BaseBackup.CreatedAtUtc.ToString("O"),
                        sizeBytes = plan.BaseBackup.SizeBytes,
                    },
                    targetTimeUtc = body.TargetTimeUtc.ToString("O"),
                    segments = plan.Segments.Select(s => new
                    {
                        sequenceNumber = s.SequenceNumber,
                        archivedAtUtc = s.ArchivedAtUtc.ToString("O"),
                        byteCount = s.ByteCount,
                        recordCount = s.RecordCount,
                    }),
                    segmentCount = plan.Segments.Count,
                    bytesToReplay = plan.Segments.Sum(s => s.ByteCount),
                    sequenceGaps = plan.SequenceGaps.Select(g => new
                    {
                        afterSequence = g.AfterSequence,
                        beforeSequence = g.BeforeSequence,
                        missingCount = g.MissingCount,
                    }),
                });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/admin/backup/restore-pitr", (HttpContext ctx, PitrRestoreBody body) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            if (pitr is null)
                return Results.NotFound(new { error = "PITR not configured." });
            if (string.IsNullOrWhiteSpace(body.BackupId))
                return Results.BadRequest(new { error = "Missing 'backupId'." });
            if (string.IsNullOrWhiteSpace(body.NewDatabaseName))
                return Results.BadRequest(new { error = "Missing 'newDatabaseName'." });
            try
            {
                var result = pitr.Restore(body.BackupId, body.TargetTimeUtc, body.NewDatabaseName);
                // Persist the attach so it survives the next restart.
                catalog?.RecordAttach(result.NewDatabaseName, result.NewDatabaseFilePath);
                return Results.Ok(new
                {
                    database = result.NewDatabaseName,
                    filePath = result.NewDatabaseFilePath,
                    sourceDatabase = result.SourceDatabase,
                    baseBackupId = result.BaseBackupId,
                    targetTimeUtc = result.TargetTimeUtc.ToString("O"),
                    segmentsApplied = result.SegmentsApplied,
                    bytesReplayed = result.BytesReplayed,
                    sequenceGaps = result.SequenceGaps.Select(g => new
                    {
                        afterSequence = g.AfterSequence,
                        beforeSequence = g.BeforeSequence,
                        missingCount = g.MissingCount,
                    }),
                });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
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

                // Issue #105: the '_' prefix is reserved for internal system
                // databases (e.g. _system, the key store). Refuse to create/attach
                // one over the API — it would be unreachable via the data plane
                // anyway, and must not shadow or collide with a system DB.
                if (ScopeCheck.IsReservedDatabase(req.Name))
                    return Results.BadRequest(new { error =
                        $"Database name '{req.Name}' is reserved — names starting with '_' are for internal system databases." });

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
            if (ScopeCheck.DenyIfReserved(name) is { } reserved) return reserved;
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
            // classic Issue #85/#86 scenario (catalog page corrupted,
            // data pages intact, rebuild can recover it).
            //
            // Threshold: an empty DB on disk is exactly 16 KB (page 0
            // header + page 1 empty catalog). Any byte beyond that is
            // at least one additional page — meaning there's SOMETHING
            // in the file the catalog isn't pointing at. Stricter than
            // the original 32 KB threshold because a single data page
            // (8 KB) brings the file to 24 KB, which is genuinely
            // suspicious in a "no collections" engine.
            const long EmptyDbStrictBytes = 16 * 1024;
            string recommendation;
            string? recommendationDetail;
            if (collectionNames.Count == 0 && dataInfo.Length > EmptyDbStrictBytes)
            {
                recommendation = "rebuild-catalog";
                recommendationDetail =
                    $"Catalog reports 0 collections but the data file is {dataInfo.Length:N0} bytes " +
                    "(an empty database is exactly 16,384 bytes). At least one extra page is sitting " +
                    "in the file the catalog doesn't point at — the catalog page that names them is " +
                    "probably zeroed. Use the Rebuild catalog flow to scan data pages and reconstruct it.";
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
                    // Payload bytes only: a live WAL always carries an 8-byte
                    // format header, and holding committed-but-not-checkpointed
                    // records is the normal healthy state under fsync-on-commit
                    // (issues #89/#90) — not "pending recovery". Header-only
                    // reads as 0.
                    recoveryLogBytes = recoveryInfo.Exists && recoveryInfo.Length > 8 ? recoveryInfo.Length - 8 : 0L,
                    walBytes = walInfo.Exists ? walInfo.Length : 0L,
                    snapshotMarkerPresent = snapshotMarker.Exists,
                },
                lockHolder = lockInfo.Exists ? TryReadLockHolder(lockInfo.FullName) : null,
                recommendation,
                recommendationDetail,
            });
        });

        // Issue #86 — catalog rebuild. Two endpoints: GET preview (Studio
        // confirmation dialog uses this to show what would be rebuilt
        // BEFORE writing anything), POST commit (does the actual rebuild).
        //
        // Operator workflow:
        //   1. Health endpoint flags recommendation = "rebuild-catalog"
        //   2. Operator Detaches the DB (rebuilder needs exclusive file access)
        //   3. POST /databases/discover-rebuild?path=... → preview
        //   4. Confirm
        //   5. POST /databases/discover-rebuild?path=...&execute=true → write
        //   6. Re-attach the DB via existing POST /databases
        //
        // The endpoint operates on a FILE PATH rather than a registry name
        // because the DB has to be detached first; there's nothing in the
        // registry to address. Admin-scoped.
        app.MapGet("/databases/rebuild-catalog/preview", (HttpContext ctx, string path) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "Missing 'path' query parameter." });
            if (!File.Exists(path))
                return Results.NotFound(new { error = $"No file at '{path}'." });
            var canonical = Path.GetFullPath(path);

            // Refuse if the file is still attached. Plan() opens read-only
            // so it'd succeed, but offering a plan when the operator
            // hasn't detached yet is misleading — Execute would fail.
            var attached = registry.List().FirstOrDefault(e =>
                string.Equals(Path.GetFullPath(e.FilePath), canonical,
                    StringComparison.OrdinalIgnoreCase));
            if (attached is not null)
            {
                return Results.Conflict(new
                {
                    error = $"Database is currently attached as '{attached.Name}'. " +
                            "DELETE /databases/" + attached.Name + " first (detach without --drop), then preview.",
                });
            }

            try
            {
                using var rb = CatalogRebuilder.Open(canonical);
                var plan = rb.Plan();
                return Results.Ok(new
                {
                    path = canonical,
                    safeToRebuild = plan.SafeToRebuild,
                    refusalReason = plan.RefusalReason,
                    chains = plan.Chains.Select(c => new
                    {
                        suggestedName = c.SuggestedName,
                        firstPageId = c.FirstPageId.Value,
                        documentCount = c.DocumentCount,
                        pageCount = c.PageCount,
                    }),
                });
            }
            catch (IOException ex)
            {
                return Results.Conflict(new { error = $"Could not open file for inspection: {ex.Message}" });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/databases/rebuild-catalog", (HttpContext ctx, string path) =>
        {
            if (ScopeCheck.RequireAdmin(ctx) is { } deny) return deny;
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "Missing 'path' query parameter." });
            if (!File.Exists(path))
                return Results.NotFound(new { error = $"No file at '{path}'." });
            var canonical = Path.GetFullPath(path);

            var attached = registry.List().FirstOrDefault(e =>
                string.Equals(Path.GetFullPath(e.FilePath), canonical,
                    StringComparison.OrdinalIgnoreCase));
            if (attached is not null)
            {
                return Results.Conflict(new
                {
                    error = $"Database is currently attached as '{attached.Name}'. " +
                            "DELETE /databases/" + attached.Name + " first (detach without --drop), then rebuild.",
                });
            }

            try
            {
                using var rb = CatalogRebuilder.Open(canonical);
                var result = rb.Execute();
                return Results.Ok(new
                {
                    path = result.FilePath,
                    backupPath = result.BackupPath,
                    backedUpPage1Bytes = result.BackedUpPage1Bytes,
                    recovered = result.Recovered.Select(r => new
                    {
                        name = r.Name,
                        firstPageId = r.FirstPageId.Value,
                        documentCount = r.DocumentCount,
                    }),
                });
            }
            catch (CatalogRebuildException ex)
            {
                // The rebuilder's safety invariants kicked in. 409 Conflict
                // is the right shape — the operation is well-formed but
                // refused given the current state.
                return Results.Conflict(new { error = ex.Message });
            }
            catch (IOException ex)
            {
                return Results.Conflict(new { error = $"Could not open file for rebuild: {ex.Message}" });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
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
            if (ScopeCheck.DenyIfReserved(name) is { } reserved) return reserved;
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
            if (ScopeCheck.DenyIfReserved(name) is { } reserved) return reserved;
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
            if (ScopeCheck.DenyIfReserved(name) is { } reserved) return reserved;
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
            if (ScopeCheck.DenyIfReserved(name) is { } reserved) return reserved;
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
                return Results.BadRequest(new { error = result.Message, code = result.ErrorCode });

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

        // Scoped index list — the flat GET /indexes/{collection} only sees
        // the default DB, which left Studio's Explorer blind for every other
        // attached database. Shape mirrors the flat route.
        app.MapGet("/db/{name}/indexes/{collection}", (HttpContext ctx, string name, string collection) =>
        {
            if (ScopeCheck.RequireDbRead(ctx, name) is { } deny) return deny;
            var db = registry.TryGet(name);
            if (db is null)
                return Results.NotFound(new { error = $"Database '{name}' is not attached." });
            var indexes = db.GetIndexes(collection);
            return Results.Ok(new
            {
                database = name,
                collection,
                indexes = indexes.Select(i => new { name = i.Name, path = i.JsonPath, unique = i.IsUnique }),
            });
        });

        // Scoped statistics — same gap as indexes. Unlike the flat /stats
        // this reports the raw byte size alongside the rounded MB figure so
        // programmatic callers don't have to un-round.
        app.MapGet("/db/{name}/stats", (HttpContext ctx, string name) =>
        {
            if (ScopeCheck.RequireDbRead(ctx, name) is { } deny) return deny;
            var db = registry.TryGet(name);
            if (db is null)
                return Results.NotFound(new { error = $"Database '{name}' is not attached." });
            db.Flush();
            var stats = db.GetStatistics();
            return Results.Ok(new
            {
                database = name,
                filePath = stats.FilePath,
                fileSizeBytes = stats.FileSize,
                fileSizeMb = Math.Round(stats.FileSize / 1024.0 / 1024.0, 2),
                pageCount = stats.PageCount,
                cachedPages = stats.CachedPages,
                dirtyPages = stats.DirtyPages,
                collections = stats.Collections.Select(c => new
                {
                    name = c.Name,
                    documentCount = c.DocumentCount,
                    indexCount = c.IndexCount,
                    indexes = c.Indexes.Select(i => new
                    {
                        name = i.Name, path = i.JsonPath, entries = i.EntryCount, unique = i.IsUnique,
                    }),
                }),
            });
        });

        // Scoped document read by internal _id. Returns the document JSON and
        // sets an ETag header, so a client (e.g. Studio's editable grid) can
        // refetch the current version after a 412 conflict.
        app.MapGet("/db/{name}/collections/{collection}/{id}", (HttpContext ctx, string name, string collection, string id) =>
        {
            if (ScopeCheck.RequireDbRead(ctx, name) is { } deny) return deny;
            var db = registry.TryGet(name);
            if (db is null) return Results.NotFound(new { error = $"Database '{name}' is not attached." });
            if (!Guid.TryParse(id, out var guid))
                return Results.BadRequest(new { error = "Expected DocumentForge's internal _id (a GUID)." });
            var coll = db.GetCollection(collection);
            var doc = coll?.FindById(new DocumentId(guid));
            if (doc is null) return Results.NotFound();
            return Results.Content(doc.ToJson(), "application/json");
        });

        // Scoped replace by internal _id, mirroring the flat PUT /collections/{name}/{id}
        // including optimistic concurrency: an `If-Match: <etag>` header routes
        // through ReplaceIfEtag and a mismatch returns 412 with the current ETag.
        // Without the header it's last-write-wins.
        app.MapPut("/db/{name}/collections/{collection}/{id}", async (HttpContext ctx, string name, string collection, string id, HttpRequest request) =>
        {
            if (ScopeCheck.RequireDbWrite(ctx, name) is { } deny) return deny;
            var db = registry.TryGet(name);
            if (db is null) return Results.NotFound(new { error = $"Database '{name}' is not attached." });
            if (!Guid.TryParse(id, out var guid))
                return Results.BadRequest(new { error = "Expected DocumentForge's internal _id (a GUID)." });

            using var reader = new StreamReader(request.Body);
            var json = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(json)) return Results.BadRequest(new { error = "Empty body." });

            var docId = new DocumentId(guid);
            var ifMatch = NormaliseIfMatch(request.Headers["If-Match"].ToString());
            try
            {
                if (!string.IsNullOrEmpty(ifMatch))
                {
                    string? newEtag;
                    try { newEtag = db.ReplaceIfEtag(collection, docId, json, ifMatch); }
                    catch (EtagMismatchException ex)
                    {
                        return Results.Json(new { error = ex.Message, expected = ex.ExpectedEtag, actual = ex.ActualEtag },
                            statusCode: StatusCodes.Status412PreconditionFailed);
                    }
                    if (newEtag is null) return Results.NotFound();
                    return Results.Ok(new { database = name, success = true, id = docId.ToString(), collection, etag = newEtag });
                }

                if (!db.Replace(collection, docId, json)) return Results.NotFound();
                return Results.Ok(new { database = name, success = true, id = docId.ToString(), collection });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // Issue #103 — scoped atomic conditional update / compare-and-set.
        app.MapPost("/db/{name}/collections/{collection}/{id}/conditional",
            (HttpContext ctx, string name, string collection, string id, HttpRequest request) =>
        {
            if (ScopeCheck.RequireDbWrite(ctx, name) is { } deny) return Task.FromResult(deny);
            var db = registry.TryGet(name);
            if (db is null) return Task.FromResult(Results.NotFound(new { error = $"Database '{name}' is not attached." }));
            return ConditionalUpdateHandler.Handle(db, collection, id, request);
        });

        // Scoped delete by internal _id.
        app.MapDelete("/db/{name}/collections/{collection}/{id}", (HttpContext ctx, string name, string collection, string id) =>
        {
            if (ScopeCheck.RequireDbWrite(ctx, name) is { } deny) return deny;
            var db = registry.TryGet(name);
            if (db is null) return Results.NotFound(new { error = $"Database '{name}' is not attached." });
            if (!Guid.TryParse(id, out var guid))
                return Results.BadRequest(new { error = "Expected DocumentForge's internal _id (a GUID)." });
            var coll = db.GetCollection(collection);
            if (coll is null) return Results.NotFound();
            var docId = new DocumentId(guid);
            var doc = coll.FindById(docId);
            if (doc is null) return Results.NotFound();
            if (coll.Delete(docId)) db.NotifyDocDeleted(collection, docId, doc);
            return Results.Ok(new { database = name, success = true, id = docId.ToString(), collection });
        });

        // Scoped drop-collection. Destructive, so it requires the same
        // X-Confirm: true header guard as the flat route.
        app.MapDelete("/db/{name}/collections/{collection}", (HttpContext ctx, string name, string collection, HttpRequest request) =>
        {
            if (ScopeCheck.RequireDbWrite(ctx, name) is { } deny) return deny;
            var db = registry.TryGet(name);
            if (db is null) return Results.NotFound(new { error = $"Database '{name}' is not attached." });
            if (request.Headers["X-Confirm"].ToString() != "true")
                return Results.BadRequest(new { error = "Destructive op. Include header 'X-Confirm: true' to proceed." });
            return db.DropCollection(collection)
                ? Results.Ok(new { database = name, success = true, dropped = collection })
                : Results.NotFound();
        });

        // Scoped compaction — defragment a collection and reclaim space from
        // deleted documents. Mirrors the flat /admin/compact/{collection}.
        app.MapPost("/db/{name}/admin/compact/{collection}", (HttpContext ctx, string name, string collection) =>
        {
            if (ScopeCheck.RequireDbWrite(ctx, name) is { } deny) return deny;
            var db = registry.TryGet(name);
            if (db is null) return Results.NotFound(new { error = $"Database '{name}' is not attached." });
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var result = db.Compact(collection);
                sw.Stop();
                return Results.Ok(new
                {
                    database = name,
                    success = true,
                    collection,
                    pagesCompacted = result.PagesCompacted,
                    bytesReclaimed = result.BytesReclaimed,
                    timeMs = sw.Elapsed.TotalMilliseconds,
                });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
    }

    private static string NormaliseIfMatch(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = raw.Trim();
        if (s.StartsWith("W/", StringComparison.Ordinal)) s = s[2..];
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1];
        return s;
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

// Issue #87 — backup config + restore payloads. Kept narrow on purpose;
// schedule + offsite shipping fields will land in follow-up iterations.
public record BackupConfigBody(string? BackupDir = null, int? RetentionCount = null, string? ScheduleCron = null);
public record RestoreBody(string BackupId, string NewDatabaseName);

// Issue #88 phase 2 — PITR preview + execute. TargetTimeUtc is the
// recovery target; the engine replays WAL segments up to that time.
public record PitrPreviewBody(string BackupId, DateTime TargetTimeUtc);
public record PitrRestoreBody(string BackupId, DateTime TargetTimeUtc, string NewDatabaseName);
