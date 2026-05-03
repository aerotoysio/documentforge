using DocumentForge.Core;
using DocumentForge.Document;

namespace DocumentForge.Engine.Cluster;

public enum ShardTxOpKind
{
    Insert,
    Replace,
    DeleteByField,
}

/// <summary>
/// One staged op inside a <see cref="ClusterTransaction"/>, scoped to a
/// single participant shard. The cluster groups these by target shard and
/// hands the per-shard batch to <see cref="IShardTransport.ExecuteTransaction"/>
/// (single-shard fast path) or the participant's PREPARE flow (multi-shard).
/// </summary>
public sealed record ShardTxOp
{
    public ShardTxOpKind Kind { get; init; }
    public string Collection { get; init; } = "";

    // Insert / Replace
    public BsonDocument? Doc { get; init; }

    // Replace — id of the existing doc to overwrite
    public DocumentId DocId { get; init; }

    // DeleteByField
    public string? Field { get; init; }
    public string? Value { get; init; }

    public static ShardTxOp ForInsert(string collection, BsonDocument doc) =>
        new() { Kind = ShardTxOpKind.Insert, Collection = collection, Doc = doc };

    public static ShardTxOp ForReplace(string collection, DocumentId id, BsonDocument doc) =>
        new() { Kind = ShardTxOpKind.Replace, Collection = collection, DocId = id, Doc = doc };

    public static ShardTxOp ForDeleteByField(string collection, string field, string value) =>
        new() { Kind = ShardTxOpKind.DeleteByField, Collection = collection, Field = field, Value = value };
}
