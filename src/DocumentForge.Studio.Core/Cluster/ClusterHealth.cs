using DocumentForge.Studio.Core.Connections;

namespace DocumentForge.Studio.Core.Cluster;

/// <summary>Reachability + health of a single shard endpoint.</summary>
public sealed record ShardHealth(bool Reachable, bool Healthy, string Status, string? Version, string? Error)
{
    public static ShardHealth Down(string error) => new(false, false, "unreachable", null, error);
}

/// <summary>Pings shard endpoints over HTTP (the same <c>/health</c> the engine
/// exposes) so Studio can show cluster status without shelling out to
/// <c>dfdb health</c>.</summary>
public static class ClusterHealth
{
    /// <summary>Best-effort: the collection names on a shard's default database,
    /// so the cluster editor can suggest them. Empty on any failure.</summary>
    public static async Task<IReadOnlyList<string>> TryGetCollectionsAsync(string endpoint, TimeSpan timeout, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return Array.Empty<string>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var descriptor = new ConnectionDescriptor { Name = endpoint, Kind = ConnectionKind.Http, Url = endpoint };
            await using var connection = new HttpConnection(descriptor, apiKey: null);
            var dbs = await connection.GetDatabasesAsync(cts.Token);
            var target = dbs.FirstOrDefault(d => d.IsDefault) ?? dbs.FirstOrDefault();
            return target is null ? Array.Empty<string>() : await connection.GetCollectionNamesAsync(target.Name, cts.Token);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static async Task<ShardHealth> PingAsync(string endpoint, TimeSpan timeout, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return ShardHealth.Down("no endpoint");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var descriptor = new ConnectionDescriptor
            {
                Name = endpoint,
                Kind = ConnectionKind.Http,
                Url = endpoint,
            };
            await using var connection = new HttpConnection(descriptor, apiKey: null);
            var health = await connection.GetHealthAsync(cts.Token);
            return new ShardHealth(true, health.Healthy, health.Status, health.Version, health.Detail);
        }
        catch (OperationCanceledException)
        {
            return ShardHealth.Down("timed out");
        }
        catch (Exception ex)
        {
            return ShardHealth.Down(ex.Message);
        }
    }
}
