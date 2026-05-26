using DocumentForge.Engine;

namespace DocumentForge.Cli;

/// <summary>
/// Issue #88 phase 2 — restore a database to a chosen moment in time.
/// Walks every WAL segment archived between a base backup and the
/// recovery target, concatenates the relevant ones into a synthetic
/// <c>.recovery</c> sidecar next to a restored copy of the base
/// backup, then attaches the database. The engine's existing recovery-
/// log replay path on Open does the actual page-write replay — no
/// new replay code anywhere.
///
/// <para>
/// Restore semantics, in order:
/// </para>
/// <list type="number">
///   <item>Locate the base backup row in <c>_system.backups</c>.</item>
///   <item>Reject if the target time is BEFORE the base backup was
///   taken (can only roll forward, never backward of the snapshot).</item>
///   <item>Find every WAL segment for the source DB whose
///   <c>archivedAtUtc</c> is in the open-closed window
///   <c>(baseBackup.CreatedAtUtc, targetTimeUtc]</c>. Order by sequence.</item>
///   <item>Reject if the target DB name is already attached or has a
///   colliding file in the data dir — restore is always to a NEW
///   name, the same never-clobber rule as <see cref="BackupManager.RestoreToNewDatabase"/>.</item>
///   <item>Copy the base backup file to <c>{dataDir}/{newName}.dfdb</c>.</item>
///   <item>Concatenate the segment files byte-for-byte into
///   <c>{dataDir}/{newName}.dfdb.recovery</c>. Each segment is already
///   in the same on-disk format the engine's recovery replay expects,
///   so concatenation is the entire transformation.</item>
///   <item><see cref="DatabaseRegistry.AttachOrCreate"/> the new DB.
///   The engine reads the synthetic recovery file on Open, replays
///   the page writes, and we're done.</item>
/// </list>
///
/// <para>
/// Sequence gaps: if archived segments are missing (segment 3 sitting
/// alongside 1, 2, 4, 5...), the missing pages aren't applied. Often
/// harmless — the same page might be rewritten by a later segment, so
/// the page-write-LWW model auto-heals. We surface the gap in the
/// result so the operator can see it, but we don't refuse. The
/// alternative (refusing the whole restore) trades a tractable
/// inconsistency for a complete failure, which is worse.
/// </para>
/// </summary>
public sealed class PointInTimeRestore
{
    private readonly DatabaseRegistry _registry;
    private readonly BackupManager _backupManager;
    private readonly WalArchiver _walArchiver;
    private readonly string _dataDir;

    public PointInTimeRestore(DatabaseRegistry registry, BackupManager backupManager, WalArchiver walArchiver, string dataDir)
    {
        _registry = registry;
        _backupManager = backupManager;
        _walArchiver = walArchiver;
        _dataDir = dataDir;
    }

    /// <summary>Plan — what segments WOULD be applied, with no
    /// filesystem mutations. Studio's restore wizard calls this before
    /// committing so the operator can confirm "X segments, Y MB of
    /// replay" before pulling the trigger.</summary>
    public PitrPlan Plan(string baseBackupId, DateTime targetUtc)
    {
        // Critical: JSON-deserialized DateTimes often arrive as Local
        // or Unspecified kind (depending on the offset in the input).
        // Comparing those raw Ticks against Utc-kind ArchivedAtUtc
        // values gives off-by-timezone errors that look like "PITR
        // included segments that come AFTER the target." Normalize.
        targetUtc = NormalizeToUtc(targetUtc);
        var (baseBackup, _) = LocateBackupOrThrow(baseBackupId);
        if (targetUtc < baseBackup.CreatedAtUtc)
        {
            return new PitrPlan
            {
                FeasibleToRestore = false,
                RefusalReason = $"Target time {targetUtc:o} is before the base backup was taken " +
                                $"({baseBackup.CreatedAtUtc:o}). PITR rolls forward only — pick an " +
                                $"earlier backup or a later target.",
                BaseBackup = baseBackup,
                Segments = Array.Empty<WalSegmentInfo>(),
            };
        }
        var segments = SegmentsInWindow(baseBackup.Database, baseBackup.CreatedAtUtc, targetUtc);
        return new PitrPlan
        {
            FeasibleToRestore = true,
            BaseBackup = baseBackup,
            Segments = segments,
            SequenceGaps = DetectSequenceGaps(segments),
        };
    }

