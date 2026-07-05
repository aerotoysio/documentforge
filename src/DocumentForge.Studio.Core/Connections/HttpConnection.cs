using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentForge.Studio.Core.Models;

namespace DocumentForge.Studio.Core.Connections;

/// <summary>Thrown when the server answers with a non-success status; carries
/// the parsed error message when the body had one.</summary>
public sealed class DfHttpException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public DfHttpException(HttpStatusCode statusCode, string message) : base(message)
        => StatusCode = statusCode;
}

/// <summary>
/// Typed client for a dfdb serve node (local service or remote endpoint).
/// Bearer auth; databases are addressed via the /db/{name}/* scoped routes or
/// the ?database= query parameter on flat routes.
/// </summary>
public sealed class HttpConnection : IDfConnection
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _http;
    private bool _connected;

    public HttpConnection(ConnectionDescriptor descriptor, string? apiKey, HttpMessageHandler? handler = null)
    {
        if (descriptor.Kind != ConnectionKind.Http || string.IsNullOrWhiteSpace(descriptor.Url))
            throw new ArgumentException("Descriptor must be an Http connection with a Url.", nameof(descriptor));
        Descriptor = descriptor;

        var baseUrl = descriptor.Url!.TrimEnd('/') + "/";
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(baseUrl);
        // A generous ceiling; per-query timeouts are enforced by the caller's
        // CancellationToken (the workbench "Timeout (s)" field), which fires first.
        _http.Timeout = TimeSpan.FromMinutes(10);
        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    public ConnectionDescriptor Descriptor { get; }

    public ConnectionCapabilities Capabilities =>
        ConnectionCapabilities.MultiDatabase |
        ConnectionCapabilities.CreateDatabase |
        ConnectionCapabilities.DropDatabase |
        ConnectionCapabilities.ServerAdmin;

    public bool IsConnected => _connected;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        // /health is public; /databases exercises auth so a bad key fails here
        // rather than on first tree expand.
        await GetHealthAsync(ct).ConfigureAwait(false);
        await GetDatabasesAsync(ct).ConfigureAwait(false);
        _connected = true;
    }

    public async Task<IReadOnlyList<DatabaseInfo>> GetDatabasesAsync(CancellationToken ct = default)
    {
        var dto = await GetAsync<DatabasesDto>("databases", ct).ConfigureAwait(false);
        return dto.Databases.Select(d => new DatabaseInfo(d.Name, d.FilePath, d.IsDefault)).ToList();
    }

    public async Task<IReadOnlyList<string>> GetCollectionNamesAsync(string database, CancellationToken ct = default)
    {
        var dto = await GetAsync<CollectionsDto>(
            $"db/{Uri.EscapeDataString(database)}/collections", ct).ConfigureAwait(false);
        return dto.Collections;
    }

    public async Task<IReadOnlyList<IndexInfo>> GetIndexesAsync(string database, string collection, CancellationToken ct = default)
    {
        var dto = await GetAsync<IndexesDto>(
            $"db/{Uri.EscapeDataString(database)}/indexes/{Uri.EscapeDataString(collection)}", ct).ConfigureAwait(false);
        // The list route doesn't count entries (that needs a B-tree walk);
        // per-index entry counts come from /stats when a dashboard needs them.
        return dto.Indexes.Select(i => new IndexInfo(i.Name, i.Path, i.Unique, EntryCount: -1)).ToList();
    }

    public async Task<StudioQueryResult> ExecuteAsync(string database, string sql, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(
            $"db/{Uri.EscapeDataString(database)}/query", new { sql }, Json, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new DfHttpException(response.StatusCode, ExtractError(body, response.StatusCode));

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var documents = new List<string>();
        if (root.TryGetProperty("documents", out var docsEl) && docsEl.ValueKind == JsonValueKind.Array)
            foreach (var d in docsEl.EnumerateArray())
                documents.Add(d.GetRawText());

        return new StudioQueryResult(
            Success: !root.TryGetProperty("success", out var ok) || ok.GetBoolean(),
            Documents: documents,
            AffectedCount: root.TryGetProperty("affected", out var aff) ? aff.GetInt64() : documents.Count,
            Plan: root.TryGetProperty("plan", out var plan) ? plan.GetString() : null,
            ExecutionMs: root.TryGetProperty("executionTimeMs", out var ms) ? ms.GetDouble() : 0,
            Message: root.TryGetProperty("message", out var msg) ? msg.GetString() : null);
    }

    public async Task<DatabaseStats> GetStatsAsync(string database, CancellationToken ct = default)
    {
        var dto = await GetAsync<StatsDto>(
            $"db/{Uri.EscapeDataString(database)}/stats", ct).ConfigureAwait(false);
        return new DatabaseStats(
            dto.FileSizeBytes,
            dto.PageCount,
            dto.CachedPages,
            dto.DirtyPages,
            dto.Collections.Select(c => new CollectionStats(c.Name, c.DocumentCount, c.IndexCount)).ToList());
    }

    public async Task<ServerHealth> GetHealthAsync(CancellationToken ct = default)
    {
        // /health intentionally returns 503 when degraded — read the body either way.
        using var response = await _http.GetAsync("health", ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.ServiceUnavailable)
            throw new DfHttpException(response.StatusCode, ExtractError(body, response.StatusCode));

        var dto = JsonSerializer.Deserialize<HealthDto>(body, Json) ?? new HealthDto();
        return new ServerHealth(
            Healthy: response.IsSuccessStatusCode,
            Status: dto.Status ?? (response.IsSuccessStatusCode ? "ok" : "degraded"),
            Version: dto.Version,
            Detail: dto.Health?.LastFailure);
    }

    public async Task<string> UpdateDocumentAsync(string database, string collection, string id, string json, string expectedEtag, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put,
            $"db/{Uri.EscapeDataString(database)}/collections/{Uri.EscapeDataString(collection)}/{Uri.EscapeDataString(id)}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("If-Match", expectedEtag);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            var (expected, actual) = ExtractEtagConflict(body);
            throw new EtagConflictException(expected, actual, ExtractError(body, response.StatusCode));
        }
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new KeyNotFoundException($"Document '{id}' not found in '{collection}'.");
        if (!response.IsSuccessStatusCode)
            throw new DfHttpException(response.StatusCode, ExtractError(body, response.StatusCode));

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("etag", out var etag) ? etag.GetString() ?? "" : "";
    }

    public async Task DeleteDocumentAsync(string database, string collection, string id, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync(
            $"db/{Uri.EscapeDataString(database)}/collections/{Uri.EscapeDataString(collection)}/{Uri.EscapeDataString(id)}", ct)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new KeyNotFoundException($"Document '{id}' not found in '{collection}'.");
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new DfHttpException(response.StatusCode, ExtractError(body, response.StatusCode));
        }
    }

    public async Task<string> InsertDocumentAsync(string database, string collection, string json, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"db/{Uri.EscapeDataString(database)}/collections/{Uri.EscapeDataString(collection)}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new DfHttpException(response.StatusCode, ExtractError(body, response.StatusCode));
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
    }

    public async Task<bool> DropCollectionAsync(string database, string collection, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete,
            $"db/{Uri.EscapeDataString(database)}/collections/{Uri.EscapeDataString(collection)}");
        request.Headers.TryAddWithoutValidation("X-Confirm", "true");
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new DfHttpException(response.StatusCode, ExtractError(body, response.StatusCode));
        }
        return true;
    }

    public async Task<CompactionInfo> CompactCollectionAsync(string database, string collection, CancellationToken ct = default)
    {
        using var response = await _http.PostAsync(
            $"db/{Uri.EscapeDataString(database)}/admin/compact/{Uri.EscapeDataString(collection)}", content: null, ct)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new DfHttpException(response.StatusCode, ExtractError(body, response.StatusCode));
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return new CompactionInfo(
            root.TryGetProperty("pagesCompacted", out var p) ? p.GetInt64() : 0,
            root.TryGetProperty("bytesReclaimed", out var b) ? b.GetInt64() : 0,
            root.TryGetProperty("timeMs", out var t) ? t.GetDouble() : 0);
    }

    public async Task<IReadOnlyList<ApiKeyInfo>> GetApiKeysAsync(CancellationToken ct = default)
    {
        var body = await GetRawAsync("admin/keys", ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var result = new List<ApiKeyInfo>();
        if (doc.RootElement.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Array)
            foreach (var k in keys.EnumerateArray())
                result.Add(new ApiKeyInfo(
                    k.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    ReadStringArray(k, "scopes"),
                    k.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
                    k.TryGetProperty("createdAt", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null));
        return result;
    }

    public async Task<CreatedApiKey> CreateApiKeyAsync(string? description, IReadOnlyList<string> scopes, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("admin/keys", new { scopes, description }, Json, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new DfHttpException(response.StatusCode, ExtractError(body, response.StatusCode));
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return new CreatedApiKey(
            root.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            root.TryGetProperty("secret", out var s) ? s.GetString() ?? "" : "",
            ReadStringArray(root, "scopes"),
            root.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null);
    }

    public async Task RevokeApiKeyAsync(string id, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync($"admin/keys/{Uri.EscapeDataString(id)}", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new DfHttpException(response.StatusCode, ExtractError(body, response.StatusCode));
        }
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement obj, string prop)
    {
        var list = new List<string>();
        if (obj.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var el in arr.EnumerateArray())
                if (el.ValueKind == JsonValueKind.String) list.Add(el.GetString()!);
        return list;
    }

    public async Task<ReplicationStatus> GetReplicationStatusAsync(string database, CancellationToken ct = default)
    {
        var body = await GetRawAsync($"db/{Uri.EscapeDataString(database)}/replication/status", ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var leader = root.GetProperty("leader");
        var follower = root.TryGetProperty("follower", out var f) ? f : default;

        var followers = new List<ReplicationFollowerInfo>();
        if (leader.TryGetProperty("followers", out var fs) && fs.ValueKind == JsonValueKind.Array)
            foreach (var el in fs.EnumerateArray())
                followers.Add(new ReplicationFollowerInfo(
                    el.TryGetProperty("endpoint", out var ep) ? ep.GetString() ?? "" : "",
                    el.TryGetProperty("httpEndpoint", out var he) && he.ValueKind == JsonValueKind.String ? he.GetString() : null,
                    el.TryGetProperty("worstCaseLagSeq", out var lag) ? (long)lag.GetUInt64() : 0,
                    el.TryGetProperty("connectedAt", out var ca) && ca.ValueKind == JsonValueKind.String ? ca.GetString() : null));

        string? followerLeader = null;
        if (follower.ValueKind == JsonValueKind.Object
            && follower.TryGetProperty("leader", out var fl) && fl.ValueKind == JsonValueKind.Object
            && fl.TryGetProperty("endpoint", out var fle))
            followerLeader = fle.GetString();

        var hasFollower = follower.ValueKind == JsonValueKind.Object;
        return new ReplicationStatus(
            Role: root.TryGetProperty("role", out var r) ? r.GetString() ?? "none" : "none",
            ReadOnly: root.TryGetProperty("readOnly", out var ro) && Truthy(ro),
            CurrentSeq: leader.TryGetProperty("currentSeq", out var cs) ? Int(cs) : 0,
            LeaderPort: leader.TryGetProperty("port", out var lp) && lp.ValueKind == JsonValueKind.Number ? lp.GetInt32() : 0,
            FollowerCount: leader.TryGetProperty("followerCount", out var fc) && fc.ValueKind == JsonValueKind.Number ? fc.GetInt32() : followers.Count,
            Followers: followers,
            FollowerLastAppliedSeq: hasFollower && follower.TryGetProperty("lastAppliedSeq", out var las) ? Int(las) : 0,
            OpsApplied: hasFollower && follower.TryGetProperty("opsApplied", out var oa) ? Int(oa) : 0,
            GapsDetected: hasFollower && follower.TryGetProperty("gapsDetected", out var gd) && Truthy(gd),
            AutoFailoverPromoted: hasFollower && follower.TryGetProperty("autoFailoverPromoted", out var afp) && Truthy(afp),
            FollowerLeaderEndpoint: followerLeader);
    }

    // The replication endpoint mixes types across versions (e.g. gapsDetected as
    // a bool or a count, port as a number or null) — read defensively.
    private static long Int(JsonElement e) => e.ValueKind == JsonValueKind.Number ? (long)e.GetUInt64() : 0;

    private static bool Truthy(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Number => e.TryGetInt64(out var n) && n != 0,
        _ => false,
    };

    public async Task<DatabaseHealthReport> GetDatabaseHealthAsync(string database, CancellationToken ct = default)
    {
        var body = await GetRawAsync($"databases/{Uri.EscapeDataString(database)}/health", ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var collections = root.GetProperty("collections");
        var files = root.GetProperty("files");

        string? lockHolder = null;
        if (root.TryGetProperty("lockHolder", out var lh) && lh.ValueKind == JsonValueKind.Object)
        {
            var pid = lh.TryGetProperty("pid", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32().ToString() : "?";
            var host = lh.TryGetProperty("host", out var h) ? h.GetString() : "?";
            var since = lh.TryGetProperty("openedAtUtc", out var o) ? o.GetString() : null;
            lockHolder = $"pid {pid} on {host}" + (since is null ? "" : $" since {since}");
        }

        return new DatabaseHealthReport(
            HealthStatus: root.TryGetProperty("healthStatus", out var hs) ? hs.GetString() ?? "" : "",
            ReadOnly: root.TryGetProperty("readOnly", out var ro) && ro.GetBoolean(),
            CollectionCount: collections.TryGetProperty("count", out var cc) ? cc.GetInt32() : 0,
            TotalDocuments: collections.TryGetProperty("totalDocuments", out var td) ? td.GetInt64() : 0,
            DataSizeBytes: files.TryGetProperty("dataSizeBytes", out var ds) ? ds.GetInt64() : 0,
            RecoveryLogBytes: files.TryGetProperty("recoveryLogBytes", out var rl) ? rl.GetInt64() : 0,
            WalBytes: files.TryGetProperty("walBytes", out var wb) ? wb.GetInt64() : 0,
            SnapshotMarkerPresent: files.TryGetProperty("snapshotMarkerPresent", out var sm) && sm.GetBoolean(),
            LockHolder: lockHolder,
            Recommendation: root.TryGetProperty("recommendation", out var rec) ? rec.GetString() ?? "healthy" : "healthy",
            RecommendationDetail: root.TryGetProperty("recommendationDetail", out var rd) && rd.ValueKind == JsonValueKind.String ? rd.GetString() : null);
    }

    public Task StartReplicationLeaderAsync(string database, int port, CancellationToken ct = default) =>
        PostReplicationAsync($"db/{Uri.EscapeDataString(database)}/replication/start-leader", new { port }, ct);

    public Task StartReplicationFollowerAsync(string database, string leaderHost, int leaderPort, CancellationToken ct = default) =>
        PostReplicationAsync($"db/{Uri.EscapeDataString(database)}/replication/start-follower", new { host = leaderHost, port = leaderPort }, ct);

    public Task PromoteReplicaAsync(string database, int port, CancellationToken ct = default) =>
        PostReplicationAsync($"db/{Uri.EscapeDataString(database)}/replication/promote", new { port }, ct);

    private async Task PostReplicationAsync(string url, object payload, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(url, payload, Json, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new DfHttpException(response.StatusCode, ExtractError(body, response.StatusCode));
        }
    }

    private async Task<string> GetRawAsync(string relativeUrl, CancellationToken ct)
    {
        using var response = await _http.GetAsync(relativeUrl, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new DfHttpException(response.StatusCode, ExtractError(body, response.StatusCode));
        return body;
    }

    public async Task<DatabaseInfo> CreateDatabaseAsync(string name, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(
            "databases", new { name, createIfMissing = true }, Json, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new DfHttpException(response.StatusCode, ExtractError(body, response.StatusCode));
        var dto = JsonSerializer.Deserialize<DatabaseDto>(body, Json)!;
        return new DatabaseInfo(dto.Name, dto.FilePath, dto.IsDefault);
    }

    public async Task DropDatabaseAsync(string name, bool deleteFiles, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync(
            $"databases/{Uri.EscapeDataString(name)}?deleteFiles={(deleteFiles ? "true" : "false")}", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new DfHttpException(response.StatusCode, ExtractError(body, response.StatusCode));
        }
    }

    private async Task<T> GetAsync<T>(string relativeUrl, CancellationToken ct)
    {
        using var response = await _http.GetAsync(relativeUrl, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new DfHttpException(response.StatusCode, ExtractError(body, response.StatusCode));
        return JsonSerializer.Deserialize<T>(body, Json)
               ?? throw new DfHttpException(response.StatusCode, "Empty response from server.");
    }

    /// <summary>Pulls the expected/actual ETags out of a 412 body
    /// (<c>{ "expected": …, "actual": … }</c>), tolerating their absence.</summary>
    private static (string? Expected, string? Actual) ExtractEtagConflict(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var expected = root.TryGetProperty("expected", out var e) ? e.GetString() : null;
            var actual = root.TryGetProperty("actual", out var a) ? a.GetString() : null;
            return (expected, actual);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string ExtractError(string body, HttpStatusCode status)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var prop in new[] { "error", "message", "detail" })
                    if (doc.RootElement.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String)
                        return el.GetString()!;
        }
        catch (JsonException) { }
        return $"Server returned {(int)status} {status}.";
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        _connected = false;
        return ValueTask.CompletedTask;
    }

    // --- response DTOs (server emits camelCase) ---

    private sealed class DatabasesDto
    {
        public List<DatabaseDto> Databases { get; set; } = new();
    }

    private sealed class DatabaseDto
    {
        public string Name { get; set; } = "";
        public string? FilePath { get; set; }
        public bool IsDefault { get; set; }
    }

    private sealed class CollectionsDto
    {
        public List<string> Collections { get; set; } = new();
    }

    private sealed class IndexesDto
    {
        public List<IndexDto> Indexes { get; set; } = new();
    }

    private sealed class IndexDto
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public bool Unique { get; set; }
    }

    private sealed class StatsDto
    {
        public long FileSizeBytes { get; set; }
        public long PageCount { get; set; }
        public int CachedPages { get; set; }
        public int DirtyPages { get; set; }
        public List<StatsCollectionDto> Collections { get; set; } = new();
    }

    private sealed class StatsCollectionDto
    {
        public string Name { get; set; } = "";
        public long DocumentCount { get; set; }
        public int IndexCount { get; set; }
    }

    private sealed class HealthDto
    {
        public string? Status { get; set; }
        public string? Version { get; set; }
        public HealthDetailDto? Health { get; set; }
    }

    private sealed class HealthDetailDto
    {
        public string? State { get; set; }
        public string? LastFailure { get; set; }
    }
}
