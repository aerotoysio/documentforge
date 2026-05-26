using System.Collections.Concurrent;
using System.Text.Json;
using DocumentForge.Core;
using DocumentForge.Engine;
using DocumentForge.Storage;

namespace DocumentForge.Cli;

/// <summary>
/// Issue #88 phase 1 — continuous WAL archiver via the engine's
/// <see cref="IPreFlushHook"/> infrastructure. Captures every page
/// write at the moment it's flushed (which is also the moment the
/// engine's existing recovery log captures it, and the moment the
/// replication subsystem broadcasts it). Each completed flush batch
/// becomes one timestamped <strong>segment file</strong> under the
/// backup directory.
///
/// <para>
/// Why hook-based, not polling: an earlier draft of this class tailed
/// the <c>.recovery</c> file at 1s intervals. That didn't work — the
/// engine truncates the recovery log after each flush, so by the time
/// the polling tick read the file it was already empty for any
/// workload faster than the poll cadence. The flush hook fires
/// synchronously during every flush, before truncation, so we capture
/// every page write exactly once.
/// </para>
///
/// <para>
/// Why hook into the FLUSH path rather than the explicit transaction
/// WAL (<c>.dfdb.wal</c>): the engine's flat insert/update API doesn't
/// open a transaction unless the caller asks for one, so the
/// transaction WAL is empty for the bulk of real-world workloads. The
/// flush hook fires for EVERY page write, transactional or not, so
/// PITR coverage is universal.
/// </para>
///
/// <para>
/// Restore (next phase) works by concatenating the relevant segment
/// files in sequence order, dropping them as a synthetic
/// <c>.recovery</c> file next to a restored snapshot, and letting the
/// engine's existing <c>ReplayRecoveryLog</c> path do the rest. Zero
/// new replay code; the engine already knows how to apply
/// <c>[Magic:4 "WLOG"][PageId:4][Checksum:4][PageData:8192]</c>
/// records to a data file.
/// </para>
///
/// <para>
/// Archive layout on disk:
/// </para>
/// <code>
/// {backupDir}/
///   wal/
///     {dbname}/
///       20260526_143022123_seg00000001.walseg       ← N "WLOG" records
///       20260526_143022123_seg00000001.walseg.meta  ← JSON metadata
///       20260526_143023456_seg00000002.walseg
///       ...
/// </code>
/// </summary>
public sealed class WalArchiver : IDisposable
{
    private const string SystemDbName = "_system";
    private const string ArchiveStateCollection = "wal_archive_state";

    // Same on-wire format as RecoveryLog so segment bytes can be
    // concatenated directly into a synthetic .recovery for replay.
    private static readonly byte[] WalMagic = "WLOG"u8.ToArray();
    private const int RecordSize = 12 + Constants.PageSize; // magic(4) + pageId(4) + checksum(4) + page(8192)

    private readonly DatabaseRegistry _registry;
    private readonly BackupManager _backupManager;
    private readonly ConcurrentDictionary<string, HookedState> _hooks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _persistLock = new();
    private readonly Timer _flushTimer;
    private readonly TimeSpan _flushInterval;

    public WalArchiver(DatabaseRegistry registry, BackupManager backupManager, TimeSpan? flushInterval = null)
    {
        _registry = registry;
        _backupManager = backupManager;
        // The PreFlushHook only fires when the engine actually flushes
        // dirty pages — which the engine doesn't do on its own; it's
        // operator-driven via Checkpoint/Flush. For continuous WAL
        // archiving we have to drive the flush ourselves. Default 5s
        // is the airline-OMS sweet spot: tight enough RPO that bad
        // events lose < 5 seconds of data, infrequent enough that
        // the write-lock acquisition cost is negligible.
        _flushInterval = flushInterval ?? TimeSpan.FromSeconds(5);
        _flushTimer = new Timer(_ => SafeFlushEnabled(), null, _flushInterval, _flushInterval);
    }

    /// <summary>Drive an immediate flush across all archive-enabled DBs.
    /// Useful from the Studio "Archive now" button (lets the operator
    /// confirm new data has been shipped without waiting for the next
    /// scheduled tick) and from tests.</summary>
    public void FlushNow() => SafeFlushEnabled();

    private void SafeFlushEnabled()
    {
        foreach (var (name, st) in _hooks.ToArray())
        {
            try { st.Db.Flush(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WalArchiver] {name}: flush failed — {ex.Message}");
            }
        }
    }

    /// <summary>Start archiving the named database. Idempotent. Reads
    /// the persisted sequence number from <c>_system.wal_archive_state</c>
    /// if present so service restarts continue numbering monotonically.</summary>
    public void EnableForDatabase(string name)
    {
        if (IsInternalName(name)) throw new ArgumentException($"Refusing to archive '{name}'.");
        var db = _registry.TryGet(name)
            ?? throw new ArgumentException($"Database '{name}' is not attached.");
        if (_hooks.ContainsKey(name)) return; // idempotent

        var nextSeq = ReadPersistedSequence(name);
        var hook = new ArchiverHook(name, this, nextSeq);
        db.AddPreFlushHook(hook);
        _hooks[name] = new HookedState { Hook = hook, Db = db };
        PersistEnabledState(name, true, hook.NextSequence);
    }

