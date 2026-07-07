using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DocumentForge.Studio.Core.Cluster;

public sealed record RebalanceMove(string From, string To, long Count);

public sealed record RebalanceCollectionPlan(
    string Name, string? ShardKeyPath, long EstimatedMovedDocs, IReadOnlyList<RebalanceMove> Moves);

public sealed record RebalancePlanInfo(long TotalDocumentsToMove, IReadOnlyList<RebalanceCollectionPlan> Collections);

public sealed record RebalanceStatusInfo(
    bool Started, bool Running, string? Error,
    long TotalDocumentsToMove, long DocumentsMoved, long MoveFailures,
    string? LastProgress, string? CompletedAtUtc);

/// <summary>Client for a serve node's HTTP rebalance surface (issue #116):
/// plan (dry run), execute (background), status (poll). The node is the
/// coordinator — it scans the old topology's shards and moves documents; the
/// old config defaults to the coordinator's stored cluster.json (#113).</summary>
public static class ClusterRebalance
{
    private static HttpClient Client(string? apiKey, TimeSpan? timeout)
    {
        var http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(120) };
        if (!string.IsNullOrWhiteSpace(apiKey))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return http;
    }

    private static StringContent Body(string newConfigJson, string? oldConfigJson, string? shardApiKey)
    {
        var payload = new Dictionary<string, string> { ["newConfigJson"] = newConfigJson };
        if (oldConfigJson is not null) payload["oldConfigJson"] = oldConfigJson;
        if (!string.IsNullOrWhiteSpace(shardApiKey)) payload["shardApiKey"] = shardApiKey;
        return new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
    }

    public static async Task<RebalancePlanInfo> PlanAsync(
        string coordinatorEndpoint, string newConfigJson,
        string? oldConfigJson = null, string? shardApiKey = null, string? apiKey = null,
        CancellationToken ct = default)
    {
        var baseUrl = coordinatorEndpoint.Trim().TrimEnd('/');
        using var http = Client(apiKey, timeout: null);
        using var body = Body(newConfigJson, oldConfigJson, shardApiKey);
        using var response = await http.PostAsync($"{baseUrl}/admin/rebalance/plan", body, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ExtractError(text, (int)response.StatusCode));

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var collections = new List<RebalanceCollectionPlan>();
        if (root.TryGetProperty("collections", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var c in arr.EnumerateArray())
            {
                var moves = new List<RebalanceMove>();
                if (c.TryGetProperty("moves", out var ms) && ms.ValueKind == JsonValueKind.Array)
                    foreach (var m in ms.EnumerateArray())
                        moves.Add(new RebalanceMove(
                            m.GetProperty("from").GetString() ?? "",
                            m.GetProperty("to").GetString() ?? "",
                            m.GetProperty("count").GetInt64()));
                collections.Add(new RebalanceCollectionPlan(
                    c.GetProperty("name").GetString() ?? "",
                    c.TryGetProperty("shardKeyPath", out var kp) && kp.ValueKind == JsonValueKind.String ? kp.GetString() : null,
                    c.GetProperty("estimatedMovedDocs").GetInt64(),
                    moves));
            }
        return new RebalancePlanInfo(
            root.TryGetProperty("totalDocumentsToMove", out var t) ? t.GetInt64() : 0,
            collections);
    }

    public static async Task ExecuteAsync(
        string coordinatorEndpoint, string newConfigJson,
        string? oldConfigJson = null, string? shardApiKey = null, string? apiKey = null,
        CancellationToken ct = default)
    {
        var baseUrl = coordinatorEndpoint.Trim().TrimEnd('/');
        using var http = Client(apiKey, timeout: null);
        using var body = Body(newConfigJson, oldConfigJson, shardApiKey);
        using var response = await http.PostAsync($"{baseUrl}/admin/rebalance/execute", body, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ExtractError(text, (int)response.StatusCode));
    }

    public static async Task<RebalanceStatusInfo> GetStatusAsync(
        string coordinatorEndpoint, string? apiKey = null, CancellationToken ct = default)
    {
        var baseUrl = coordinatorEndpoint.Trim().TrimEnd('/');
        using var http = Client(apiKey, TimeSpan.FromSeconds(10));
        using var response = await http.GetAsync($"{baseUrl}/admin/rebalance/status", ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ExtractError(text, (int)response.StatusCode));

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        return new RebalanceStatusInfo(
            Started: root.TryGetProperty("started", out var st) && st.ValueKind == JsonValueKind.True,
            Running: root.TryGetProperty("running", out var r) && r.ValueKind == JsonValueKind.True,
            Error: root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null,
            TotalDocumentsToMove: root.TryGetProperty("totalDocumentsToMove", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : 0,
            DocumentsMoved: root.TryGetProperty("documentsMoved", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt64() : 0,
            MoveFailures: root.TryGetProperty("moveFailures", out var mf) && mf.ValueKind == JsonValueKind.Number ? mf.GetInt64() : 0,
            LastProgress: root.TryGetProperty("lastProgress", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null,
            CompletedAtUtc: root.TryGetProperty("completedAtUtc", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null);
    }

    private static string ExtractError(string body, int status)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
                return e.GetString()!;
        }
        catch (JsonException) { }
        return $"Coordinator returned {status}.";
    }
}
