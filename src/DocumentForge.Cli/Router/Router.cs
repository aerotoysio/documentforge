using System.Text.Json;

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
///  - <c>Hash</c>: insert goes to <c>hash(doc[shardKeyPath]) % N</c>;
///     query fans out to every shard and merges by concatenation.
///  - <c>Replicated</c>: insert fans out to every shard's leader;
///     query reads from one shard (least-loaded heuristic = first
///     healthy in config order).
/// </para>
///
/// <para>
/// Hashing: 32-bit FNV-1a over the UTF-8 bytes of the shard-key value.
/// Chosen for stable distribution + zero dependencies. Stable across
/// platforms because the bytes are produced by the same encoder.
/// </para>
/// </summary>
public sealed class ClusterRouter : IDisposable
{
    private readonly ClusterConfig _config;
    private readonly Dictionary<string, RingClient> _ringByName;

    public ClusterConfig Config => _config;

    public ClusterRouter(ClusterConfig config)
    {
        config.Validate();
        _config = config;
        // One client per shard leader. Followers aren't dialed by the
        // router today — replication handles the read side. A future
        // "read from followers" optimisation will dial them too.
        _ringByName = config.Shards.ToDictionary(
            s => s.Name,
            s => new RingClient(s.Name, s.Leader!),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Public clients keyed by shard name. Useful for callers
    /// that want to drive a specific ring directly (health probes,
    /// admin ops).</summary>
    public IReadOnlyDictionary<string, RingClient> Rings => _ringByName;

    // -------------------------------------------------------------- insert

    /// <summary>
    /// Route an insert per the collection's strategy. Returns the
    /// upstream response from the (single) ring that handled it, or
    /// for replicated collections the response from the first ring
    /// (every ring gets the same insert; the IDs may differ).
    /// </summary>
    public async Task<HttpResponseMessage> InsertAsync(string collection, JsonElement doc, CancellationToken ct = default)
    {
        var policy = _config.FindCollection(collection)
            ?? new CollectionConfig { Name = collection, Strategy = ShardStrategy.Single };

        switch (policy.Strategy)
        {
            case ShardStrategy.Hash:
            {
                var ring = SelectRingByHash(doc, policy.ShardKeyPath!);
                return await ring.InsertAsync(collection, doc, ct);
            }
            case ShardStrategy.Replicated:
            {
                // Fan-out. Surface a 5xx if ANY ring rejects — partial
                // replication leaves the cluster in an inconsistent state
                // that's worse than failing closed.
                HttpResponseMessage? firstOk = null;
                Exception? firstErr = null;
                foreach (var (_, ring) in _ringByName)
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
                var ring = _ringByName[_config.Shards[0].Name];
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
        var coll = TryExtractCollectionFromSql(sql);
        var policy = coll is null ? null : _config.FindCollection(coll);
        var strategy = policy?.Strategy ?? ShardStrategy.Single;

        switch (strategy)
        {
            case ShardStrategy.Hash:
            {
                // Fan-out + merge documents from every ring.
                var responses = await Task.WhenAll(_ringByName.Values
                    .Select(r => r.QueryAsync(sql, ct)));
                return MergeQueryResponses(responses);
            }
            case ShardStrategy.Replicated:
            case ShardStrategy.Single:
            default:
            {
                var firstRing = _ringByName[_config.Shards[0].Name];
                return await firstRing.QueryAsync(sql, ct);
            }
        }
    }

    // -------------------------------------------------------------- helpers

    private RingClient SelectRingByHash(JsonElement doc, string shardKeyPath)
    {
        var keyValue = ExtractKey(doc, shardKeyPath);
        if (keyValue is null)
            throw new InvalidOperationException(
                $"Hash routing requires '{shardKeyPath}' in the document; not present.");
        var hash = Fnv1a32(keyValue);
        var index = (int)(hash % (uint)_config.Shards.Count);
        var ringName = _config.Shards[index].Name;
        return _ringByName[ringName];
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

    /// <summary>FNV-1a 32-bit hash. Cheap, stable, well-distributed
    /// for short keys. The router uses it for shard-index selection;
    /// the engine's index hashing is independent.</summary>
    public static uint Fnv1a32(string s)
    {
        const uint offset = 2166136261u;
        const uint prime = 16777619u;
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        uint h = offset;
        for (int i = 0; i < bytes.Length; i++)
        {
            h ^= bytes[i];
            h *= prime;
        }
        return h;
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
        foreach (var c in _ringByName.Values) c.Dispose();
        _ringByName.Clear();
    }
}
