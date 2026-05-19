using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocumentForge.Cli.Router;

/// <summary>
/// Issue #66 Phase 6 — cluster topology + routing policy for the
/// <c>dfdb router</c> stateless proxy. One JSON file describes
/// every shard ring (each with a leader + optional followers) and
/// every collection's distribution strategy (hash / replicated / single).
///
/// <para>
/// A ring "endpoint" is a <c>(baseUrl, database?)</c> pair. The
/// optional <c>database</c> targets a specific attached DB on a
/// multi-DB host (Issue #66 Phase 1+); omitting it routes to the
/// service's default DB, which preserves back-compat with the
/// pre-#66 single-DB topology format the existing /cluster page
/// generates.
/// </para>
///
/// <para>
/// Wire format mirrors the JSON the admin-UI's existing static
/// /cluster page produces, extended for multi-DB. JSON loader is
/// permissive — endpoint as bare URL string is normalised to
/// <c>{ baseUrl: "...", database: null }</c> so old configs Just Work.
/// </para>
/// </summary>
public sealed class ClusterConfig
{
    /// <summary>Router-level settings (port, name). Optional —
    /// CLI flags override these when both are present.</summary>
    public RouterConfig Router { get; set; } = new();

    /// <summary>Shard rings — each one is a leader plus zero or more
    /// followers. Hash-sharded collections distribute documents across
    /// these rings via modular hashing of the shard key.</summary>
    public List<ShardConfig> Shards { get; set; } = new();

    /// <summary>Per-collection distribution policy.</summary>
    public List<CollectionConfig> Collections { get; set; } = new();

    /// <summary>Resolve "orders" to its strategy. Returns null when
    /// the collection isn't declared — the router treats undeclared
    /// collections as <c>strategy: single</c> against the first ring,
    /// so casual dev workflows don't trip on a missing entry.</summary>
    public CollectionConfig? FindCollection(string name) =>
        Collections.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public static ClusterConfig FromJson(string json)
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        // Custom converter accepts endpoint as either a bare URL string
        // or the structured object form.
        opts.Converters.Add(new EndpointConverter());
        // ShardStrategy is serialised as a lowercase string ("hash" /
        // "replicated" / "single") rather than its numeric value —
        // matches the JSON the /cluster page emits and is what
        // operators expect to write by hand.
        opts.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

        var cfg = JsonSerializer.Deserialize<ClusterConfig>(json, opts)
            ?? throw new InvalidOperationException("cluster.json deserialised to null.");

        cfg.Validate();
        return cfg;
    }

    public string ToJson()
    {
        var opts = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Emit camelCase keys to match the convention used by every
            // other endpoint in DocumentForge and the format the
            // /cluster page already generates.
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        opts.Converters.Add(new EndpointConverter());
        opts.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return JsonSerializer.Serialize(this, opts);
    }

    /// <summary>Throw on structural mistakes that would only show up
    /// as 500s mid-request. Cheap upfront beats a 3am page.</summary>
    public void Validate()
    {
        if (Shards.Count == 0)
            throw new InvalidOperationException("cluster.json: at least one shard is required.");
        var seenShardNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Shards)
        {
            if (string.IsNullOrWhiteSpace(s.Name))
                throw new InvalidOperationException("cluster.json: every shard needs a 'name'.");
            if (!seenShardNames.Add(s.Name))
                throw new InvalidOperationException($"cluster.json: duplicate shard name '{s.Name}'.");
            if (s.Leader is null || string.IsNullOrWhiteSpace(s.Leader.BaseUrl))
                throw new InvalidOperationException($"cluster.json: shard '{s.Name}' needs a 'leader.baseUrl'.");
        }
        foreach (var c in Collections)
        {
            if (string.IsNullOrWhiteSpace(c.Name))
                throw new InvalidOperationException("cluster.json: every collection needs a 'name'.");
            if (c.Strategy == ShardStrategy.Hash && string.IsNullOrWhiteSpace(c.ShardKeyPath))
                throw new InvalidOperationException(
                    $"cluster.json: hash-sharded collection '{c.Name}' needs a 'shardKeyPath'.");
        }
    }
}

