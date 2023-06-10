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
}
