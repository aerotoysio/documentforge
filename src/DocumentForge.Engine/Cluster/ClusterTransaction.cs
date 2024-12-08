using DocumentForge.Core;
using DocumentForge.Document;
using DocumentForge.Index;
using DocumentForge.Transactions;

namespace DocumentForge.Engine.Cluster;

/// <summary>
/// A multi-document transaction over a sharded cluster. Stages writes in
/// memory until <see cref="Commit"/>; routing happens at staging time so
/// the cluster knows which shard(s) each op targets.
///
/// <para>
/// Phase A (this commit): single-shard fast path only. If every staged op
/// routes to the same shard, <see cref="Commit"/> hands the batch to that
/// shard's local <c>BeginTransaction().Commit()</c> — same atomicity, same
/// validation, same WAL fsync as a non-cluster transaction. If staged ops
/// span more than one shard, <see cref="Commit"/> throws
/// <see cref="NotImplementedException"/>; cross-shard 2PC lands in Phase B
/// of issue #14.
/// </para>
///
/// <para>
/// Phase A only exposes <see cref="Insert(string, BsonDocument)"/> and
/// <see cref="Find"/>. Replace, Delete-by-id, and DeleteByField require a
/// doc-location lookup (or shard-key-aware semantics) that the in-memory
/// shard router doesn't have without an extra round-trip — they ship in
/// Phase B alongside the multi-shard machinery.
/// </para>
///
/// <para>
/// Performance contract: this type is only allocated when a caller asks
/// for <c>cluster.BeginTransaction()</c>. Non-transactional cluster ops
/// (<c>cluster.Insert</c>, <c>cluster.Execute</c>) do not touch any of this
/// code, so their hot path is unchanged.
/// </para>
/// </summary>
public sealed class ClusterTransaction : IDisposable
{
    private readonly DocumentForgeCluster _cluster;
    private readonly Dictionary<int, List<ShardTxOp>> _opsByShard = new();
    private readonly Dictionary<DocumentId, BsonDocument> _stagedInsertsById = new();

    public Guid Id { get; }
    public TransactionState State { get; private set; } = TransactionState.Active;

    internal ClusterTransaction(DocumentForgeCluster cluster)
    {
        _cluster = cluster;
        Id = Guid.NewGuid();
    }

    private void EnsureActive()
    {
        if (State != TransactionState.Active)
            throw new TransactionException($"ClusterTransaction {Id} is {State}; no further operations allowed.");
    }

    private void AddOp(int shardIndex, ShardTxOp op)
    {
        if (!_opsByShard.TryGetValue(shardIndex, out var list))
        {
            list = new List<ShardTxOp>();
            _opsByShard[shardIndex] = list;
        }
        list.Add(op);
    }

    /// <summary>
    /// Stage an insert. The doc's shard key is extracted now and used to
    /// pick a participant — staging late (at Commit) would mean Commit
    /// could fail in different ways depending on the doc body, which
    /// muddies the failure model.
    /// </summary>
    public DocumentId Insert(string collection, BsonDocument doc)
    {
        EnsureActive();
        var policy = _cluster.GetPolicyForTx(collection);
        if (policy.Strategy == ShardingStrategy.Replicated)
            throw new NotImplementedException(
                $"Cluster transactions on replicated collection '{collection}' will land in Phase B of issue #14 — every shard becomes a participant, which needs the cross-shard machinery.");
        if (policy.ShardKeyPath is null)
            throw new DocumentForgeException($"Collection '{collection}' has no shard key configured.");

        doc.EnsureId();
        var id = doc.GetId();

        var keyValue = JsonPathExtractor.Extract(doc, policy.ShardKeyPath);
        if (keyValue.IsNull)
            throw new DocumentForgeException(
                $"Document missing shard key '{policy.ShardKeyPath}' for collection '{collection}'.");

        var shardIdx = _cluster.PickShardForTx(keyValue);
        AddOp(shardIdx, ShardTxOp.ForInsert(collection, doc));
        _stagedInsertsById[id] = doc;
        return id;
    }

    public DocumentId Insert(string collection, string json) =>
        Insert(collection, BsonDocument.FromJson(json));