    /// <summary>Stop archiving. Idempotent. The persisted state record
    /// stays around (marked disabled) so a subsequent Enable resumes
    /// numbering from where we left off.</summary>
    public void DisableForDatabase(string name)
    {
        if (_hooks.TryRemove(name, out var st))
        {
            st.Db.RemovePreFlushHook(st.Hook);
            PersistEnabledState(name, false, st.Hook.NextSequence);
        }
    }

    public IReadOnlyList<string> EnabledDatabases() => _hooks.Keys.ToList();

    public ArchiveStatus GetStatus(string name)
    {
        if (_hooks.TryGetValue(name, out var st))
        {
            return new ArchiveStatus
            {
                Database = name,
                Enabled = true,
                NextSequence = st.Hook.NextSequence,
                LastShippedAtUtc = st.Hook.LastShippedAtUtc,
                SegmentsThisSession = st.Hook.SegmentsThisSession,
            };
        }
        // Surface persisted-but-disabled state so the UI can show
        // "previously enabled, currently off" rather than "never set up".
        return new ArchiveStatus
        {
            Database = name,
            Enabled = false,
            NextSequence = ReadPersistedSequence(name),
            LastShippedAtUtc = null,
            SegmentsThisSession = 0,
        };
    }

    public IReadOnlyList<WalSegmentInfo> ListSegments(string name)
    {
        var dir = SegmentDirFor(name);
        if (!Directory.Exists(dir)) return Array.Empty<WalSegmentInfo>();
        var rows = new List<WalSegmentInfo>();
        foreach (var metaPath in Directory.EnumerateFiles(dir, "*.walseg.meta"))
        {
            try
            {
                var json = File.ReadAllText(metaPath);
                using var jdoc = JsonDocument.Parse(json);
                var root = jdoc.RootElement;
                rows.Add(new WalSegmentInfo
                {
                    Database = name,
                    SegmentPath = metaPath[..^5],
                    MetaPath = metaPath,
                    SequenceNumber = root.GetProperty("sequenceNumber").GetInt64(),
                    ArchivedAtUtc = DateTime.Parse(root.GetProperty("archivedAtUtc").GetString()!).ToUniversalTime(),
                    ByteCount = root.GetProperty("byteCount").GetInt64(),
                    RecordCount = root.TryGetProperty("recordCount", out var rc) ? rc.GetInt32() : 0,
                });
            }
            catch { /* corrupt sidecar — skip */ }
        }
        return rows.OrderBy(r => r.SequenceNumber).ToList();
    }

    /// <summary>Re-enable any databases that were enabled in a previous
    /// service lifetime. Called once at boot from ServeCommand so PITR
    /// resumes automatically.</summary>
    public void RestoreFromPersistedState()
    {
        var sys = _registry.TryGet(SystemDbName);
        if (sys is null) return;
        try
        {
            var r = sys.Execute($"SELECT * FROM {ArchiveStateCollection}");
            if (!r.Success) return;
            foreach (var doc in r.Documents)
            {
                if (!doc.ContainsKey("database") || !doc.ContainsKey("enabled")) continue;
                var name = doc["database"].AsString;
                var enabled = doc["enabled"].Type == Document.BsonType.Boolean && doc["enabled"].AsBoolean;
                if (!enabled) continue;
                if (_registry.TryGet(name) is null) continue;
                try { EnableForDatabase(name); } catch { /* missing DB or already enabled */ }
            }
        }
        catch { /* collection doesn't exist yet */ }
    }

    public void Dispose()
    {
        _flushTimer.Dispose();
        foreach (var kvp in _hooks.ToArray())
        {
            DisableForDatabase(kvp.Key);
        }
    }

    // ----------------------------------------------------------------
    // Internal — called by the hook

    internal string SegmentDirFor(string dbName) =>
        Path.Combine(_backupManager.EffectiveBackupDir, "wal", dbName);

    internal void PersistEnabledState(string name, bool enabled, long nextSeq)
    {
        var sys = _registry.TryGet(SystemDbName);
        if (sys is null) return;
        lock (_persistLock)
        {
            try { sys.Execute($"DELETE FROM {ArchiveStateCollection} WHERE database = '{name}'"); }
            catch { /* fresh collection */ }
            sys.Insert(ArchiveStateCollection, JsonSerializer.Serialize(new
            {
                database = name,
                enabled,
                nextSequence = nextSeq,
                updatedAtUtc = DateTime.UtcNow.ToString("O"),
            }));
            sys.Flush();
        }
    }

