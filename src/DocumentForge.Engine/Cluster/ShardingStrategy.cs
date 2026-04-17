namespace DocumentForge.Engine.Cluster;

/// <summary>
/// How a collection is distributed across shards.
/// </summary>
public enum ShardingStrategy
{
    /// <summary>Every shard holds a full copy. Ideal for small reference data
    /// (countries, airports, config) so joins stay local.</summary>
    Replicated,

    /// <summary>Each document lives on exactly one shard, picked by hashing
    /// the shard key value. Queries with shardKey equality go to one shard;
    /// queries without scatter to all.</summary>
    Hash,
}

/// <summary>
/// Configuration for how a single collection is distributed.
/// </summary>
public sealed class CollectionShardingPolicy
{
    public string CollectionName { get; init; } = "";
    public ShardingStrategy Strategy { get; init; }
    /// <summary>JSON path to the shard key. Required for Hash strategy. E.g. "pnr" or "account.id".</summary>
    public string? ShardKeyPath { get; init; }
}