    /// <summary>
    /// Stage a replace by _id. The new doc's shard key is extracted to
    /// route the op — implicit constraint: the new doc must hash to the
    /// same shard as the existing doc. Changing the shard-key value
    /// would require a cross-shard delete-then-insert (a different
    /// operation); we don't currently support that. The participant
    /// validates at PREPARE time and throws if the doc doesn't exist.
    /// </summary>
    public bool Replace(string collection, DocumentId id, BsonDocument newDoc)
    {
        EnsureActive();
        var policy = _cluster.GetPolicyForTx(collection);
        if (policy.Strategy == ShardingStrategy.Replicated)
            throw new NotImplementedException(
                $"Cluster transactions on replicated collection '{collection}' aren't supported yet (issue #14).");
        if (policy.ShardKeyPath is null)
            throw new DocumentForgeException($"Collection '{collection}' has no shard key configured.");

        newDoc.SetId(id);
        var keyValue = JsonPathExtractor.Extract(newDoc, policy.ShardKeyPath);
        if (keyValue.IsNull)
            throw new DocumentForgeException(
                $"Replacement doc missing shard key '{policy.ShardKeyPath}' for collection '{collection}'.");

        var shardIdx = _cluster.PickShardForTx(keyValue);

        // If we previously staged an Insert on this shard for the same id,
        // overwrite it — same single-node Replace semantics.
        if (_stagedInsertsById.TryGetValue(id, out _))
        {
            _stagedInsertsById[id] = newDoc;
            // Replace the existing Insert op in the per-shard list. We
            // could iterate, but for simplicity just append a Replace op
            // — the participant's working-set logic merges them at apply
            // time.
        }

        AddOp(shardIdx, ShardTxOp.ForReplace(collection, id, newDoc));
        return true;
    }

    public bool Replace(string collection, DocumentId id, string json) =>
        Replace(collection, id, BsonDocument.FromJson(json));

    /// <summary>
    /// Stage deletes by field=value. If <paramref name="field"/> is the
    /// collection's shard key path, the op routes to the one shard that
    /// owns that key value (single-shard tx). Otherwise the matching
    /// docs could be on any shard, so the op fans out to all shards
    /// (multi-shard 2PC tx). Each participant's PREPARE phase scans
    /// its local data and stages the actual deletes.
    /// </summary>
    public int DeleteByField(string collection, string field, string value)
    {
        EnsureActive();
        var policy = _cluster.GetPolicyForTx(collection);
        if (policy.Strategy == ShardingStrategy.Replicated)
            throw new NotImplementedException(
                $"Cluster transactions on replicated collection '{collection}' aren't supported yet (issue #14).");

        if (policy.ShardKeyPath is not null &&
            string.Equals(field, policy.ShardKeyPath, StringComparison.OrdinalIgnoreCase))
        {
            // Single-shard fast path: the field IS the shard key, so we
            // know exactly which shard owns the matching docs.
            var keyValue = BsonValue.FromString(value);
            var shardIdx = _cluster.PickShardForTx(keyValue);
            AddOp(shardIdx, ShardTxOp.ForDeleteByField(collection, field, value));
            return -1;  // staging-time count not known; reported at commit
        }

        // Scatter: any shard might have matching docs. Stage on every
        // shard — participants with no matches no-op cleanly.
        for (int i = 0; i < _cluster.ShardCount; i++)
        {
            AddOp(i, ShardTxOp.ForDeleteByField(collection, field, value));
        }
        return -1;
    }

    /// <summary>
    /// Read by _id. Returns staged inserts/replaces from this transaction.
    /// Cluster-wide reads of committed state aren't done at staging time
    /// because they'd cost a network round-trip — call <c>cluster.Execute</c>
    /// outside the transaction if you need to read committed state.
    /// </summary>
    public BsonDocument? Find(string collection, DocumentId id)
    {
        EnsureActive();
        return _stagedInsertsById.TryGetValue(id, out var doc) ? doc : null;
    }

    public void Commit()
    {
        EnsureActive();
        try
        {
            if (_opsByShard.Count == 0)
            {
                // No-op commit — match single-node semantics.
                State = TransactionState.Committed;
                return;
            }

            if (_opsByShard.Count == 1)
            {
                // Single-shard fast path. Direct local commit on the participant —
                // no PREPARE round-trip needed since there's only one participant.
                var entry = _opsByShard.First();
                _cluster.ExecuteOnShardForTx(entry.Key, entry.Value);
                State = TransactionState.Committed;
                return;
            }

            // Multi-shard: drive 2PC across the participating shards.
            CommitMultiShard();
            State = TransactionState.Committed;
        }
        catch
        {
            State = TransactionState.RolledBack;
            throw;
        }
    }

