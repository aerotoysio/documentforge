namespace DocumentForge.Studio.Core.Models;

/// <summary>A database visible through a connection. For direct-file
/// connections there is exactly one; for HTTP connections this mirrors the
/// server's attached-database registry.</summary>
public sealed record DatabaseInfo(string Name, string? FilePath, bool IsDefault);

/// <summary>EntryCount is -1 when the transport can't provide it cheaply
/// (direct-file mode); the UI hides the count in that case.</summary>
public sealed record IndexInfo(string Name, string JsonPath, bool IsUnique, long EntryCount);

public sealed record CollectionStats(string Name, long DocumentCount, int IndexCount);

public sealed record DatabaseStats(
    long FileSize,
    long PageCount,
    int CachedPages,
    int DirtyPages,
    IReadOnlyList<CollectionStats> Collections);

public sealed record ServerHealth(bool Healthy, string Status, string? Version, string? Detail);

public sealed record CompactionInfo(long PagesCompacted, long BytesReclaimed, double TimeMs);

/// <summary>Per-database health + diagnostics. Recommendation is one of
/// "healthy" / "rebuild-catalog" / "recovery-pending" / "engine-degraded".</summary>
public sealed record DatabaseHealthReport(
    string HealthStatus,
    bool ReadOnly,
    int CollectionCount,
    long TotalDocuments,
    long DataSizeBytes,
    long RecoveryLogBytes,
    long WalBytes,
    bool SnapshotMarkerPresent,
    string? LockHolder,
    string Recommendation,
    string? RecommendationDetail);

/// <summary>A follower peer as seen from a leader.</summary>
public sealed record ReplicationFollowerInfo(string Endpoint, string? HttpEndpoint, long LagSeq, string? ConnectedAtUtc = null)
{
    /// <summary>Caught up with the leader (no known lag).</summary>
    public bool InSync => LagSeq == 0;

    public string StatusText => LagSeq == 0 ? "in sync" : $"lagging ({LagSeq:N0} behind)";
}

/// <summary>Per-database replication state. Role is "leader", "follower", or
/// "none". Leader fields populate when this DB leads; follower fields when it
/// follows.</summary>
public sealed record ReplicationStatus(
    string Role,
    bool ReadOnly,
    long CurrentSeq,
    int LeaderPort,
    int FollowerCount,
    IReadOnlyList<ReplicationFollowerInfo> Followers,
    long FollowerLastAppliedSeq,
    long OpsApplied,
    bool GapsDetected,
    bool AutoFailoverPromoted,
    string? FollowerLeaderEndpoint);

/// <summary>Transport-neutral query result. Documents are raw JSON strings so
/// the UI can render them without another round-trip through a BSON type.</summary>
public sealed record StudioQueryResult(
    bool Success,
    IReadOnlyList<string> Documents,
    long AffectedCount,
    string? Plan,
    double ExecutionMs,
    string? Message);
