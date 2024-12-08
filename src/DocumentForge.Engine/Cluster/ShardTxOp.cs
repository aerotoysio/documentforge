using DocumentForge.Document;

namespace DocumentForge.Engine.Cluster;

public enum ShardTxOpKind
{
    Insert,
    DeleteByField
}

/// <summary>
/// One staged op inside a <see cref="ClusterTransaction"/>, scoped to a
/// single participant shard. The cluster groups these by target shard and
/// hands the per-shard batch to <see cref="IShardTransport.ExecuteTransaction"/>
/// for atomic apply.
///
/// <para>
/// Phase A only carries Insert and DeleteByField. Replace and Delete-by-id
/// land in Phase B alongside cross-shard 2PC, since they need a doc-location
/// lookup that isn't trivial without a shard-key on the call.
/// </para>
/// </summary>
public sealed record ShardTxOp
{
    public ShardTxOpKind Kind { get; init; }
    public string Collection { get; init; } = "";

    // Insert
    public BsonDocument? Doc { get; init; }

    // DeleteByField
    public string? Field { get; init; }
    public string? Value { get; init; }

    public static ShardTxOp ForInsert(string collection, BsonDocument doc) =>
        new() { Kind = ShardTxOpKind.Insert, Collection = collection, Doc = doc };

    public static ShardTxOp ForDeleteByField(string collection, string field, string value) =>
        new() { Kind = ShardTxOpKind.DeleteByField, Collection = collection, Field = field, Value = value };
}