    /// <summary>
    /// 2PC across the participating shards.
    ///
    /// <para>
    /// Phase B.2 happy path: PREPARE each participant in shard-id-sorted
    /// order; if any returns ABORT, broadcast ROLLBACK to the ones that
    /// did PREPARE and surface the abort reason. If all PREPARED, broadcast
    /// COMMIT.
    /// </para>
    ///
    /// <para>
    /// Crash semantics in Phase B: if the coordinator (this client) dies
    /// after some participants are PREPARED but before COMMIT/ROLLBACK is
    /// broadcast, those participants stay PREPARED with their write lock
    /// held — readers/writers on those shards block until manual cleanup.
    /// Phase C will add the participant prepared-tx-log replay path and
    /// the operator-facing forced-abort endpoint to fix this.
    /// </para>
    ///
    /// <para>
    /// Coordinator shard selection: the participant with the lowest shard
    /// index. Phase C will use this to pick where the persisted decision
    /// log lives. For B.2 we just record the name in the PREPARE message
    /// so participants can later (Phase C) ask the coordinator shard for
    /// the decision on restart.
    /// </para>
    /// </summary>
    private void CommitMultiShard()
    {
        var shardIndices = _opsByShard.Keys.OrderBy(i => i).ToList();
        var coordinatorShardId = _cluster.GetShardNameForTx(shardIndices[0]);
        var txId = Id.ToString("N");

        // PREPARE phase. Sequential — the participants are independent so
        // parallelism is a perf optimization for later (Phase E). Sequential
        // also gives us a clean abort: as soon as one shard says NO we stop
        // calling PREPARE on the rest and only need to ROLLBACK the YESes
        // we've already collected.
        var preparedShards = new List<int>();
        string? abortReason = null;
        int abortShardIdx = -1;
        foreach (var shardIdx in shardIndices)
        {
            var ops = _opsByShard[shardIdx];
            var result = _cluster.PrepareOnShardForTx(shardIdx, txId, coordinatorShardId, ops);
            if (result.Vote == PrepareVote.Prepared)
            {
                preparedShards.Add(shardIdx);
            }
            else
            {
                abortReason = result.AbortReason;
                abortShardIdx = shardIdx;
                break;
            }
        }

        if (abortReason is not null)
        {
            // ROLLBACK every participant we successfully PREPARED. We
            // best-effort each one — a rollback failure on one participant
            // shouldn't prevent us from rolling back the others. The first
            // failure's reason is what we surface to the caller.
            foreach (var preparedIdx in preparedShards)
            {
                try { _cluster.RollbackOnShardForTx(preparedIdx, txId); }
                catch { /* best effort during abort */ }
            }
            throw new TransactionException(
                $"ClusterTransaction {Id} aborted on shard '{_cluster.GetShardNameForTx(abortShardIdx)}': {abortReason}");
        }

        // The point of no return: durably record COMMIT_DECISION on the
        // coordinator shard BEFORE broadcasting COMMIT. If we crash here
        // without the record, recovery (Phase C.2) will see PREPARED txns
        // on the participants and no decision in the coordinator log,
        // and treat them as aborted. If we crash AFTER the record but
        // before broadcasting, recovery replays the COMMIT broadcast.
        var coordinatorShardIdx = shardIndices[0];
        _cluster.RecordCoordinatorDecisionForTx(coordinatorShardIdx, txId, commit: true);

        // All voted PREPARED — commit each one. After this loop returns the
        // tx is applied on every participant. If the coordinator dies in
        // the middle of this loop (between two CommitPrepared calls), some
        // participants are committed and some still PREPARED — Phase C.2's
        // recovery sees the COMMIT_DECISION and replays the broadcast.
        foreach (var preparedIdx in preparedShards)
        {
            _cluster.CommitOnShardForTx(preparedIdx, txId);
        }

        // All participants ACK'd COMMIT — record DONE so recovery doesn't
        // bother replaying the broadcast on a future restart. (DONE is an
        // optimization, not a correctness requirement: replaying COMMIT
        // for an already-committed tx is a no-op on each participant
        // since the prepared.log RESOLVED record makes it not in-flight.)
        _cluster.RecordCoordinatorDoneForTx(coordinatorShardIdx, txId);
    }

    public void Rollback()
    {
        if (State != TransactionState.Active)
            throw new TransactionException($"Cannot rollback ClusterTransaction {Id}: state is {State}");
        // Phase A has no persisted coordinator log yet — rollback is just
        // discarding the in-memory staged ops. Phase C adds the durable
        // log + recovery story.
        State = TransactionState.RolledBack;
    }

    public void Dispose()
    {
        if (State == TransactionState.Active) Rollback();
    }

    public int StagedOperationCount =>
        _opsByShard.Values.Sum(list => list.Count);

    /// <summary>Number of distinct shards this tx has staged ops on. Useful for tests and diagnostics.</summary>
    public int ParticipantCount => _opsByShard.Count;
}
