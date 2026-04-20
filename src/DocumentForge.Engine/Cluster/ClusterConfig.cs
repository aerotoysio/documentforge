using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocumentForge.Engine.Cluster;

/// <summary>
/// Durable description of the cluster - which shards exist, where to reach them,
/// and how each collection is distributed. Persist this as JSON alongside your
/// application config, or store it inside a DocumentForge collection for dogfooding.
///
/// All nodes must agree on this config. On any change (adding a shard, changing a
/// sharding policy), the new version should be distributed to every node before
/// traffic shifts.
/// </summary>
public sealed class ClusterConfig
{
    public int Version { get; set; } = 1;

    public List<ShardDescriptor> Shards { get; set; } = new();

    /// <summary>Policies keyed by collection name.</summary>
    public Dictionary<string, CollectionPolicyDescriptor> Collections { get; set; } = new();

    /// <summary>
    /// Virtual nodes per shard in the consistent hash ring. Higher = more even
    /// distribution but larger config. 150 is a reasonable default (used by Ketama).
    /// </summary>
    public int VirtualNodesPerShard { get; set; } = 150;

    /// <summary>
    /// When non-null, the cluster is mid-migration. Writes route to the current Shards list,
    /// reads check current shards first and fall back to PreviousShards.
    /// </summary>
    public MigrationStateDescriptor? Migration { get; set; }

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string ToJson() => JsonSerializer.Serialize(this, _json);
    public static ClusterConfig FromJson(string json) => JsonSerializer.Deserialize<ClusterConfig>(json, _json)!;
    public void Save(string path) => File.WriteAllText(path, ToJson());
    public static ClusterConfig Load(string path) => FromJson(File.ReadAllText(path));
    public static bool Exists(string path) => File.Exists(path);
}

public sealed class MigrationStateDescriptor
{
    /// <summary>The shard list BEFORE the migration started - used as fallback for reads.</summary>
    public List<ShardDescriptor> PreviousShards { get; set; } = new();

    /// <summary>UTC start timestamp of this migration, for observability.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>Optional human-readable note (e.g. "scale 2->4 prep").</summary>
    public string? Note { get; set; }
}

public sealed class ShardDescriptor
{
    /// <summary>Human-readable shard name, e.g. "dubai", "shard-a".</summary>
    public string Name { get; set; } = "";

    /// <summary>Where to reach this shard. Format depends on transport ("host:port" for TCP/HTTP, file path for in-process).</summary>
    public string Endpoint { get; set; } = "";
}

public sealed class CollectionPolicyDescriptor
{
    public ShardingStrategy Strategy { get; set; }
    public string? ShardKeyPath { get; set; }
}
