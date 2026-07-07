using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DocumentForge.Studio.Core.Cluster;

public sealed record ClusterPushResult(string Endpoint, bool Success, string Detail);

/// <summary>Distributes a cluster config to the nodes it names (issue #113):
/// one PUT /admin/cluster-config per distinct endpoint, so every node stores
/// the same topology. Per-node results — one unreachable node never aborts
/// the rest.</summary>
public static class ClusterPush
{
    /// <summary>Every distinct node base URL a config references (leaders +
    /// followers), in first-seen order.</summary>
    public static IReadOnlyList<string> DistinctEndpoints(ClusterConfig config)
    {
        var seen = new List<string>();
        void Add(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            var normalized = url.Trim().TrimEnd('/');
            if (!seen.Contains(normalized, StringComparer.OrdinalIgnoreCase)) seen.Add(normalized);
        }
        foreach (var s in config.Shards)
        {
            Add(s.LeaderEndpoint);
            foreach (var f in s.Followers) Add(f);
        }
        return seen;
    }

    public static async Task<ClusterPushResult> PushAsync(
        string endpoint, string configJson, string? apiKey = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var baseUrl = endpoint.Trim().TrimEnd('/');
        try
        {
            using var http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(10) };
            if (!string.IsNullOrWhiteSpace(apiKey))
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var content = new StringContent(configJson, Encoding.UTF8, "application/json");
            using var response = await http.PutAsync($"{baseUrl}/admin/cluster-config", content, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return new ClusterPushResult(baseUrl, true, "pushed");
            return new ClusterPushResult(baseUrl, false, ExtractError(body) ?? $"{(int)response.StatusCode} {response.StatusCode}");
        }
        catch (Exception ex)
        {
            return new ClusterPushResult(baseUrl, false, ex.Message);
        }
    }

    private static string? ExtractError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                return e.GetString();
        }
        catch (JsonException) { }
        return null;
    }
}
