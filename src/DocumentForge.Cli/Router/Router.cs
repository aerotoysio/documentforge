using System.Text.Json;
using DocumentForge.Engine.Cluster;

namespace DocumentForge.Cli.Router;

/// <summary>
/// Issue #66 Phase 6 — the routing brain. Stateless: given a
/// <see cref="ClusterConfig"/> and a request, decides which ring(s)
/// to forward to and merges the result.
///
/// <para>
/// Strategies (mirrored from <see cref="ShardStrategy"/>):
///  - <c>Single</c>: every op goes to shards[0]. Default for undeclared
///     collections so casual workflows ("just attach this DB to the
///     router for now") don't trip on a missing config entry.
///  - <c>Hash</c>: insert routes via the engine's
///     <see cref="ConsistentHashRing"/> over <c>doc[shardKeyPath]</c>;
///     query fans out to every shard and merges by concatenation.
///  - <c>Replicated</c>: insert fans out to every shard's leader;
///     query reads from one shard (least-loaded heuristic = first
///     healthy in config order).
/// </para>
///
/// <para>
/// Hashing (issue #100): the router shares the engine cluster's
/// <see cref="ConsistentHashRing"/> — same vnode construction, same
/// FNV-1a — so a key placed through the router lands on the same
/// shard the engine cluster would pick. The old FNV-1a-modulo-N
/// scheme placed keys differently and silently misrouted mixed
/// deployments. Shard NAMES and <c>virtualNodesPerShard</c> must
/// match the engine's cluster config for placement parity.
/// </para>
/// </summary>
public sealed class ClusterRouter : IDisposable
{
    /// <summary>
    /// Issue #134 — an immutable snapshot of everything a request needs to
    /// route: the config, the per-shard clients, and the hash ring. The router
    /// holds exactly one of these behind a single reference; a hot-reload builds
    /// a fresh snapshot and swaps the reference in one assignment. Every request
    /// method reads the reference ONCE into a local, so a swap mid-request can
    /// never tear (the in-flight request keeps using its captured snapshot).
    /// </summary>
    private sealed class RoutingState
    {
        public ClusterConfig Config { get; }
        public Dictionary<string, RingClient> RingByName { get; }
        public ConsistentHashRing HashRing { get; }

        public RoutingState(ClusterConfig config, Dictionary<string, RingClient> ringByName, ConsistentHashRing hashRing)
        {
            Config = config;
            RingByName = ringByName;
            HashRing = hashRing;
        }
    }

    // The single swappable reference. `volatile` so a reader on another thread
    // always sees the latest published snapshot, never a half-constructed one.
    private volatile RoutingState _state;

    // Ring clients dropped by a hot-reload (a shard was removed or re-pointed).
    // Disposed at shutdown rather than at swap time so an in-flight request that
    // captured the old snapshot can finish against a still-open HttpClient.
    private readonly List<RingClient> _retired = new();
    private readonly object _applyLock = new();

    public ClusterConfig Config => _state.Config;

    public ClusterRouter(ClusterConfig config)
    {
        _state = BuildState(config, previous: null);
    }

