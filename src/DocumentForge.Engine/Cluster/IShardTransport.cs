using DocumentForge.Core;
using DocumentForge.Document;
using DocumentForge.Query;

namespace DocumentForge.Engine.Cluster;

/// <summary>
/// Abstraction for talking to a single shard. Lets the cluster work over any transport
/// (in-process for tests, HTTP for real deployment, TCP for low-latency).
/// </summary>
public interface IShardTransport : IDisposable
{
    string ShardName { get; }
    QueryResult Execute(string sql);
    DocumentId Insert(string collectionName, BsonDocument doc);
    DatabaseStatistics GetStatistics();
    /// <summary>Direct delete by DocumentId. Bypasses SQL so ObjectId comparison is exact.</summary>
    bool DeleteById(string collectionName, DocumentId id);

    /// <summary>
    /// Apply <paramref name="ops"/> on this shard as a single local transaction:
    /// stage all ops, commit atomically, or throw and persist nothing. Used by
    /// <see cref="ClusterTransaction"/> when every staged op routed to this one
    /// shard (the single-shard fast path — no PREPARE/COMMIT round-trip needed
    /// since there's only one participant).
    ///
    /// <para>
    /// Phase A: in-process transports implement this; HTTP throws
    /// <see cref="NotSupportedException"/> until the wire endpoint lands.
    /// </para>
    /// </summary>
    void ExecuteTransaction(IReadOnlyList<ShardTxOp> ops);

    // --- 2PC participant wire ops (issue #14 Phase B) ---

    /// <summary>
    /// Phase 1 of 2PC. Validate <paramref name="ops"/> against the shard's
    /// current state, persist them to the prepared-tx log, and hold the
    /// write lock waiting for <see cref="CommitPrepared"/> or
    /// <see cref="RollbackPrepared"/>. Returns
    /// <see cref="PrepareVote.Prepared"/> on success or
    /// <see cref="PrepareVote.Aborted"/> with a reason on conflict.
    /// </summary>
    PrepareResult Prepare(string txId, string coordinatorShardId, IReadOnlyList<ShardTxOp> ops);

    /// <summary>Phase 2 commit: apply the prepared ops on this shard and release the lock.</summary>
    void CommitPrepared(string txId);

    /// <summary>Phase 2 rollback: drop the prepared ops on this shard and release the lock.</summary>
    void RollbackPrepared(string txId);
}