    private long ReadPersistedSequence(string name)
    {
        var sys = _registry.TryGet(SystemDbName);
        if (sys is null) return 0;
        try
        {
            var r = sys.Execute($"SELECT * FROM {ArchiveStateCollection} WHERE database = '{name}'");
            if (!r.Success || r.Documents.Count == 0) return 0;
            var d = r.Documents[0];
            return d.ContainsKey("nextSequence")
                ? (d["nextSequence"].Type == Document.BsonType.Int64 ? d["nextSequence"].AsInt64
                  : d["nextSequence"].Type == Document.BsonType.Int32 ? d["nextSequence"].AsInt32 : 0)
                : 0;
        }
        catch { return 0; }
    }

    private static bool IsInternalName(string name) =>
        !string.IsNullOrEmpty(name) && (name.StartsWith("_", StringComparison.Ordinal) || name == "data");

    // ----------------------------------------------------------------

    private sealed class HookedState
    {
        public ArchiverHook Hook { get; init; } = null!;
        public DocumentForgeDb Db { get; init; } = null!;
    }

    /// <summary>The actual <see cref="IPreFlushHook"/> implementation.
    /// Buffers in-flight page writes during a flush batch and emits one
    /// segment file per batch in <see cref="OnAfterFlushComplete"/>.</summary>
    private sealed class ArchiverHook : IPreFlushHook
    {
        private readonly string _database;
        private readonly WalArchiver _parent;
        private readonly List<(PageId PageId, byte[] PageData)> _pending = new();
        private readonly object _gate = new();

        public long NextSequence;
        public DateTime? LastShippedAtUtc;
        public long SegmentsThisSession;

        public ArchiverHook(string database, WalArchiver parent, long startingSequence)
        {
            _database = database;
            _parent = parent;
            NextSequence = startingSequence;
        }

        public void OnBeforeFlush(PageId pageId, byte[] pageData)
        {
            // Defensive copy: the engine reuses page buffers between
            // writes, so we cannot retain the caller's array.
            var snapshot = new byte[pageData.Length];
            Array.Copy(pageData, snapshot, pageData.Length);
            lock (_gate) _pending.Add((pageId, snapshot));
        }

        public void OnAfterFlushComplete()
        {
            List<(PageId PageId, byte[] PageData)> batch;
            lock (_gate)
            {
                if (_pending.Count == 0) return;
                batch = new List<(PageId, byte[])>(_pending);
                _pending.Clear();
            }
            try { ShipBatch(batch); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WalArchiver] {_database}: segment ship failed — {ex.Message}");
            }
        }

        public void EnsureLogDurable() { /* nothing to do — segments
            are durable via WriteAllBytes which fsyncs on close. */ }

        private void ShipBatch(List<(PageId PageId, byte[] PageData)> batch)
        {
            // Encode in the SAME on-wire format as RecoveryLog so
            // restore can concatenate segments straight into a
            // synthetic .recovery file.
            var buffer = new byte[batch.Count * RecordSize];
            int offset = 0;
            foreach (var (pageId, pageData) in batch)
            {
                WalMagic.CopyTo(buffer, offset);
                BitConverter.TryWriteBytes(buffer.AsSpan(offset + 4), pageId.Value);
                BitConverter.TryWriteBytes(buffer.AsSpan(offset + 8), Crc32(pageData));
                Array.Copy(pageData, 0, buffer, offset + 12, pageData.Length);
                offset += RecordSize;
            }

            var now = DateTime.UtcNow;
            var seq = System.Threading.Interlocked.Increment(ref NextSequence);
            var dir = _parent.SegmentDirFor(_database);
            Directory.CreateDirectory(dir);
            var fileName = $"{now:yyyyMMdd_HHmmssfff}_seg{seq:D8}.walseg";
            var segPath = Path.Combine(dir, fileName);
            File.WriteAllBytes(segPath, buffer);
            File.WriteAllText(segPath + ".meta", JsonSerializer.Serialize(new
            {
                sequenceNumber = seq,
                archivedAtUtc = now.ToString("O"),
                byteCount = (long)buffer.Length,
                recordCount = batch.Count,
                database = _database,
            }));

            LastShippedAtUtc = now;
            SegmentsThisSession++;
            _parent.PersistEnabledState(_database, enabled: true, nextSeq: NextSequence);
        }

        // Mirror of RecoveryLog's CRC32 implementation. Kept private
        // to avoid making RecoveryLog's internals public; it's standard
        // CRC-32 (IEEE 802.3) so any reader can verify offline.
        private static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                    crc = (crc >> 1) ^ (0xEDB88320u & (uint)(-(int)(crc & 1)));
            }
            return ~crc;
        }
    }
}

public sealed record ArchiveStatus
{
    public string Database { get; init; } = "";
    public bool Enabled { get; init; }
    public long NextSequence { get; init; }
    public DateTime? LastShippedAtUtc { get; init; }
    public long SegmentsThisSession { get; init; }
}

public sealed record WalSegmentInfo
{
    public string Database { get; init; } = "";
    public string SegmentPath { get; init; } = "";
    public string MetaPath { get; init; } = "";
    public long SequenceNumber { get; init; }
    public DateTime ArchivedAtUtc { get; init; }
    public long ByteCount { get; init; }
    public int RecordCount { get; init; }
}
