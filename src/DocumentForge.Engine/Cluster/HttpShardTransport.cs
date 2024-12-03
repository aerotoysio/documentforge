using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DocumentForge.Core;
using DocumentForge.Document;
using DocumentForge.Query;

namespace DocumentForge.Engine.Cluster;

/// <summary>
/// Shard transport that talks to a remote DocumentForge.Api instance over HTTP.
///
/// Pairs with the DocumentForge.Api sample - the same endpoints the REPL and
/// Postman hit. Each shard in a distributed cluster runs its own Api process;
/// the cluster router creates an HttpShardTransport per shard.
/// </summary>
public sealed class HttpShardTransport : IShardTransport
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public string ShardName { get; }
    public Uri BaseAddress => _client.BaseAddress!;

    public HttpShardTransport(string shardName, string baseUrl, HttpClient? httpClient = null, string? apiKey = null)
    {
        ShardName = shardName;
        if (httpClient is null)
        {
            _client = new HttpClient { BaseAddress = new Uri(baseUrl) };
            _ownsClient = true;
        }
        else
        {
            _client = httpClient;
            _client.BaseAddress ??= new Uri(baseUrl);
            _ownsClient = false;
        }
        if (!string.IsNullOrEmpty(apiKey))
            _client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    }

    public QueryResult Execute(string sql)
    {
        var request = new { sql };
        var response = _client.PostAsJsonAsync("/query", request).GetAwaiter().GetResult();
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        if (!response.IsSuccessStatusCode)
            return QueryResult.Error($"[{ShardName}] HTTP {(int)response.StatusCode}: {body}");

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        var docs = new List<BsonDocument>();
        if (root.TryGetProperty("documents", out var docsEl) && docsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in docsEl.EnumerateArray())
                docs.Add(BsonDocument.FromJson(d.GetRawText()));
        }

        var plan = root.TryGetProperty("plan", out var p) ? p.GetString() : null;
        var affected = root.TryGetProperty("affected", out var a) && a.ValueKind == JsonValueKind.Number
            ? a.GetInt64() : 0;
        var execMs = root.TryGetProperty("executionTimeMs", out var t) && t.ValueKind == JsonValueKind.Number
            ? t.GetDouble() : 0;

        return new QueryResult
        {
            Documents = docs,
            AffectedCount = affected,
            QueryPlan = plan,
            Success = true,
            ExecutionTime = TimeSpan.FromMilliseconds(execMs)
        };
    }

    public DocumentId Insert(string collectionName, BsonDocument doc)
    {
        // Make sure _id is set BEFORE sending so the server uses our id (not its own)
        doc.EnsureId();
        var id = doc.GetId();
        var json = doc.ToJson();
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = _client.PostAsync($"/collections/{collectionName}", content).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            var err = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            throw new DocumentForgeException($"[{ShardName}] Insert failed: HTTP {(int)response.StatusCode}: {err}");
        }
        return id;
    }

    public bool DeleteById(string collectionName, DocumentId id)
    {
        var response = _client.DeleteAsync($"/collections/{collectionName}/{id}").GetAwaiter().GetResult();
        return response.IsSuccessStatusCode;
    }

    public void ExecuteTransaction(IReadOnlyList<ShardTxOp> ops) =>
        // Phase B will add the wire endpoint (POST /tx/single-shard) and the
        // PREPARE/COMMIT handlers for cross-shard 2PC. Until then, cluster
        // transactions only work with in-process shards.
        throw new NotSupportedException(
            $"[{ShardName}] HttpShardTransport.ExecuteTransaction is not implemented yet — cluster transactions over HTTP land in Phase B of issue #14.");

    public DatabaseStatistics GetStatistics()
    {
        var response = _client.GetAsync("/stats").GetAwaiter().GetResult();
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
            throw new DocumentForgeException($"[{ShardName}] Stats failed: HTTP {(int)response.StatusCode}: {body}");

        // Minimal parsing - we don't need every field round-tripped perfectly
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var stats = new DatabaseStatistics
        {
            FilePath = root.TryGetProperty("filePath", out var fp) ? (fp.GetString() ?? "") : "",
            FileSize = root.TryGetProperty("fileSizeMb", out var fs) && fs.ValueKind == JsonValueKind.Number
                ? (long)(fs.GetDouble() * 1024 * 1024) : 0,
            PageCount = root.TryGetProperty("pageCount", out var pc) && pc.ValueKind == JsonValueKind.Number
                ? pc.GetUInt32() : 0,
            CachedPages = root.TryGetProperty("cachedPages", out var cp) && cp.ValueKind == JsonValueKind.Number
                ? cp.GetInt32() : 0
        };
        return stats;
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }
}