public sealed class RouterConfig
{
    public int? Port { get; set; }
    public string? Name { get; set; }
}

public sealed class ShardConfig
{
    public string Name { get; set; } = "";
    public ShardEndpoint? Leader { get; set; }
    public List<ShardEndpoint> Followers { get; set; } = new();
}

public sealed class ShardEndpoint
{
    public string BaseUrl { get; set; } = "";

    /// <summary>Optional attached-DB name (multi-DB hosting, Issue #66).
    /// When null, the router targets the service's default DB.</summary>
    public string? Database { get; set; }

    /// <summary>Bearer token if the target service is auth-gated.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Compose the URL path prefix for this endpoint. Multi-DB
    /// targets prefix with <c>/db/{database}</c>; flat targets stay at
    /// the root.</summary>
    public string PathPrefix => string.IsNullOrEmpty(Database) ? string.Empty : $"/db/{Database}";
}

public sealed class CollectionConfig
{
    public string Name { get; set; } = "";
    public ShardStrategy Strategy { get; set; } = ShardStrategy.Single;
    public string? ShardKeyPath { get; set; }
}

/// <summary>
/// How a collection is distributed across the cluster.
/// </summary>
public enum ShardStrategy
{
    /// <summary>Single-shard. Default. Routes everything to the first
    /// shard in the config. Equivalent to running a collection on
    /// only one ring.</summary>
    Single = 0,

    /// <summary>Hash-sharded by <see cref="CollectionConfig.ShardKeyPath"/>.
    /// Documents distribute across all shards by hash modulo shard count.</summary>
    Hash = 1,

    /// <summary>Full copy on every shard. Inserts fan out to every leader;
    /// reads can hit any one (the router picks the cheapest).</summary>
    Replicated = 2,
}

// ----------------------------------------------------------------
// JSON converter: lets the user write a bare URL string instead of
// the object form. Two equivalent representations:
//
//   "leader": "http://localhost:5000"
//   "leader": { "baseUrl": "http://localhost:5000", "database": "ring_a" }
//
// The first is what the legacy /cluster page produces; the second
// is the new multi-DB-aware form.
// ----------------------------------------------------------------
internal sealed class EndpointConverter : JsonConverter<ShardEndpoint>
{
    public override ShardEndpoint? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType == JsonTokenType.String)
        {
            var url = reader.GetString();
            if (string.IsNullOrWhiteSpace(url)) return null;
            return new ShardEndpoint { BaseUrl = url };
        }
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException($"Expected string or object for ShardEndpoint, got {reader.TokenType}.");

        var ep = new ShardEndpoint();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return ep;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var prop = reader.GetString();
            reader.Read();
            switch (prop?.ToLowerInvariant())
            {
                case "baseurl":  ep.BaseUrl  = reader.GetString() ?? ""; break;
                case "database": ep.Database = reader.GetString(); break;
                case "apikey":   ep.ApiKey   = reader.GetString(); break;
                default: reader.Skip(); break;
            }
        }
        throw new JsonException("Unexpected end of object while reading ShardEndpoint.");
    }

    public override void Write(Utf8JsonWriter writer, ShardEndpoint value, JsonSerializerOptions options)
    {
        // Prefer the compact string form when there's nothing to add.
        if (string.IsNullOrEmpty(value.Database) && string.IsNullOrEmpty(value.ApiKey))
        {
            writer.WriteStringValue(value.BaseUrl);
            return;
        }
        writer.WriteStartObject();
        writer.WriteString("baseUrl", value.BaseUrl);
        if (!string.IsNullOrEmpty(value.Database)) writer.WriteString("database", value.Database);
        if (!string.IsNullOrEmpty(value.ApiKey))   writer.WriteString("apiKey",   value.ApiKey);
        writer.WriteEndObject();
    }
}
