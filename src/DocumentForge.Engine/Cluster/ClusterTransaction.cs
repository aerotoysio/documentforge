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
    /// Read by _id. Phase A returns staged inserts only — committed cluster
    /// state isn't queried because it would mean a network round-trip
    /// during staging. Phase B will layer staged state over a real lookup.
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

            if (_opsByShard.Count > 1)
                throw new NotImplementedException(
                    $"ClusterTransaction {Id} staged ops on {_opsByShard.Count} shards. " +
                    $"Cross-shard commit (2PC) lands in Phase B of issue #14.");

            var entry = _opsByShard.First();
            _cluster.ExecuteOnShardForTx(entry.Key, entry.Value);
            State = TransactionState.Committed;
        }
        catch
        {
            State = TransactionState.RolledBack;
            throw;
        }
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
