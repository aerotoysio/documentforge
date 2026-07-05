using DocumentForge.Document;

namespace DocumentForge.Query;

public sealed record QueryResult
{
    /// <summary>Stable, machine-readable error codes (issue #69). Clients
    /// branch on these instead of parsing <see cref="Message"/> text.</summary>
    public static class Codes
    {
        /// <summary>The FROM/target collection does not exist yet.
        /// Collections are created lazily on first insert, so reads can
        /// legitimately race ahead of the first write.</summary>
        public const string CollectionNotFound = "collectionNotFound";
    }

    public List<BsonDocument> Documents { get; init; } = new();
    public long AffectedCount { get; init; }
    public string? Message { get; init; }
    public bool Success { get; init; } = true;
    public TimeSpan ExecutionTime { get; init; }
    public string? QueryPlan { get; init; } // "INDEX_SCAN(idx_name)" or "COLLECTION_SCAN"

    /// <summary>Stable error identifier from <see cref="Codes"/> when
    /// <see cref="Success"/> is false and the failure has a well-known
    /// cause; null for free-form errors (parse failures etc.).</summary>
    public string? ErrorCode { get; init; }

    public static QueryResult Ok(List<BsonDocument> docs, string? plan = null, TimeSpan elapsed = default) =>
        new() { Documents = docs, AffectedCount = docs.Count, QueryPlan = plan, ExecutionTime = elapsed };

    public static QueryResult Affected(long count, string? message = null, TimeSpan elapsed = default) =>
        new() { AffectedCount = count, Message = message, ExecutionTime = elapsed };

    public static QueryResult Error(string message, string? errorCode = null) =>
        new() { Success = false, Message = message, ErrorCode = errorCode };
}
