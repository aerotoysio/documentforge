using System.Text.Json;
using DocumentForge.Engine;
using DocumentForge.Query;

namespace DocumentForge.Cli;

/// <summary>
/// Issue #87 — backup + recovery for attached databases. Wraps the
/// engine's built-in <see cref="DocumentForgeDb.Snapshot"/> primitive
/// (consistent on-disk snapshot at a checkpointed boundary) with:
///
/// <list type="bullet">
///   <item>Operator-friendly destination management (configured dir,
///         auto-created, timestamped filenames per DB)</item>
///   <item>Audit log in <c>_system.backups</c> so Studio + ops can
///         see what's been taken without scraping the disk</item>
///   <item>Retention policy: keep the N most-recent backups per DB,
///         delete older ones on demand. Defaults to <c>10</c>.</item>
///   <item>Restore-to-new-name semantics — never clobbers the source.
///         Operator picks a new DB name; the file is copied, attached,
///         and surfaced in the registry.</item>
/// </list>
///
/// <para>
/// Out of scope for the first iteration (deliberately deferred):
/// WAL archiving + true point-in-time recovery, scheduled backups,
/// remote shipping (S3 / Azure Blob), backup verification, cross-shard
/// coordination. All foreshadowed in the schema (extra columns reserved
/// in <c>_system.backups</c>) so adding them later doesn't migrate
/// existing data.
/// </para>
///
/// <para>
/// Why a class wired through <see cref="DatabaseRegistry"/> rather than
/// a static helper: each tenant DB has its own engine instance, and
/// snapshots are per-engine. The manager needs to look up the right
/// engine by name, hold a reference to <c>_system</c> for the audit log,
/// and survive concurrent backup requests (one outer lock per manager).
/// </para>
/// </summary>
public sealed class BackupManager
{
    private const string SystemDbName = "_system";
    private const string BackupsCollection = "backups";
    private const string ConfigCollection = "backup_config";
    private const string ConfigDocName = "global";
    private const int DefaultRetentionCount = 10;

    private readonly DatabaseRegistry _registry;
    private readonly string _dataDir;
    private readonly object _lock = new();

    public BackupManager(DatabaseRegistry registry, string dataDir)
    {
        _registry = registry;
        _dataDir = dataDir;
    }

