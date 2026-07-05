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

    private static ClusterRouter ThreeShardRouter() => new(ClusterConfig.FromJson("""
        {
          "shards": [
            { "name": "ring-a", "leader": "http://localhost:5001" },
            { "name": "ring-b", "leader": "http://localhost:5002" },
            { "name": "ring-c", "leader": "http://localhost:5003" }
          ],
          "collections": [
            { "name": "orders", "strategy": "hash", "shardKeyPath": "pnr" }
          ]
        }
        """));

    [Fact]
    public void HashRouting_MatchesEngineConsistentHashRing()
    {
        // Issue #100 — the router and the engine cluster MUST place the
        // same key on the same shard, or a mixed deployment misroutes.
        // Build the engine ring exactly as DocumentForgeCluster does
        // (same names, same vnode count) and demand placement parity.
        using var router = ThreeShardRouter();
        var engineRing = new DocumentForge.Engine.Cluster.ConsistentHashRing(
            new[] { "ring-a", "ring-b", "ring-c" }, 150);
        var names = new[] { "ring-a", "ring-b", "ring-c" };

        for (int i = 0; i < 2000; i++)
        {
            var key = $"PNR-{i:D5}";
            Assert.Equal(names[engineRing.PickShardIndex(key)], router.PickShardNameForKey(key));
        }
    }

    [Fact]
    public void HashRouting_DistributesEvenly_AcrossThreeShards()
    {
        // Sanity: the consistent-hash ring should hit each of N shards
        // within a reasonable tolerance for pnr-like keys. Loose bound —
        // we just want to catch egregious bucket bias.
        using var router = ThreeShardRouter();
        const int total = 3000;
        var counts = new Dictionary<string, int>();
        for (int i = 0; i < total; i++)
        {
            var shard = router.PickShardNameForKey($"PNR-{i:D5}");
            counts[shard] = counts.GetValueOrDefault(shard) + 1;
        }
        Assert.Equal(3, counts.Count);
        var min = counts.Values.Min();
        var max = counts.Values.Max();
        Assert.True(max < min * 1.6,
            $"Hash distribution skewed badly: {string.Join(" / ", counts.Values)}");
    }

    [Fact]
    public void HashRouting_HonoursConfiguredVirtualNodeCount()
    {
        // virtualNodesPerShard is part of the placement contract — a
        // config that overrides it must produce the matching ring.
        var cfg = ClusterConfig.FromJson("""
        {
          "virtualNodesPerShard": 50,
          "shards": [
            { "name": "ring-a", "leader": "http://localhost:5001" },
            { "name": "ring-b", "leader": "http://localhost:5002" }
          ]
        }
        """);
        Assert.Equal(50, cfg.VirtualNodesPerShard);

        using var router = new ClusterRouter(cfg);
        var engineRing = new DocumentForge.Engine.Cluster.ConsistentHashRing(
            new[] { "ring-a", "ring-b" }, 50);
        var names = new[] { "ring-a", "ring-b" };
        for (int i = 0; i < 500; i++)
        {
            var key = $"K-{i}";
            Assert.Equal(names[engineRing.PickShardIndex(key)], router.PickShardNameForKey(key));
        }
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
