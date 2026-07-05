using DocumentForge.Document;

namespace DocumentForge.Engine;

/// <summary>
/// Issue #103 — declarative atomic conditional update / compare-and-set. A set
/// of <see cref="UpdateCondition"/>s is evaluated against the current document
/// and, only if ALL hold, a set of <see cref="UpdateOperation"/>s is applied —
/// all inside the engine's write lock, so two racing callers can't both win.
/// This is the server-side "decrement seats if &gt;= 1" / "set status if still
/// held" / ETag-CAS primitive that the whole-document If-Match path couldn't
/// express without client retries.
/// </summary>
public enum ConditionOp { Eq, Ne, Lt, Lte, Gt, Gte, Exists, NotExists }

public enum MutationOp { Set, Inc, Dec, Unset }

/// <summary>A predicate on one top-level field of the target document.</summary>
public sealed record UpdateCondition(string Field, ConditionOp Op, BsonValue? Value);

/// <summary>A mutation of one top-level field. Inc/Dec require a numeric field
/// (an absent field is treated as 0); Set replaces the value outright; Unset
/// removes the field (a no-op if it was already absent, so PATCH is idempotent).</summary>
public sealed record UpdateOperation(string Field, MutationOp Op, BsonValue? Value);

public enum ConditionalUpdateStatus
{
    /// <summary>All conditions held; the operations were applied and committed.</summary>
    Success,
    /// <summary>No document with the given id exists in the collection.</summary>
    NotFound,
    /// <summary>A condition did not hold; nothing was written.</summary>
    ConditionFailed,
}

/// <summary>Outcome of <see cref="DocumentForgeDb.TryConditionalUpdate"/>.</summary>
public sealed record ConditionalUpdateResult(
    ConditionalUpdateStatus Status,
    string? Etag = null,
    BsonDocument? Document = null,
    string? FailedCondition = null)
{
    public bool Succeeded => Status == ConditionalUpdateStatus.Success;

    public static readonly ConditionalUpdateResult NotFound =
        new(ConditionalUpdateStatus.NotFound);

    public static ConditionalUpdateResult Failed(string which) =>
        new(ConditionalUpdateStatus.ConditionFailed, FailedCondition: which);

    public static ConditionalUpdateResult Ok(string etag, BsonDocument doc) =>
        new(ConditionalUpdateStatus.Success, etag, doc);
}
