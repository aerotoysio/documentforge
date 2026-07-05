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
