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
    ///
    /// <para>
    /// If <paramref name="timeout"/> elapses before COMMIT or ROLLBACK
    /// arrives, the participant self-aborts: it releases the write lock
    /// and writes a RESOLVED-aborted record to the prepared-tx log. A
    /// late <see cref="CommitPrepared"/> for that txId then throws.
    /// This is the Phase D liveness fix — without it, a coordinator
    /// crash before broadcasting COMMIT would freeze the participant
    /// indefinitely.
    /// </para>
    /// </summary>
    PrepareResult Prepare(string txId, string coordinatorShardId, IReadOnlyList<ShardTxOp> ops, TimeSpan timeout);

    /// <summary>
    /// Convenience overload using the default 30-second prepare timeout.
    /// </summary>
    PrepareResult Prepare(string txId, string coordinatorShardId, IReadOnlyList<ShardTxOp> ops) =>
        Prepare(txId, coordinatorShardId, ops, TimeSpan.FromSeconds(30));

    /// <summary>Phase 2 commit: apply the prepared ops on this shard and release the lock.</summary>
    void CommitPrepared(string txId);

    /// <summary>Phase 2 rollback: drop the prepared ops on this shard and release the lock.</summary>
    void RollbackPrepared(string txId);

    /// <summary>
    /// Coordinator-shard call: durably record the COMMIT decision. The
    /// cluster issues this once every participant voted PREPARED and before
    /// it broadcasts COMMIT. After this call returns the decision is the
    /// point of no return — recovery (Phase C.2) replays a COMMIT broadcast
    /// for any txId in this log without a matching DONE.
    /// </summary>
    void RecordCoordinatorDecision(string txId, bool commit);

    /// <summary>Coordinator-shard call: every participant ACK'd COMMIT — record DONE.</summary>
    void RecordCoordinatorDone(string txId);

    // --- Recovery (Phase C.2) ---

    /// <summary>
    /// Return every prepared-tx that has not yet been resolved on this
    /// shard. The cluster's <c>Recover()</c> sweep calls this on each
    /// participant after a crash so it can ask each tx's coordinator for
    /// the decision and finalize.
    /// </summary>
    IReadOnlyList<PreparedTxRecord> ScanInFlightPrepared();

    /// <summary>
    /// Coordinator-shard call: what does this shard's coord.log say about
    /// <paramref name="txId"/>? Returns null if the txId never appeared in
    /// this shard's coord log (which, by convention, means ABORT).
    /// </summary>
    CoordinatorTxState? GetCoordinatorTxState(string txId);
}
