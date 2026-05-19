using System.Net.Http.Json;
using System.Text.Json;

namespace DocumentForge.Cli.Router;

/// <summary>
/// Thin HTTP client around one shard ring endpoint — leader or follower.
/// Owns a pinned <see cref="HttpClient"/>, the path-prefix logic for
/// multi-DB targets, and the auth header if the backing service is
/// API-keyed. Everything the router needs to talk to one slice of the
/// cluster.
///
/// <para>Stateless beyond the HttpClient itself: no caching, no retries,
/// no health tracking. The router orchestrates retries / fallbacks; this
/// class just makes the call.</para>
/// </summary>
public sealed class RingClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ShardEndpoint _endpoint;

    public string ShardName { get; }
    public ShardEndpoint Endpoint => _endpoint;
    public string BaseUrl => _endpoint.BaseUrl.TrimEnd('/');

    public RingClient(string shardName, ShardEndpoint endpoint, TimeSpan? timeout = null)
    {
        ShardName = shardName;
        _endpoint = endpoint;
        _http = new HttpClient
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(15),
        };
        if (!string.IsNullOrEmpty(endpoint.ApiKey))
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
    }

    private string Url(string path) => BaseUrl + _endpoint.PathPrefix + path;

    /// <summary>POST /collections/{name}. Forwarded verbatim — the
    /// router doesn't peek inside the doc payload here, it already
    /// did the shard-key extraction to pick this ring.</summary>
    public async Task<HttpResponseMessage> InsertAsync(string collection, JsonElement doc, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, Url($"/collections/{Uri.EscapeDataString(collection)}"))
        {
            Content = JsonContent.Create(doc),
        };
        return await _http.SendAsync(req, ct);
    }

    /// <summary>POST /query. Returns the parsed JSON response so the
    /// router can merge results across rings for hash-sharded queries.</summary>
    public async Task<JsonElement> QueryAsync(string sql, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, Url("/query"))
        {
            Content = JsonContent.Create(new { sql }),
        };
        var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Ring '{ShardName}' query failed: {(int)resp.StatusCode} {body}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    /// <summary>GET /collections — used for auto-discovery of which
    /// collections exist on this ring.</summary>
    public async Task<List<string>> ListCollectionsAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync(Url("/collections"), ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var arr = doc.RootElement.GetProperty("collections");
        var names = new List<string>(arr.GetArrayLength());
        foreach (var n in arr.EnumerateArray()) names.Add(n.GetString() ?? "");
        return names;
    }

    /// <summary>Cheap health probe — returns true if the backing
    /// service's /health responds 200. Probes the bare base URL,
    /// not the multi-DB scoped path, because /health is service-
    /// wide, not per-DB.</summary>
    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            var resp = await _http.GetAsync($"{BaseUrl}/health", cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public void Dispose() => _http.Dispose();
}
