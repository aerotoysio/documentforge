namespace DocumentForge.Studio.Core.Models;

/// <summary>Backup settings from GET /admin/backup/config. BackupDir is the
/// effective directory; BackupDirConfigured the explicitly-set one (null =
/// server default).</summary>
public sealed record BackupConfigInfo(
    string? BackupDir,
    string? BackupDirConfigured,
    int RetentionCount,
    string? ScheduleCron);

/// <summary>Per-database WAL-archiving state (issue #88 phase 1).</summary>
public sealed record ArchiveStatusInfo(
    string Database,
    bool Enabled,
    long NextSequence,
    string? LastShippedAtUtc,
    long SegmentsThisSession);

/// <summary>One shipped WAL segment.</summary>
public sealed record ArchiveSegmentInfo(
    long SequenceNumber,
    string? ArchivedAtUtc,
    long ByteCount,
    long RecordCount);

/// <summary>A hole in the archived-segment sequence — data in this range is
/// unrecoverable, so PITR refuses to cross it.</summary>
public sealed record PitrSequenceGap(long AfterSequence, long BeforeSequence, long MissingCount);

/// <summary>Dry-run plan for a point-in-time restore (issue #88 phase 2):
/// what would be replayed, and whether the restore is feasible at all.</summary>
public sealed record PitrPreviewInfo(
    bool FeasibleToRestore,
    string? RefusalReason,
    string BaseBackupId,
    string BaseDatabase,
    string? BaseCreatedAtUtc,
    int SegmentCount,
    long BytesToReplay,
    IReadOnlyList<PitrSequenceGap> SequenceGaps);

/// <summary>The outcome of an executed point-in-time restore.</summary>
public sealed record PitrRestoreResult(
    string Database,
    string FilePath,
    string SourceDatabase,
    int SegmentsApplied,
    long BytesReplayed);