    /// <summary>The directory backups are written to. Reads from
    /// <c>_system.backup_config</c> if set, otherwise falls back to
    /// <c>{data-dir}/backups</c>. The directory is created on demand.</summary>
    public string EffectiveBackupDir
    {
        get
        {
            var configured = TryReadConfig()?.BackupDir;
            var dir = !string.IsNullOrWhiteSpace(configured)
                ? configured!
                : Path.Combine(_dataDir, "backups");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>How many backups to keep per DB before older ones get
    /// pruned. Configured via the settings doc; defaults to 10.</summary>
    public int EffectiveRetentionCount =>
        TryReadConfig()?.RetentionCount ?? DefaultRetentionCount;

    /// <summary>Take a fresh snapshot of the named attached database.
    /// Emits a self-contained <c>.dfdb</c> file in the backup directory
    /// and records the action in <c>_system.backups</c>. Returns the
    /// record so the caller can show "backup completed → here's the
    /// row" without re-querying.</summary>
    public BackupRecord BackupOne(string databaseName, string kind = "manual")
    {
        if (IsInternalName(databaseName))
            throw new ArgumentException($"Refusing to back up internal database '{databaseName}'.");
        var db = _registry.TryGet(databaseName)
            ?? throw new ArgumentException($"Database '{databaseName}' is not attached.");

        lock (_lock)
        {
            var dir = EffectiveBackupDir;
            // Timestamp goes in the filename so concurrent backups (different
            // DBs) and sequential backups (same DB) never collide. Format is
            // lexicographically sortable so a directory listing is also a
            // chronological listing.
            var ts = DateTime.UtcNow.ToString("yyyyMMdd_HHmmssfff");
            var fileName = $"{databaseName}_{ts}.dfdb";
            var fullPath = Path.Combine(dir, fileName);

            // Snapshot() takes the engine write lock briefly, flushes
            // every dirty page, fsyncs, and copies the data file. No
            // sidecars (.wal, .recovery) are needed — the snapshot is
            // always at a checkpointed boundary.
            db.Snapshot(fullPath);

            var record = new BackupRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Database = databaseName,
                Path = fullPath,
                SizeBytes = new FileInfo(fullPath).Length,
                CreatedAtUtc = DateTime.UtcNow,
                Kind = kind,
            };
            RecordBackup(record);
            EnforceRetention(databaseName);
            return record;
        }
    }

    /// <summary>Back up every attached, non-internal database. Stops
    /// the moment any one fails — a partial cluster-wide backup is
    /// worse than no backup because operators assume success means
    /// success. The caller can check <see cref="BackupAllResult.Errors"/>
    /// to see what didn't finish.</summary>
    public BackupAllResult BackupAll(string kind = "manual")
    {
        var done = new List<BackupRecord>();
        var errors = new List<string>();
        foreach (var entry in _registry.List())
        {
            if (IsInternalName(entry.Name)) continue;
            try
            {
                done.Add(BackupOne(entry.Name, kind));
            }
            catch (Exception ex)
            {
                errors.Add($"{entry.Name}: {ex.Message}");
            }
        }
        return new BackupAllResult { Backups = done, Errors = errors };
    }

    /// <summary>List every backup recorded in <c>_system.backups</c>,
    /// optionally filtered by DB name. Returned newest-first.</summary>
    public IReadOnlyList<BackupRecord> ListBackups(string? databaseName = null)
    {
        var sys = _registry.TryGet(SystemDbName);
        if (sys is null) return Array.Empty<BackupRecord>();
        var rows = new List<BackupRecord>();
        QueryResult? result;
        try { result = sys.Execute($"SELECT * FROM {BackupsCollection}"); }
        catch { return rows; /* collection doesn't exist yet — fresh boot */ }
        if (!result.Success) return rows;

        // Defensive per-doc parse — a single damaged row in the audit log
        // mustn't mask the rest. BSON numeric types are sticky (an Int32
        // saved is read back as Int32, not auto-widened to Int64), so a
        // blanket AsInt64 cast in older builds could swallow whole result
        // sets when sizeBytes happened to fit in 32 bits.
        foreach (var doc in result.Documents)
        {
            try
            {
                if (!doc.ContainsKey("id") || !doc.ContainsKey("database")) continue;
                var record = new BackupRecord
                {
                    Id = doc["id"].AsString,
                    Database = doc["database"].AsString,
                    Path = doc.ContainsKey("path") ? doc["path"].AsString : "",
                    SizeBytes = doc.ContainsKey("sizeBytes") ? AsLongFlexible(doc["sizeBytes"]) : 0,
                    CreatedAtUtc = doc.ContainsKey("createdAtUtc") ? AsDateTimeFlexible(doc["createdAtUtc"]) : DateTime.MinValue,
                    Kind = doc.ContainsKey("kind") ? doc["kind"].AsString : "manual",
                };
                if (databaseName is null || string.Equals(record.Database, databaseName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    rows.Add(record);
                }
            }
            catch { /* skip damaged row, keep going */ }
        }
        return rows.OrderByDescending(r => r.CreatedAtUtc).ToList();
    }

    /// <summary>Read a BSON numeric as long without caring whether it
    /// was stored as Int32 or Int64. The engine preserves the input type
    /// (no auto-widening), so a JSON literal like <c>24576</c> lands as
    /// Int32 and a strict AsInt64 cast throws.</summary>
    private static long AsLongFlexible(Document.BsonValue v) => v.Type switch
    {
        Document.BsonType.Int64 => v.AsInt64,
        Document.BsonType.Int32 => v.AsInt32,
        Document.BsonType.Double => (long)v.AsDouble,
        _ => 0L,
    };

    /// <summary>Same defensive idea for DateTime: BSON has a native
    /// DateTime type but our writes are JSON strings via JsonSerializer.
    /// The engine's FromJson recogises ISO 8601 strings and silently
    /// converts them to BsonType.DateTime, so on read we may get either
    /// a String or a DateTime/DateTimeOffset. Be flexible.</summary>
    private static DateTime AsDateTimeFlexible(Document.BsonValue v)
    {
        if (v.Type == Document.BsonType.DateTime)
        {
            // BSON's DateTime is actually a DateTimeOffset internally.
            // Strip the offset → UTC DateTime for consumers.
            var dto = v.AsDateTime;
            return dto.UtcDateTime;
        }
        if (v.Type == Document.BsonType.String && DateTime.TryParse(v.AsString, out var t)) return t;
        // Last-resort: BSON might wrap a DateTimeOffset internally; pull
        // the raw object out and try ToString → TryParse.
        try
        {
            var str = v.ToString();
            if (DateTime.TryParse(str, out var t2)) return t2;
        }
        catch { }
        return DateTime.MinValue;
    }

    /// <summary>Delete a backup file AND its catalog row. No-op if the
    /// id isn't recognised. Returns true if a row was actually removed.</summary>
    public bool DeleteBackup(string backupId)
    {
        var sys = _registry.TryGet(SystemDbName);
        if (sys is null) return false;

        lock (_lock)
        {
            var record = ListBackups().FirstOrDefault(r => r.Id == backupId);
            if (record is null) return false;
            try { if (File.Exists(record.Path)) File.Delete(record.Path); } catch { /* best-effort */ }
            try
            {
                sys.Execute($"DELETE FROM {BackupsCollection} WHERE id = '{backupId}'");
                sys.Flush();
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>Restore a backup AS A NEW DATABASE (the source is never
    /// clobbered). The backup file is copied to <c>{data-dir}/{newName}.dfdb</c>
    /// and attached to the registry under the new name. Returns the new
    /// file path on success.</summary>
    public string RestoreToNewDatabase(string backupId, string newDatabaseName)
    {
        if (IsInternalName(newDatabaseName))
            throw new ArgumentException($"Refusing to restore to internal name '{newDatabaseName}'.");
        if (_registry.TryGet(newDatabaseName) is not null)
            throw new InvalidOperationException(
                $"Database '{newDatabaseName}' is already attached. Pick a fresh name; the restore is " +
                $"a copy, not an in-place overwrite — Detach the existing one first if you really want " +
                $"to free the name.");

        var record = ListBackups().FirstOrDefault(r => r.Id == backupId)
            ?? throw new ArgumentException($"No backup found with id '{backupId}'.");
        if (!File.Exists(record.Path))
            throw new InvalidOperationException(
                $"Backup file is missing from disk: {record.Path}. The catalog row exists but the file " +
                $"is gone — DELETE the row or restore from another backup.");

        var targetPath = Path.Combine(_dataDir, $"{newDatabaseName}.dfdb");
        lock (_lock)
        {
            if (File.Exists(targetPath))
                throw new InvalidOperationException(
                    $"Target path already exists: {targetPath}. A file with this name is sitting in the " +
                    $"data dir. Either Drop it via /databases/{newDatabaseName}?deleteFiles=true (after " +
                    $"attaching it) or pick a different name.");
            File.Copy(record.Path, targetPath);
            _registry.AttachOrCreate(newDatabaseName, targetPath);
        }
        return targetPath;
    }

    /// <summary>Read the singleton config doc. Returns null if no settings
    /// have been saved yet — callers fall through to defaults.</summary>
    public BackupConfig? GetConfig() => TryReadConfig();

    /// <summary>Write the singleton config doc. Creates the collection
    /// on first save. The doc is upserted (one row total — id always
    /// <c>"global"</c>).</summary>
    public void SaveConfig(BackupConfig config)
    {
        var sys = _registry.TryGet(SystemDbName)
            ?? throw new InvalidOperationException("_system DB not attached; cannot save backup config.");
        lock (_lock)
        {
            try { sys.Execute($"DELETE FROM {ConfigCollection} WHERE id = '{ConfigDocName}'"); }
            catch { /* collection may not exist yet */ }
            sys.Insert(ConfigCollection, JsonSerializer.Serialize(new
            {
                id = ConfigDocName,
                backupDir = config.BackupDir,
                retentionCount = config.RetentionCount,
                scheduleCron = config.ScheduleCron,
                updatedAtUtc = DateTime.UtcNow.ToString("O"),
            }));
            sys.Flush();
        }
    }

    // ----------------------------------------------------------------

    private BackupConfig? TryReadConfig()
    {
        var sys = _registry.TryGet(SystemDbName);
        if (sys is null) return null;
        try
        {
            var result = sys.Execute($"SELECT * FROM {ConfigCollection} WHERE id = '{ConfigDocName}'");
            if (!result.Success || result.Documents.Count == 0) return null;
            var d = result.Documents[0];
            return new BackupConfig
            {
                BackupDir = d.ContainsKey("backupDir") ? d["backupDir"].AsString : null,
                RetentionCount = d.ContainsKey("retentionCount")
                    ? (int)AsLongFlexible(d["retentionCount"])
                    : DefaultRetentionCount,
                ScheduleCron = d.ContainsKey("scheduleCron") ? d["scheduleCron"].AsString : null,
            };
        }
        catch { return null; }
    }

    private void RecordBackup(BackupRecord r)
    {
        var sys = _registry.TryGet(SystemDbName);
        if (sys is null) return;
        sys.Insert(BackupsCollection, JsonSerializer.Serialize(new
        {
            id = r.Id,
            database = r.Database,
            path = r.Path,
            sizeBytes = r.SizeBytes,
            createdAtUtc = r.CreatedAtUtc.ToString("O"),
            kind = r.Kind,
        }));
        sys.Flush();
    }

    /// <summary>Drop backups beyond the retention horizon for the named DB.
    /// Newest-first; the keep-window is <see cref="EffectiveRetentionCount"/>.
    /// Best-effort — a failure to delete a single file doesn't abort the rest.</summary>
    private void EnforceRetention(string databaseName)
    {
        var keep = EffectiveRetentionCount;
        if (keep <= 0) return;
        var rows = ListBackups(databaseName);
        if (rows.Count <= keep) return;
        foreach (var stale in rows.Skip(keep))
        {
            try { DeleteBackup(stale.Id); } catch { /* keep going */ }
        }
    }

    private static bool IsInternalName(string name) =>
        !string.IsNullOrEmpty(name) && (name.StartsWith("_", StringComparison.Ordinal) || name == "data");
}

public sealed record BackupRecord
{
    public string Id { get; init; } = "";
    public string Database { get; init; } = "";
    public string Path { get; init; } = "";
    public long SizeBytes { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public string Kind { get; init; } = "manual";
}

public sealed record BackupAllResult
{
    public List<BackupRecord> Backups { get; init; } = new();
    public List<string> Errors { get; init; } = new();
}

public sealed class BackupConfig
{
    public string? BackupDir { get; set; }
    public int RetentionCount { get; set; } = 10;
    public string? ScheduleCron { get; set; }
}