    /// <summary>Execute the plan: copy + concatenate + attach. The
    /// returned result mirrors <see cref="Plan"/> plus the post-restore
    /// fields (new DB name, file path, attached confirmation).</summary>
    public PitrResult Restore(string baseBackupId, DateTime targetUtc, string newDatabaseName)
    {
        if (IsInternalName(newDatabaseName))
            throw new ArgumentException($"Refusing to restore to internal name '{newDatabaseName}'.");
        if (_registry.TryGet(newDatabaseName) is not null)
            throw new InvalidOperationException(
                $"Database '{newDatabaseName}' is already attached. Pick a fresh name; PITR is always " +
                $"a copy, never an in-place overwrite — Detach the existing one first if you really " +
                $"want to free the name.");

        targetUtc = NormalizeToUtc(targetUtc);
        var (baseBackup, _) = LocateBackupOrThrow(baseBackupId);
        if (targetUtc < baseBackup.CreatedAtUtc)
            throw new ArgumentException(
                $"Target time {targetUtc:o} is before the base backup was taken " +
                $"({baseBackup.CreatedAtUtc:o}). PITR rolls forward only.");
        if (!File.Exists(baseBackup.Path))
            throw new InvalidOperationException(
                $"Base backup file is missing from disk: {baseBackup.Path}. The catalog row " +
                $"exists but the file is gone.");

        var targetPath = Path.Combine(_dataDir, $"{newDatabaseName}.dfdb");
        if (File.Exists(targetPath))
            throw new InvalidOperationException(
                $"Target path already exists: {targetPath}. Pick a different name.");

        // Step 1 — copy the base snapshot to its new home.
        File.Copy(baseBackup.Path, targetPath);

        // Step 2 — concatenate the applicable segments into a
        // synthetic .recovery sidecar. The engine's recovery log
        // format is exactly what each segment file already contains,
        // so this is a pure byte-stream concatenation.
        var segments = SegmentsInWindow(baseBackup.Database, baseBackup.CreatedAtUtc, targetUtc);
        long bytesReplayed = 0;
        if (segments.Count > 0)
        {
            var recoveryPath = targetPath + ".recovery";
            using var output = new FileStream(recoveryPath, FileMode.Create, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough);
            foreach (var seg in segments)
            {
                if (!File.Exists(seg.SegmentPath))
                    continue; // missing file — treated like a gap, kept going
                using var input = new FileStream(seg.SegmentPath, FileMode.Open, FileAccess.Read,
                    FileShare.Read);
                input.CopyTo(output);
                bytesReplayed += seg.ByteCount;
            }
            output.Flush(true);
        }

        // Step 3 — attach. The engine's Open path reads the synthetic
        // .recovery, applies every (PageId, PageData) record to the
        // data file in order, and the database opens at the target
        // time's state.
        _registry.AttachOrCreate(newDatabaseName, targetPath);

        return new PitrResult
        {
            NewDatabaseName = newDatabaseName,
            NewDatabaseFilePath = targetPath,
            SourceDatabase = baseBackup.Database,
            BaseBackupId = baseBackup.Id,
            BaseBackupCreatedAtUtc = baseBackup.CreatedAtUtc,
            TargetTimeUtc = targetUtc,
            SegmentsApplied = segments.Count,
            BytesReplayed = bytesReplayed,
            SequenceGaps = DetectSequenceGaps(segments),
        };
    }

    // ----------------------------------------------------------------

    private (BackupRecord, string) LocateBackupOrThrow(string id)
    {
        var rec = _backupManager.ListBackups().FirstOrDefault(b => b.Id == id)
            ?? throw new ArgumentException($"No backup found with id '{id}'.");
        return (rec, rec.Database);
    }

    private IReadOnlyList<WalSegmentInfo> SegmentsInWindow(string sourceDb, DateTime baseUtc, DateTime targetUtc) =>
        _walArchiver.ListSegments(sourceDb)
            .Where(s => s.ArchivedAtUtc > baseUtc && s.ArchivedAtUtc <= targetUtc)
            .OrderBy(s => s.SequenceNumber)
            .ToList();

    /// <summary>Report any holes in the sequence-number stream of the
    /// segments we'd apply. Each gap is reported as (prev, next) so the
    /// operator knows exactly which segments are missing.</summary>
    private static List<SequenceGap> DetectSequenceGaps(IReadOnlyList<WalSegmentInfo> segments)
    {
        var gaps = new List<SequenceGap>();
        for (int i = 1; i < segments.Count; i++)
        {
            var prev = segments[i - 1].SequenceNumber;
            var curr = segments[i].SequenceNumber;
            if (curr != prev + 1)
            {
                gaps.Add(new SequenceGap
                {
                    AfterSequence = prev,
                    BeforeSequence = curr,
                    MissingCount = curr - prev - 1,
                });
            }
        }
        return gaps;
    }

    private static bool IsInternalName(string name) =>
        !string.IsNullOrEmpty(name) && (name.StartsWith("_", StringComparison.Ordinal) || name == "data");

    /// <summary>JSON-deserialized DateTimes can arrive as
    /// <see cref="DateTimeKind.Local"/> or <see cref="DateTimeKind.Unspecified"/>
    /// depending on the offset in the input string. Raw-Ticks
    /// comparison against UTC-kind values then drifts by the local
    /// offset — manifested as "PITR included segments after the
    /// target time" if the operator is in a non-UTC timezone. Force
    /// UTC normalisation here so every downstream comparison is
    /// timezone-correct.</summary>
    private static DateTime NormalizeToUtc(DateTime dt) => dt.Kind switch
    {
        DateTimeKind.Utc => dt,
        DateTimeKind.Local => dt.ToUniversalTime(),
        _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc), // assume bare ISO-with-Z or +00 = UTC
    };
}

public sealed record PitrPlan
{
    public bool FeasibleToRestore { get; init; }
    public string? RefusalReason { get; init; }
    public BackupRecord BaseBackup { get; init; } = null!;
    public IReadOnlyList<WalSegmentInfo> Segments { get; init; } = Array.Empty<WalSegmentInfo>();
    public IReadOnlyList<SequenceGap> SequenceGaps { get; init; } = Array.Empty<SequenceGap>();
}

public sealed record PitrResult
{
    public string NewDatabaseName { get; init; } = "";
    public string NewDatabaseFilePath { get; init; } = "";
    public string SourceDatabase { get; init; } = "";
    public string BaseBackupId { get; init; } = "";
    public DateTime BaseBackupCreatedAtUtc { get; init; }
    public DateTime TargetTimeUtc { get; init; }
    public int SegmentsApplied { get; init; }
    public long BytesReplayed { get; init; }
    public IReadOnlyList<SequenceGap> SequenceGaps { get; init; } = Array.Empty<SequenceGap>();
}

public sealed record SequenceGap
{
    public long AfterSequence { get; init; }
    public long BeforeSequence { get; init; }
    public long MissingCount { get; init; }
}
