using DocumentForge.Document;

namespace DocumentForge.Query;

public sealed record QueryResult
{
    public List<BsonDocument> Documents { get; init; } = new();
    public long AffectedCount { get; init; }
    public string? Message { get; init; }
    public bool Success { get; init; } = true;
    public TimeSpan ExecutionTime { get; init; }
    public string? QueryPlan { get; init; } // "INDEX_SCAN(idx_name)" or "COLLECTION_SCAN"

    public static QueryResult Ok(List<BsonDocument> docs, string? plan = null, TimeSpan elapsed = default) =>
        new() { Documents = docs, AffectedCount = docs.Count, QueryPlan = plan, ExecutionTime = elapsed };

    public static QueryResult Affected(long count, string? message = null, TimeSpan elapsed = default) =>
        new() { AffectedCount = count, Message = message, ExecutionTime = elapsed };

    public static QueryResult Error(string message) =>
        new() { Success = false, Message = message };
}
