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

    public void Dispose()
    {
        if (_ownsDb) _db.Dispose();
    }
}
