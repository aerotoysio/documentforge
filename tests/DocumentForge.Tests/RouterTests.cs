using System.Text.Json;
using DocumentForge.Cli.Router;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// Unit tests for the routing brain. Cover the hash-key extraction +
/// shard selection arithmetic directly so failures localise. The
/// HTTP fan-out + end-to-end "two backing services behind a router"
/// flow lives in <see cref="RouterIntegrationTests"/> — slower, real
/// Kestrel hosts, real cross-process replication style.
/// </summary>
public sealed class RouterUnitTests
{
    [Fact]
    public void ClusterConfig_AcceptsBareUrlStringForLegacyCompat()
    {
        // The /cluster page produced this format pre-multi-DB. Has to
        // continue to load without modification — that's the upgrade
        // path for existing configs.
        var json = """
        {
          "shards": [
            { "name": "ring-a", "leader": "http://localhost:5001", "followers": ["http://localhost:5002"] }
          ],
          "collections": [
            { "name": "orders", "strategy": "hash", "shardKeyPath": "pnr" }
          ]
        }
        """;
        var cfg = ClusterConfig.FromJson(json);
        Assert.Equal("http://localhost:5001", cfg.Shards[0].Leader!.BaseUrl);
        Assert.Null(cfg.Shards[0].Leader!.Database);
        Assert.Equal("http://localhost:5002", cfg.Shards[0].Followers[0].BaseUrl);
    }

    [Fact]
    public void ClusterConfig_AcceptsObjectFormWithDatabase()
    {
        // The new multi-DB-aware format: endpoint is an object that
        // names the attached DB on the target service.
        var json = """
        {
          "shards": [
            { "name": "ring-a",
              "leader": { "baseUrl": "http://localhost:5099", "database": "ring_a" },
              "followers": []
            }
          ]
        }
        """;
        var cfg = ClusterConfig.FromJson(json);
        Assert.Equal("ring_a", cfg.Shards[0].Leader!.Database);
        Assert.Equal("/db/ring_a", cfg.Shards[0].Leader!.PathPrefix);
    }

    [Fact]
    public void ClusterConfig_ValidatesShardKeyForHashStrategy()
    {
        var json = """
        {
          "shards": [{ "name": "ring-a", "leader": "http://localhost:5000" }],
          "collections": [{ "name": "orders", "strategy": "hash" }]
        }
        """;
        var ex = Assert.Throws<InvalidOperationException>(() => ClusterConfig.FromJson(json));
        Assert.Contains("shardKeyPath", ex.Message);
    }

    [Fact]
    public void ClusterConfig_ValidatesAtLeastOneShard()
    {
        var json = """{ "shards": [] }""";
        var ex = Assert.Throws<InvalidOperationException>(() => ClusterConfig.FromJson(json));
        Assert.Contains("at least one shard", ex.Message);
    }

    [Fact]
    public void ExtractKey_TopLevelString()
    {
        var doc = JsonDocument.Parse("""{"pnr":"ABC123","amount":42}""").RootElement;
        Assert.Equal("ABC123", ClusterRouter.ExtractKey(doc, "pnr"));
    }

    [Fact]
    public void ExtractKey_DottedPath()
    {
        // Composite shard keys ("customer.id") are common in real
        // OMS schemas — orders shard by customer to keep a single
        // customer's data on one ring.
        var doc = JsonDocument.Parse("""{"customer":{"id":"C-7","name":"Acme"}}""").RootElement;
        Assert.Equal("C-7", ClusterRouter.ExtractKey(doc, "customer.id"));
    }

    [Fact]
    public void ExtractKey_Missing_ReturnsNull()
    {
        var doc = JsonDocument.Parse("""{"pnr":"X"}""").RootElement;
        Assert.Null(ClusterRouter.ExtractKey(doc, "customer.id"));
    }

    [Fact]
    public void Fnv1a32_DistributesEvenly_AcrossThreeShards()
    {
        // Sanity: for a stream of pnr-like strings, FNV-1a should
        // hit each of N shards within a reasonable tolerance. Loose
        // bound — we just want to catch egregious bucket bias.
        const int total = 3000;
        var counts = new int[3];
        for (int i = 0; i < total; i++)
        {
            var h = ClusterRouter.Fnv1a32($"PNR-{i:D5}");
            counts[h % 3u]++;
        }
        var min = counts.Min();
        var max = counts.Max();
        Assert.True(max < min * 1.5,
            $"Hash distribution skewed badly: {counts[0]} / {counts[1]} / {counts[2]}");
    }

    [Fact]
    public void TryExtractCollectionFromSql_ReadsTheFromClause()
    {
        Assert.Equal("orders", ClusterRouter.TryExtractCollectionFromSql("SELECT * FROM orders LIMIT 50"));
        Assert.Equal("orders", ClusterRouter.TryExtractCollectionFromSql("select pnr from orders where status='OK'"));
        Assert.Equal("orders_archive", ClusterRouter.TryExtractCollectionFromSql("SELECT * FROM orders_archive"));
    }

    [Fact]
    public void TryExtractCollectionFromSql_NoFromClause_ReturnsNull()
    {
        Assert.Null(ClusterRouter.TryExtractCollectionFromSql("DELETE FROM"));  // no name after FROM
        Assert.Null(ClusterRouter.TryExtractCollectionFromSql("not-sql"));
    }
}
