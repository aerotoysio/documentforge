using DocumentForge.Core;
using DocumentForge.Document;
using DocumentForge.Query;

namespace DocumentForge.Engine.Cluster;

/// <summary>
/// A shard transport that wraps a local DocumentForgeDb instance in the same process.
/// Used for testing and for single-machine multi-shard deployments.
/// </summary>
public sealed class InProcessShardTransport : IShardTransport
{
    private readonly DocumentForgeDb _db;
    private readonly bool _ownsDb;

    public string ShardName { get; }

    /// <param name="ownsDb">If true, disposing this transport will dispose the DB too.</param>
    public InProcessShardTransport(string shardName, DocumentForgeDb db, bool ownsDb = false)
    {
        ShardName = shardName;
        _db = db;
        _ownsDb = ownsDb;
    }

    public QueryResult Execute(string sql) => _db.Execute(sql);

    public DocumentId Insert(string collectionName, BsonDocument doc) =>
        _db.Insert(collectionName, doc);

    public DatabaseStatistics GetStatistics() => _db.GetStatistics();

    public bool DeleteById(string collectionName, DocumentId id)
    {
        var coll = _db.GetCollection(collectionName);
        if (coll is null) return false;
        var doc = coll.FindById(id);
        if (doc is null) return false;
        var ok = coll.Delete(id);
        // Must also notify index manager so indexes drop this doc's entries. We go through
        // a small helper on the engine to avoid exposing internals publicly.
        if (ok) _db.NotifyDocDeleted(collectionName, id, doc);
        return ok;
    }

    public void ExecuteTransaction(IReadOnlyList<ShardTxOp> ops)
    {
        if (ops.Count == 0) return;
        using var tx = _db.BeginTransaction();
        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case ShardTxOpKind.Insert:
                    tx.Insert(op.Collection, op.Doc!);
                    break;
                case ShardTxOpKind.DeleteByField:
                    tx.DeleteByField(op.Collection, op.Field!, op.Value!);
                    break;
                default:
                    throw new NotSupportedException($"ShardTxOpKind.{op.Kind} not supported in Phase A");
            }
        }
        tx.Commit();
    }

    public PrepareResult Prepare(string txId, string coordinatorShardId, IReadOnlyList<ShardTxOp> ops) =>
        _db.PrepareTransaction(txId, coordinatorShardId, ops);

    public void CommitPrepared(string txId) => _db.CommitPreparedTransaction(txId);

    public void RollbackPrepared(string txId) => _db.RollbackPreparedTransaction(txId);

    public void RecordCoordinatorDecision(string txId, bool commit) =>
        _db.RecordCoordinatorDecision(txId, commit);

    public void RecordCoordinatorDone(string txId) =>
        _db.RecordCoordinatorDone(txId);

    public IReadOnlyList<PreparedTxRecord> ScanInFlightPrepared() =>
        _db.ScanInFlightPreparedTransactions();

    public CoordinatorTxState? GetCoordinatorTxState(string txId) =>
        _db.GetCoordinatorTxState(txId);

    public void Dispose()
    {
        if (_ownsDb) _db.Dispose();
    }
}
