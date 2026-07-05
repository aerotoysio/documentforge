using DocumentForge.Engine;
using DocumentForge.Engine.Cluster;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// Issue #99 — cross-shard query correctness. Exact AVG via (sum,count)
/// decomposition, IN/OR-on-shard-key routed to the owning shards instead of
/// scattered, and cross-shard JOIN/DISTINCT rejected with a clear error rather
/// than returning silently-wrong data.
/// </summary>
public sealed class CrossShardQueryTests : IDisposable
{
    private readonly List<string> _paths = new();
    private readonly List<DocumentForgeDb> _dbs = new();

    private DocumentForgeCluster ThreeShardOrders()
    {
        var cluster = new DocumentForgeCluster();
        for (int i = 0; i < 3; i++)
        {
            var path = Path.Combine(Path.GetTempPath(), $"xsq_{Guid.NewGuid():N}.dfdb");
            _paths.Add(path);
            var db = DocumentForgeDb.Create(path);
            _dbs.Add(db);
            cluster.AddShard(new InProcessShardTransport(((char)('A' + i)).ToString(), db, ownsDb: false));
        }
        cluster.ShardCollection("orders", shardKeyPath: "pnr");
        return cluster;
    }

    [Fact]
    public void CrossShardAvg_IsExact_NotAverageOfAverages()
    {
        using var cluster = ThreeShardOrders();
        // Spread 100 orders with known fares across shards. True average of
        // 1..100 is 50.5 — average-of-shard-averages would differ whenever the
        // shards hold unequal counts (which consistent hashing guarantees).
        double sum = 0;
        for (int i = 1; i <= 100; i++)
        {
            cluster.Insert("orders", $$"""{"pnr":"P{{i:D4}}","fare":{{i}}}""");
            sum += i;
        }
        double expected = sum / 100.0;

        // The dialect auto-aliases aggregates as "FUNC(path)" (no AS keyword).
        var r = cluster.Execute("SELECT AVG(fare) FROM orders");
        Assert.True(r.Success);
        Assert.Single(r.Documents);
        Assert.Equal(expected, r.Documents[0]["AVG(fare)"].AsDouble, 6);
    }

    [Fact]
    public void CrossShardAvg_GroupBy_IsExactPerGroup()
    {
        using var cluster = ThreeShardOrders();
        // Two classes; distinct fare sets so the per-group averages are known.
        var econ = new[] { 100.0, 200.0, 300.0 };  // avg 200
        var biz = new[] { 1000.0, 2000.0 };         // avg 1500
        int n = 0;
        foreach (var f in econ) cluster.Insert("orders", $$"""{"pnr":"E{{n++}}","cls":"econ","fare":{{f}}}""");
        foreach (var f in biz) cluster.Insert("orders", $$"""{"pnr":"B{{n++}}","cls":"biz","fare":{{f}}}""");

        var r = cluster.Execute("SELECT cls, AVG(fare) FROM orders GROUP BY cls");
        Assert.True(r.Success);
        var byCls = r.Documents.ToDictionary(d => d["cls"].AsString, d => d["AVG(fare)"].AsDouble);
        Assert.Equal(200.0, byCls["econ"], 6);
        Assert.Equal(1500.0, byCls["biz"], 6);
    }

    [Fact]
    public void CrossShardSumCountMinMax_StillCorrect()
    {
        using var cluster = ThreeShardOrders();
        for (int i = 1; i <= 50; i++)
            cluster.Insert("orders", $$"""{"pnr":"P{{i:D4}}","fare":{{i}}}""");

        var r = cluster.Execute("SELECT SUM(fare), COUNT(*), MIN(fare), MAX(fare) FROM orders");
        var d = r.Documents[0];
        Assert.Equal(1275.0, d["SUM(fare)"].AsDouble, 6);   // 1..50
        Assert.Equal(50, d["COUNT(*)"].AsInt64);
        Assert.Equal(1.0, d["MIN(fare)"].AsDouble, 6);
        Assert.Equal(50.0, d["MAX(fare)"].AsDouble, 6);
    }

    [Fact]
    public void InOnShardKey_RoutesToOwningShards_NotAll()
    {
        using var cluster = ThreeShardOrders();
        for (int i = 0; i < 30; i++)
            cluster.Insert("orders", $$"""{"pnr":"PNR{{i:D3}}","amount":{{i}}}""");

        var r = cluster.Execute("SELECT * FROM orders WHERE pnr IN ('PNR001','PNR002','PNR003')");
        Assert.True(r.Success);
        Assert.Equal(3, r.Documents.Count);
        // Routed to a subset — plan is SINGLE_SHARD or MULTI_SHARD, never a full scatter.
        Assert.DoesNotContain("SCATTER_GATHER", r.QueryPlan!);
        Assert.True(r.QueryPlan!.Contains("SINGLE_SHARD") || r.QueryPlan.Contains("MULTI_SHARD"),
            $"expected routed plan, got {r.QueryPlan}");
    }

    [Fact]
    public void OrOnShardKey_RoutesToOwningShards()
    {
        using var cluster = ThreeShardOrders();
        for (int i = 0; i < 30; i++)
            cluster.Insert("orders", $$"""{"pnr":"PNR{{i:D3}}","amount":{{i}}}""");

        var r = cluster.Execute("SELECT * FROM orders WHERE pnr = 'PNR005' OR pnr = 'PNR010'");
        Assert.True(r.Success);
        Assert.Equal(2, r.Documents.Count);
        Assert.DoesNotContain("SCATTER_GATHER", r.QueryPlan!);
    }

    [Fact]
    public void OrMixingShardKeyWithOtherField_StillScatters()
    {
        using var cluster = ThreeShardOrders();
        for (int i = 0; i < 30; i++)
            cluster.Insert("orders", $$"""{"pnr":"PNR{{i:D3}}","amount":{{i}}}""");

        // pnr=X OR amount=Y can't be restricted to shards — must scatter (and
        // still return correct results).
        var r = cluster.Execute("SELECT * FROM orders WHERE pnr = 'PNR001' OR amount = 20");
        Assert.True(r.Success);
        Assert.Contains("SCATTER_GATHER", r.QueryPlan!);
        Assert.Equal(2, r.Documents.Count); // PNR001 and the amount=20 doc
    }

    [Fact]
    public void CrossShardDistinct_IsRejected_NotSilentlyWrong()
    {
        using var cluster = ThreeShardOrders();
        for (int i = 0; i < 10; i++)
            cluster.Insert("orders", $$"""{"pnr":"PNR{{i:D3}}","cls":"econ"}""");

        var r = cluster.Execute("SELECT DISTINCT cls FROM orders");
        Assert.False(r.Success);
        Assert.Contains("DISTINCT", r.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SingleShardDistinct_IsAllowed()
    {
        using var cluster = ThreeShardOrders();
        cluster.Insert("orders", """{"pnr":"ONE","cls":"econ"}""");
        // Routes to one shard (shard-key equality) → DISTINCT is fine there.
        var r = cluster.Execute("SELECT DISTINCT cls FROM orders WHERE pnr = 'ONE'");
        Assert.True(r.Success);
    }

    public void Dispose()
    {
        foreach (var db in _dbs) { try { db.Dispose(); } catch { } }
        foreach (var p in _paths)
            foreach (var ext in new[] { "", ".wal", ".recovery", ".lock" })
                try { File.Delete(p + ext); } catch { }
    }
}