    /// <summary>Build a fresh <see cref="RoutingState"/>. When a
    /// <paramref name="previous"/> snapshot is supplied, ring clients for
    /// shards that are unchanged (same name + same leader endpoint) are reused
    /// so a hot-reload doesn't churn live HttpClients or drop their connections.</summary>
    private static RoutingState BuildState(ClusterConfig config, RoutingState? previous)
    {
        config.Validate();
        var ringByName = new Dictionary<string, RingClient>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in config.Shards)
        {
            if (previous is not null
                && previous.RingByName.TryGetValue(s.Name, out var existing)
                && EndpointsEqual(existing.Endpoint, s.Leader!))
            {
                ringByName[s.Name] = existing; // reuse unchanged ring
            }
            else
            {
                ringByName[s.Name] = new RingClient(s.Name, s.Leader!);
            }
        }
        var hashRing = new ConsistentHashRing(
            config.Shards.Select(s => s.Name).ToList(),
            config.VirtualNodesPerShard);
        return new RoutingState(config, ringByName, hashRing);
    }

    private static bool EndpointsEqual(ShardEndpoint a, ShardEndpoint b) =>
        string.Equals(a.BaseUrl, b.BaseUrl, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Database ?? "", b.Database ?? "", StringComparison.Ordinal)
        && string.Equals(a.ApiKey ?? "", b.ApiKey ?? "", StringComparison.Ordinal);

    /// <summary>
    /// Issue #134 — atomically hot-swap the routing config on a live router.
    /// Validates the new config, builds a fresh ring snapshot (reusing unchanged
    /// ring clients), bumps <see cref="ClusterConfig.Version"/> to
    /// <c>previous + 1</c>, and publishes it in a single reference assignment.
    /// Returns the new version. Throws <see cref="InvalidOperationException"/>
    /// (from <see cref="ClusterConfig.Validate"/>) on a structurally bad config,
    /// in which case the live state is left untouched.
    /// </summary>
    public int ApplyConfig(ClusterConfig newConfig)
    {
        lock (_applyLock) // serialise concurrent PUTs; the read path stays lock-free
        {
            var old = _state;
            // Validate + build BEFORE mutating anything, so a bad config can't
            // leave a half-applied ring.
            newConfig.Version = old.Config.Version + 1;
            var next = BuildState(newConfig, previous: old);

            _state = next; // single atomic publish

            // Retire ring clients that weren't carried into the new snapshot.
            foreach (var kv in old.RingByName)
            {
                if (!next.RingByName.TryGetValue(kv.Key, out var carried) || !ReferenceEquals(carried, kv.Value))
                    _retired.Add(kv.Value);
            }
            return newConfig.Version;
        }
    }

    /// <summary>Public clients keyed by shard name. Useful for callers
    /// that want to drive a specific ring directly (health probes,
    /// admin ops). Reflects the current (possibly hot-reloaded) snapshot.</summary>
    public IReadOnlyDictionary<string, RingClient> Rings => _state.RingByName;

    // -------------------------------------------------------------- insert

    /// <summary>
    /// Route an insert per the collection's strategy. Returns the
    /// upstream response from the (single) ring that handled it, or
    /// for replicated collections the response from the first ring
    /// (every ring gets the same insert; the IDs may differ).
    /// </summary>
    public async Task<HttpResponseMessage> InsertAsync(string collection, JsonElement doc, CancellationToken ct = default)
    {
        var state = _state; // snapshot once — a concurrent hot-reload can't tear this request
        var policy = state.Config.FindCollection(collection)
            ?? new CollectionConfig { Name = collection, Strategy = ShardStrategy.Single };

        switch (policy.Strategy)
        {
            case ShardStrategy.Hash:
            {
                var ring = SelectRingByHash(state, doc, policy.ShardKeyPath!);
                return await ring.InsertAsync(collection, doc, ct);
            }
            case ShardStrategy.Replicated:
            {
                // Fan-out. Surface a 5xx if ANY ring rejects — partial
                // replication leaves the cluster in an inconsistent state
                // that's worse than failing closed.
                HttpResponseMessage? firstOk = null;
                Exception? firstErr = null;
                foreach (var (_, ring) in state.RingByName)
                {
                    try
                    {
                        var resp = await ring.InsertAsync(collection, doc, ct);
                        if (!resp.IsSuccessStatusCode)
                        {
                            firstErr ??= new InvalidOperationException(
                                $"Ring '{ring.ShardName}' rejected replicated insert: {(int)resp.StatusCode}");
                        }
                        firstOk ??= resp;
                    }
                    catch (Exception ex) { firstErr ??= ex; }
                }
                if (firstErr is not null) throw firstErr;
                return firstOk!;
            }
            case ShardStrategy.Single:
            default:
            {
                var ring = state.RingByName[state.Config.Shards[0].Name];
                return await ring.InsertAsync(collection, doc, ct);
            }
        }
    }

    // -------------------------------------------------------------- query

    /// <summary>
    /// Run a SELECT across the right ring(s) and merge the result.
    /// For hash collections, fans out to every ring and concatenates
    /// document arrays. For replicated, hits exactly one ring (the
    /// first by config order — health-aware selection is a follow-up).
    /// </summary>
    public async Task<JsonElement> QueryAsync(string sql, CancellationToken ct = default)
    {
        // Best-effort collection extraction — we don't run a real SQL
        // parser here; we just look for a "FROM <name>" token to pick
        // a strategy. INSERT/DELETE/UPDATE that hit the router today
        // get the same fan-out treatment (correct for replicated,
        // hash-key-aware INSERT happens in InsertAsync).
        var state = _state; // snapshot once
        var coll = TryExtractCollectionFromSql(sql);
        var policy = coll is null ? null : state.Config.FindCollection(coll);
        var strategy = policy?.Strategy ?? ShardStrategy.Single;

        switch (strategy)
        {
            case ShardStrategy.Hash:
            {
                // Fan-out + merge documents from every ring.
                var responses = await Task.WhenAll(state.RingByName.Values
                    .Select(r => r.QueryAsync(sql, ct)));
                return MergeQueryResponses(responses);
            }
            case ShardStrategy.Replicated:
            case ShardStrategy.Single:
            default:
            {
                var firstRing = state.RingByName[state.Config.Shards[0].Name];
                return await firstRing.QueryAsync(sql, ct);
            }
        }
    }

    // -------------------------------------------------------------- helpers

    private static RingClient SelectRingByHash(RoutingState state, JsonElement doc, string shardKeyPath)
    {
        var keyValue = ExtractKey(doc, shardKeyPath);
        if (keyValue is null)
            throw new InvalidOperationException(
                $"Hash routing requires '{shardKeyPath}' in the document; not present.");
        var shardName = state.Config.Shards[state.HashRing.PickShardIndex(keyValue)].Name;
        return state.RingByName[shardName];
    }

    /// <summary>The shard a given key value routes to — placement is
    /// identical to <see cref="DocumentForgeCluster"/>'s for the same
    /// shard names and vnode count (issue #100). Reads the current snapshot.</summary>
    public string PickShardNameForKey(string keyValue)
    {
        var state = _state;
        return state.Config.Shards[state.HashRing.PickShardIndex(keyValue)].Name;
    }

    /// <summary>Pull the shard-key value out of the document. Supports
    /// simple top-level paths ("pnr") and dotted paths ("customer.id").
    /// Anything missing returns null.</summary>
    public static string? ExtractKey(JsonElement doc, string path)
    {
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var cursor = doc;
        foreach (var seg in segments)
        {
            if (cursor.ValueKind != JsonValueKind.Object) return null;
            if (!cursor.TryGetProperty(seg, out var next)) return null;
            cursor = next;
        }
        return cursor.ValueKind switch
        {
            JsonValueKind.String => cursor.GetString(),
            JsonValueKind.Number => cursor.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public static string? TryExtractCollectionFromSql(string sql)
    {
        // Regex-free, parser-free, best-effort: look for "FROM <name>"
        // case-insensitively. Real parsing happens engine-side at the
        // ring; here we just need a hint for strategy lookup.
        var idx = sql.IndexOf("FROM", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        idx += 4; // past "FROM"
        // Skip whitespace.
        while (idx < sql.Length && char.IsWhiteSpace(sql[idx])) idx++;
        var start = idx;
        while (idx < sql.Length && (char.IsLetterOrDigit(sql[idx]) || sql[idx] == '_'))
            idx++;
        return idx > start ? sql.Substring(start, idx - start) : null;
    }

    /// <summary>Merge {documents: [...], count, ...} from every ring
    /// into one envelope. Counts sum, documents concatenate, plan/
    /// executionTimeMs come from the slowest ring so the client sees
    /// realistic latency for the whole fan-out.</summary>
    private static JsonElement MergeQueryResponses(JsonElement[] responses)
    {
        var allDocs = new List<JsonElement>();
        int totalCount = 0;
        int totalAffected = 0;
        double maxMs = 0;
        string? plan = null;

        foreach (var r in responses)
        {
            if (r.TryGetProperty("documents", out var docs) && docs.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in docs.EnumerateArray()) allDocs.Add(d);
            }
            if (r.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number)
                totalCount += c.GetInt32();
            if (r.TryGetProperty("affected", out var a) && a.ValueKind == JsonValueKind.Number)
                totalAffected += a.GetInt32();
            if (r.TryGetProperty("executionTimeMs", out var ms) && ms.ValueKind == JsonValueKind.Number)
                maxMs = Math.Max(maxMs, ms.GetDouble());
            if (plan is null && r.TryGetProperty("plan", out var p) && p.ValueKind == JsonValueKind.String)
                plan = p.GetString();
        }

        var merged = new
        {
            success = true,
            count = totalCount,
            affected = totalAffected,
            plan = plan ?? "FAN_OUT_MERGE",
            executionTimeMs = maxMs,
            documents = allDocs,
            shards = responses.Length,
        };
        var json = JsonSerializer.Serialize(merged);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public void Dispose()
    {
        var state = _state;
        foreach (var c in state.RingByName.Values) c.Dispose();
        lock (_applyLock)
        {
            foreach (var c in _retired) c.Dispose(); // ring clients dropped by hot-reloads
            _retired.Clear();
        }
    }
}
