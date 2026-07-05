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
        _http.Timeout = TimeSpan.FromSeconds(30);
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
