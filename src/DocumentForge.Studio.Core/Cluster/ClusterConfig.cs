using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocumentForge.Studio.Core.Cluster;

/// <summary>Client-side model of a DocumentForge <c>cluster.json</c>. Property
/// names, casing, and the string enum match the engine's ClusterConfig exactly,
/// so a file written here loads in <c>dfdb cluster</c> / the router and vice
/// versa. Studio only edits the file — the engine still owns cluster behaviour.</summary>
public sealed class ClusterConfig
{
    public int Version { get; set; } = 1;
    public List<ShardDescriptor> Shards { get; set; } = new();

    /// <summary>Sharding policy keyed by collection name.</summary>
    public Dictionary<string, CollectionPolicyDescriptor> Collections { get; set; } = new();

    public int VirtualNodesPerShard { get; set; } = 150;

    /// <summary>Preserved verbatim across a load/save so Studio never drops an
    /// in-progress migration it doesn't edit.</summary>
    public JsonElement? Migration { get; set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static ClusterConfig FromJson(string json) =>
        JsonSerializer.Deserialize<ClusterConfig>(json, Json) ?? new ClusterConfig();

    public static ClusterConfig Load(string path) => FromJson(File.ReadAllText(path));
    public void Save(string path) => File.WriteAllText(path, ToJson());

    /// <summary>Pull the live cluster config from a running <c>dfdb router</c>'s
    /// <c>GET /cluster/config</c> endpoint, so you can edit what the cluster is
    /// actually running instead of hunting for the file on disk. The router owns
    /// the config; Studio can read it but cannot (yet) push changes back — save
    /// the result to a file and restart the router to apply.</summary>
    public static async Task<ClusterConfig> LoadFromRouterAsync(
        string routerEndpoint, string? apiKey = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(routerEndpoint))
            throw new ArgumentException("Router endpoint is required.", nameof(routerEndpoint));

        var baseUrl = routerEndpoint.Trim().TrimEnd('/');
        using var http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(10) };
        if (!string.IsNullOrWhiteSpace(apiKey))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var json = await http.GetStringAsync($"{baseUrl}/cluster/config", ct);
        return FromJson(json);
    }
}

public sealed class ShardDescriptor
{
    public string Name { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string? Leader { get; set; }
    public List<string> Followers { get; set; } = new();

    /// <summary>Leader if set, else the plain Endpoint — the URL to health-check.</summary>
    [JsonIgnore]
    public string LeaderEndpoint => !string.IsNullOrEmpty(Leader) ? Leader! : Endpoint;
}

public sealed class CollectionPolicyDescriptor
{
    public ShardingStrategy Strategy { get; set; }
    public string? ShardKeyPath { get; set; }
}

// Order matches the engine (Replicated = 0) so the default and JSON names align.
public enum ShardingStrategy
{
    Replicated,
    Hash,
}
