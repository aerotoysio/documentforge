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

/// <summary>Transport-neutral query result. Documents are raw JSON strings so
/// the UI can render them without another round-trip through a BSON type.</summary>
public sealed record StudioQueryResult(
    bool Success,
    IReadOnlyList<string> Documents,
    long AffectedCount,
    string? Plan,
    double ExecutionMs,
    string? Message);
