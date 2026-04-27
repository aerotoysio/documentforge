using Xunit;
using DocumentForge.Core;
using DocumentForge.Document;
using DocumentForge.Engine;
using DocumentForge.Index;

namespace DocumentForge.Tests;

public class EngineTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DocumentForgeDb _db;

    public EngineTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.dfdb");
        _db = DocumentForgeDb.Create(_dbPath);
    }

    [Fact]
    public void InsertAndRetrieve_SingleDocument()
    {
        var id = _db.Insert("users", """{"name": "Alice", "age": 30}""");
        var collection = _db.GetCollection("users");
        Assert.NotNull(collection);
        var doc = collection!.FindById(id);
        Assert.NotNull(doc);
        Assert.Equal("Alice", doc!["name"].AsString);
        Assert.Equal(30, doc["age"].AsInt32);
    }

    [Fact]
    public void Query_SelectAll()
    {
        _db.Insert("users", """{"name": "Alice", "age": 30}""");
        _db.Insert("users", """{"name": "Bob", "age": 25}""");

        var result = _db.Execute("SELECT * FROM users");
        Assert.True(result.Success);
        Assert.Equal(2, result.Documents.Count);
    }

    [Fact]
    public void Query_WhereEquals()
    {
        _db.Insert("users", """{"name": "Alice", "age": 30}""");
        _db.Insert("users", """{"name": "Bob", "age": 25}""");
        _db.Insert("users", """{"name": "Charlie", "age": 30}""");

        var result = _db.Execute("SELECT * FROM users WHERE age = 30");
        Assert.True(result.Success);
        Assert.Equal(2, result.Documents.Count);
    }

    [Fact]
    public void Query_WithIndex()
    {
        _db.Insert("users", """{"name": "Alice", "age": 30}""");
        _db.Insert("users", """{"name": "Bob", "age": 25}""");
        _db.CreateIndex("users", "name", "idx_name");

        var result = _db.Execute("SELECT * FROM users WHERE name = 'Bob'");
        Assert.True(result.Success);
        Assert.Single(result.Documents);
        Assert.Equal("Bob", result.Documents[0]["name"].AsString);
        Assert.Contains("INDEX_SCAN", result.QueryPlan!);
    }

    [Fact]
    public void Query_NestedField()
    {
        _db.Insert("orders", """{"pnr": "ABC123", "passenger": {"lastName": "Smith"}}""");
        _db.Insert("orders", """{"pnr": "DEF456", "passenger": {"lastName": "Jones"}}""");
        _db.CreateIndex("orders", "passenger.lastName", "idx_lastname");

        var result = _db.Execute("SELECT * FROM orders WHERE passenger.lastName = 'Smith'");
        Assert.True(result.Success);
        Assert.Single(result.Documents);
        Assert.Contains("INDEX_SCAN", result.QueryPlan!);
    }

    [Fact]
    public void Query_OrderByAndLimit()
    {
        _db.Insert("users", """{"name": "Charlie", "age": 35}""");
        _db.Insert("users", """{"name": "Alice", "age": 30}""");
        _db.Insert("users", """{"name": "Bob", "age": 25}""");

        var result = _db.Execute("SELECT * FROM users ORDER BY name LIMIT 2");
        Assert.True(result.Success);
        Assert.Equal(2, result.Documents.Count);
        Assert.Equal("Alice", result.Documents[0]["name"].AsString);
        Assert.Equal("Bob", result.Documents[1]["name"].AsString);
    }

    [Fact]
    public void Query_Update()
    {
        _db.Insert("users", """{"name": "Alice", "age": 30}""");

        var result = _db.Execute("UPDATE users SET age = 31 WHERE name = 'Alice'");
        Assert.True(result.Success);
        Assert.Equal(1, result.AffectedCount);

        result = _db.Execute("SELECT * FROM users WHERE name = 'Alice'");
        Assert.Equal(31, result.Documents[0]["age"].AsDouble);
    }

    [Fact]
    public void Query_Delete()
    {
        _db.Insert("users", """{"name": "Alice", "age": 30}""");
        _db.Insert("users", """{"name": "Bob", "age": 25}""");

        var result = _db.Execute("DELETE FROM users WHERE name = 'Alice'");
        Assert.True(result.Success);
        Assert.Equal(1, result.AffectedCount);

        result = _db.Execute("SELECT * FROM users");
        Assert.Single(result.Documents);
        Assert.Equal("Bob", result.Documents[0]["name"].AsString);
    }

    [Fact]
    public void Query_Count()
    {
        _db.Insert("users", """{"name": "Alice"}""");
        _db.Insert("users", """{"name": "Bob"}""");
        _db.Insert("users", """{"name": "Charlie"}""");

        var result = _db.Execute("SELECT COUNT(*) FROM users");
        Assert.True(result.Success);
        Assert.Equal(3, result.Documents[0]["count"].AsInt64);
    }

    [Fact]
    public void BsonSerializer_RoundTrip()
    {
        var doc = BsonDocument.FromJson("""
        {
            "name": "Test",
            "age": 42,
            "active": true,
            "tags": ["a", "b"],
            "address": { "city": "London", "zip": "SW1" }
        }
        """);

        var bytes = BsonSerializer.Serialize(doc);
        var restored = BsonSerializer.Deserialize(bytes);

        Assert.Equal("Test", restored["name"].AsString);
        Assert.Equal(42, restored["age"].AsInt32);
        Assert.Equal(true, restored["active"].AsBoolean);
        Assert.Equal(2, restored["tags"].AsArray.Count);
        Assert.Equal("London", JsonPathExtractor.Extract(restored, "address.city").AsString);
    }

    [Fact]
    public void JsonPathExtractor_NestedPaths()
    {
        var doc = BsonDocument.FromJson("""
        {
            "passenger": {
                "firstName": "John",
                "lastName": "Smith"
            },
            "flights": [
                { "code": "AA100", "from": "JFK" },
                { "code": "AA200", "from": "LAX" }
            ]
        }
        """);

        Assert.Equal("Smith", JsonPathExtractor.Extract(doc, "passenger.lastName").AsString);
        Assert.Equal("AA100", JsonPathExtractor.Extract(doc, "flights[0].code").AsString);
        Assert.Equal("LAX", JsonPathExtractor.Extract(doc, "flights[1].from").AsString);

        var allCodes = JsonPathExtractor.ExtractAll(doc, "flights[*].code").ToList();
        Assert.Equal(2, allCodes.Count);
        Assert.Equal("AA100", allCodes[0].AsString);
        Assert.Equal("AA200", allCodes[1].AsString);
    }

    [Fact]
    public void DatabaseStatistics()
    {
        _db.Insert("users", """{"name": "Alice"}""");
        _db.CreateIndex("users", "name", "idx_name");

        var stats = _db.GetStatistics();
        Assert.Single(stats.Collections);
        Assert.Equal("users", stats.Collections[0].Name);
        Assert.Equal(1, stats.Collections[0].DocumentCount);
        Assert.Equal(1, stats.Collections[0].IndexCount);
    }

    [Fact]
    public void Insert_ImmediatelyQueryableByIndex()
    {
        // Create index FIRST, before any data
        _db.Insert("orders", """{"pnr": "SEED01", "passenger": {"lastName": "Nobody"}}""");
        _db.CreateIndex("orders", "passenger.lastName", "idx_ln");

        // Now insert a new document
        _db.Insert("orders", """{"pnr": "NEW001", "passenger": {"lastName": "Henderson"}}""");

        // Query it IMMEDIATELY - no delay, no background sync
        var result = _db.Execute("SELECT * FROM orders WHERE passenger.lastName = 'Henderson'");

        Assert.True(result.Success);
        Assert.Single(result.Documents);
        Assert.Equal("NEW001", result.Documents[0]["pnr"].AsString);
        Assert.Contains("INDEX_SCAN", result.QueryPlan!); // Proves it used the index
    }

    [Fact]
    public void CrashRecovery_ReplaysWalOnReopen()
    {
        // Insert data normally - this leaves the DB in a clean state
        _db.Insert("orders", """{"pnr": "SAFE01", "amount": 100}""");
        _db.Insert("orders", """{"pnr": "SAFE02", "amount": 200}""");
        _db.Flush();

        // Simulate a crash: write directly to the recovery log a page that
        // wasn't flushed to the data file. This mimics "WAL written, data file crashed".
        // The easiest way to prove the recovery replay works is to:
        //   1. Close cleanly
        //   2. Synthesize a recovery log with a fake page entry pointing to page 0 (header)
        //   3. Reopen and verify the recovery happens without corruption

        _db.Dispose();

        // Read the actual header page from disk
        var headerBytes = new byte[Constants.PageSize];
        using (var fs = new FileStream(_dbPath, FileMode.Open, FileAccess.Read))
        {
            fs.Read(headerBytes, 0, Constants.PageSize);
        }

        // Create a recovery log with that page (simulating a mid-flush crash)
        var recoveryPath = _dbPath + ".recovery";
        using (var rl = new DocumentForge.Transactions.RecoveryLog(recoveryPath))
        {
            rl.LogPageWrite(PageId.Header, headerBytes);
            rl.Flush();
        }

        // Reopen - recovery should trigger
        using var reopened = DocumentForgeDb.Open(_dbPath);
        var result = reopened.Execute("SELECT * FROM orders");
        Assert.True(result.Success);
        Assert.Equal(2, result.Documents.Count);

        // Recovery log may be recreated (empty) by the new DB instance, but must be empty
        if (File.Exists(recoveryPath))
            Assert.Equal(0, new FileInfo(recoveryPath).Length);
    }

    public sealed class TestOrder
    {
        public string _id { get; set; } = "";
        public string Pnr { get; set; } = "";
        public string Status { get; set; } = "";
        public int Amount { get; set; }
    }

    [Fact]
    public void Linq_WhereAndFirstOrDefault()
    {
        _db.Insert("orders", """{"pnr": "ABC123", "status": "CONFIRMED", "amount": 100}""");
        _db.Insert("orders", """{"pnr": "DEF456", "status": "CANCELLED", "amount": 50}""");

        var orders = _db.Collection<TestOrder>("orders");
        var found = orders.Where(o => o.Pnr == "ABC123").FirstOrDefault();

        Assert.NotNull(found);
        Assert.Equal("ABC123", found!.Pnr);
        Assert.Equal(100, found.Amount);
    }

    [Fact]
    public void Linq_MultipleConditions_And()
    {
        _db.Insert("orders", """{"pnr": "A", "status": "CONFIRMED", "amount": 100}""");
        _db.Insert("orders", """{"pnr": "B", "status": "CONFIRMED", "amount": 500}""");
        _db.Insert("orders", """{"pnr": "C", "status": "CANCELLED", "amount": 200}""");

        var orders = _db.Collection<TestOrder>("orders");
        var results = orders.Where(o => o.Status == "CONFIRMED" && o.Amount > 200).ToList();

        Assert.Single(results);
        Assert.Equal("B", results[0].Pnr);
    }

    [Fact]
    public void Linq_OrderByAndTake()
    {
        _db.Insert("orders", """{"pnr": "X", "amount": 300}""");
        _db.Insert("orders", """{"pnr": "Y", "amount": 100}""");
        _db.Insert("orders", """{"pnr": "Z", "amount": 200}""");

        var orders = _db.Collection<TestOrder>("orders");
        var top2 = orders.OrderByDescending(o => o.Amount).Take(2).ToList();

        Assert.Equal(2, top2.Count);
        Assert.Equal("X", top2[0].Pnr);  // 300
        Assert.Equal("Z", top2[1].Pnr);  // 200
    }

    [Fact]
    public void Linq_CountWithWhere()
    {
        _db.Insert("orders", """{"pnr": "A", "status": "CONFIRMED"}""");
        _db.Insert("orders", """{"pnr": "B", "status": "CONFIRMED"}""");
        _db.Insert("orders", """{"pnr": "C", "status": "CANCELLED"}""");

        var orders = _db.Collection<TestOrder>("orders");
        Assert.Equal(2, orders.Where(o => o.Status == "CONFIRMED").Count());
    }

    [Fact]
    public void Linq_WithCapturedVariable()
    {
        _db.Insert("orders", """{"pnr": "CAP001", "amount": 100}""");
        _db.Insert("orders", """{"pnr": "CAP002", "amount": 200}""");

        var targetPnr = "CAP002";
        var orders = _db.Collection<TestOrder>("orders");
        var found = orders.Where(o => o.Pnr == targetPnr).FirstOrDefault();

        Assert.NotNull(found);
        Assert.Equal("CAP002", found!.Pnr);
    }

    [Fact]
    public void Cluster_HashShard_RoutesSingleDocToOneShard()
    {
        var path1 = Path.Combine(Path.GetTempPath(), $"sh1_{Guid.NewGuid():N}.dfdb");
        var path2 = Path.Combine(Path.GetTempPath(), $"sh2_{Guid.NewGuid():N}.dfdb");
        var path3 = Path.Combine(Path.GetTempPath(), $"sh3_{Guid.NewGuid():N}.dfdb");

        try
        {
            var db1 = DocumentForgeDb.Create(path1);
            var db2 = DocumentForgeDb.Create(path2);
            var db3 = DocumentForgeDb.Create(path3);

            using var cluster = new DocumentForge.Engine.Cluster.DocumentForgeCluster()
                .AddShard(new DocumentForge.Engine.Cluster.InProcessShardTransport("A", db1, ownsDb: true))
                .AddShard(new DocumentForge.Engine.Cluster.InProcessShardTransport("B", db2, ownsDb: true))
                .AddShard(new DocumentForge.Engine.Cluster.InProcessShardTransport("C", db3, ownsDb: true))
                .ShardCollection("orders", shardKeyPath: "pnr");

            // Insert 300 orders - consistent hashing spreads them reasonably
            for (int i = 0; i < 300; i++)
                cluster.Insert("orders", $$"""{"pnr": "ORD{{i:D4}}", "amount": {{i * 10}}}""");

            // Count per shard to verify distribution
            long c1 = db1.Execute("SELECT COUNT(*) FROM orders").Documents[0]["count"].AsInt64;
            long c2 = db2.Execute("SELECT COUNT(*) FROM orders").Documents[0]["count"].AsInt64;
            long c3 = db3.Execute("SELECT COUNT(*) FROM orders").Documents[0]["count"].AsInt64;

            Assert.Equal(300, c1 + c2 + c3);
            Assert.True(c1 > 0 && c2 > 0 && c3 > 0, $"Expected docs on all shards, got {c1}/{c2}/{c3}");

            // Single-shard query (WHERE on shard key) should route to ONE shard
            var result = cluster.Execute("SELECT * FROM orders WHERE pnr = 'ORD0050'");
            Assert.Single(result.Documents);
            Assert.Contains("SINGLE_SHARD", result.QueryPlan!);

            // Scatter-gather query (no shard key filter)
            var all = cluster.Execute("SELECT * FROM orders");
            Assert.Equal(300, all.Documents.Count);
            Assert.Contains("SCATTER_GATHER", all.QueryPlan!);
        }
        finally
        {
            try { File.Delete(path1); File.Delete(path1 + ".wal"); File.Delete(path1 + ".recovery"); } catch { }
            try { File.Delete(path2); File.Delete(path2 + ".wal"); File.Delete(path2 + ".recovery"); } catch { }
            try { File.Delete(path3); File.Delete(path3 + ".wal"); File.Delete(path3 + ".recovery"); } catch { }
        }
    }

    [Fact]
    public void Cluster_Rebalance_ScaleUp_2To4_RedistributesData()
    {
        // 2-shard cluster
        var pathsBefore = Enumerable.Range(0, 2).Select(i =>
            Path.Combine(Path.GetTempPath(), $"rb_before_{i}_{Guid.NewGuid():N}.dfdb")).ToArray();
        var pathsAfter = Enumerable.Range(0, 4).Select(i =>
            Path.Combine(Path.GetTempPath(), $"rb_after_{i}_{Guid.NewGuid():N}.dfdb")).ToArray();
        var allPaths = pathsBefore.Concat(pathsAfter).ToList();

        try
        {
            // Build the "before" state: 2 shards with data
            var dbsBefore = pathsBefore.Select(p => DocumentForgeDb.Create(p)).ToList();
            var names2 = new[] { "A", "B" };

            var oldConfig = new DocumentForge.Engine.Cluster.ClusterConfig
            {
                Shards = names2.Zip(pathsBefore, (n, p) => new DocumentForge.Engine.Cluster.ShardDescriptor { Name = n, Endpoint = p }).ToList(),
                Collections = { ["orders"] = new() { Strategy = DocumentForge.Engine.Cluster.ShardingStrategy.Hash, ShardKeyPath = "pnr" } }
            };

            using (var cluster2 = new DocumentForge.Engine.Cluster.DocumentForgeCluster())
            {
                for (int i = 0; i < 2; i++)
                    cluster2.AddShard(new DocumentForge.Engine.Cluster.InProcessShardTransport(names2[i], dbsBefore[i]));
                cluster2.ShardCollection("orders", "pnr");

                for (int i = 0; i < 200; i++)
                    cluster2.Insert("orders", $$"""{"pnr": "ORD{{i:D4}}", "amount": {{i * 10}}}""");
            }

            // Dispose cluster (but we still own the DBs)
            foreach (var db in dbsBefore) db.Flush();
            foreach (var db in dbsBefore) db.Dispose();

            // Snapshot "before" counts per shard
            long totalBefore = 0;
            var beforeCounts = new long[2];
            for (int i = 0; i < 2; i++)
            {
                using var d = DocumentForgeDb.Open(pathsBefore[i]);
                beforeCounts[i] = d.Execute("SELECT COUNT(*) FROM orders").Documents[0]["count"].AsInt64;
                totalBefore += beforeCounts[i];
            }
            Assert.Equal(200, totalBefore);

            // Now define the target topology: 4 shards (A, B stay; C, D added)
            var dbsAfter = new List<DocumentForgeDb>();
            dbsAfter.Add(DocumentForgeDb.Open(pathsBefore[0]));  // shard A
            dbsAfter.Add(DocumentForgeDb.Open(pathsBefore[1]));  // shard B
            dbsAfter.Add(DocumentForgeDb.Create(pathsAfter[2])); // shard C (new, path reused for map)
            dbsAfter.Add(DocumentForgeDb.Create(pathsAfter[3])); // shard D (new)

            var names4 = new[] { "A", "B", "C", "D" };
            var allPathsArr = new[] { pathsBefore[0], pathsBefore[1], pathsAfter[2], pathsAfter[3] };
            var newConfig = new DocumentForge.Engine.Cluster.ClusterConfig
            {
                Shards = names4.Zip(allPathsArr, (n, p) => new DocumentForge.Engine.Cluster.ShardDescriptor { Name = n, Endpoint = p }).ToList(),
                Collections = { ["orders"] = new() { Strategy = DocumentForge.Engine.Cluster.ShardingStrategy.Hash, ShardKeyPath = "pnr" } }
            };

            // Transport factory returns the matching in-process DB (the test hack is that
            // Endpoint = the file path, so we just match on path)
            DocumentForge.Engine.Cluster.IShardTransport MakeTransport(DocumentForge.Engine.Cluster.ShardDescriptor d)
            {
                int idx = Array.IndexOf(allPathsArr, d.Endpoint);
                if (idx >= 0 && idx < dbsAfter.Count)
                    return new DocumentForge.Engine.Cluster.InProcessShardTransport(d.Name, dbsAfter[idx]);
                throw new InvalidOperationException($"No db for endpoint {d.Endpoint}");
            }

            // Plan + execute migration
            var plan = DocumentForge.Engine.Cluster.ClusterRebalancer.Plan(oldConfig, newConfig, MakeTransport);
            Assert.True(plan.TotalDocumentsToMove > 0, "Some docs should need to move");
            Assert.True(plan.TotalDocumentsToMove < 200, "Consistent hashing should keep most docs put");

            DocumentForge.Engine.Cluster.ClusterRebalancer.Execute(plan, MakeTransport);

            // Verify: every doc still exists exactly once across all 4 shards
            long totalAfter = 0;
            var afterCounts = new long[4];
            for (int i = 0; i < 4; i++)
            {
                afterCounts[i] = dbsAfter[i].Execute("SELECT COUNT(*) FROM orders").Documents[0]["count"].AsInt64;
                totalAfter += afterCounts[i];
            }
            Assert.Equal(200, totalAfter);
            // C and D should have received some docs from the rebalance
            Assert.True(afterCounts[2] > 0 || afterCounts[3] > 0, "New shards should have received some docs");

            foreach (var db in dbsAfter) db.Dispose();
        }
        finally
        {
            foreach (var p in allPaths)
            {
                try { File.Delete(p); File.Delete(p + ".wal"); File.Delete(p + ".recovery"); } catch { }
            }
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Replication_SharedSecretEnforced()
    {
        int port = 6500 + System.Random.Shared.Next(100);
        var leaderPath = Path.Combine(Path.GetTempPath(), $"sec_leader_{Guid.NewGuid():N}.dfdb");
        var good = Path.Combine(Path.GetTempPath(), $"sec_good_{Guid.NewGuid():N}.dfdb");
        var bad  = Path.Combine(Path.GetTempPath(), $"sec_bad_{Guid.NewGuid():N}.dfdb");

        try
        {
            using var leader = DocumentForgeDb.Create(leaderPath);
            leader.StartLogicalReplicationServer(port, sharedSecret: "correct-secret");
            await System.Threading.Tasks.Task.Delay(200);

            // Good follower with matching secret
            using var goodFollower = DocumentForgeDb.Create(good);
            goodFollower.StartLogicalReplicationFollower("localhost", port, sharedSecret: "correct-secret");

            // Bad follower with WRONG secret - should be rejected
            using var badFollower = DocumentForgeDb.Create(bad);
            badFollower.StartLogicalReplicationFollower("localhost", port, sharedSecret: "wrong-secret");

            // Wait for handshake attempts to settle
            for (int i = 0; i < 30 && leader.GetLogicalFollowerCount() == 0; i++)
                await System.Threading.Tasks.Task.Delay(100);
            await System.Threading.Tasks.Task.Delay(500); // extra time for the bad follower to attempt + be rejected

            // Only the good follower should be in the leader's connected set
            Assert.Equal(1, leader.GetLogicalFollowerCount());

            // Writes replicate to the good one
            leader.Insert("orders", """{"pnr": "AUTH001"}""");
            for (int i = 0; i < 20 && goodFollower.LogicallyReplicatedOps() < 1; i++)
                await System.Threading.Tasks.Task.Delay(100);
            Assert.True(goodFollower.LogicallyReplicatedOps() >= 1);

            // Bad follower saw nothing
            Assert.Equal(0, badFollower.LogicallyReplicatedOps());
        }
        finally
        {
            foreach (var p in new[] { leaderPath, good, bad })
            {
                try { File.Delete(p); File.Delete(p + ".wal"); File.Delete(p + ".recovery"); File.Delete(p + ".followerseq"); } catch { }
            }
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Cluster_OnlineRebalance_ZeroDataLossWithConcurrentWrites()
    {
        // Start with 2 shards, run concurrent writes while rebalancing to 4 shards.
        // No doc should be lost; no doc should be duplicated after completion.
        var paths = Enumerable.Range(0, 4).Select(i =>
            Path.Combine(Path.GetTempPath(), $"onl_{i}_{Guid.NewGuid():N}.dfdb")).ToArray();

        var dbs = paths.Select(p => DocumentForgeDb.Create(p)).ToList();
        var names = new[] { "A", "B", "C", "D" };

        try
        {
            var oldConfig = new DocumentForge.Engine.Cluster.ClusterConfig
            {
                Shards = new()
                {
                    new() { Name = "A", Endpoint = paths[0] },
                    new() { Name = "B", Endpoint = paths[1] }
                },
                Collections = { ["orders"] = new() { Strategy = DocumentForge.Engine.Cluster.ShardingStrategy.Hash, ShardKeyPath = "pnr" } }
            };

            var newConfig = new DocumentForge.Engine.Cluster.ClusterConfig
            {
                Shards = new()
                {
                    new() { Name = "A", Endpoint = paths[0] },
                    new() { Name = "B", Endpoint = paths[1] },
                    new() { Name = "C", Endpoint = paths[2] },
                    new() { Name = "D", Endpoint = paths[3] }
                },
                Collections = { ["orders"] = new() { Strategy = DocumentForge.Engine.Cluster.ShardingStrategy.Hash, ShardKeyPath = "pnr" } }
            };

            DocumentForge.Engine.Cluster.IShardTransport MakeTransport(DocumentForge.Engine.Cluster.ShardDescriptor d)
            {
                int idx = Array.IndexOf(paths, d.Endpoint);
                return new DocumentForge.Engine.Cluster.InProcessShardTransport(d.Name, dbs[idx]);
            }

            // Populate initial cluster (A, B) with 200 docs
            using (var cluster2 = new DocumentForge.Engine.Cluster.DocumentForgeCluster())
            {
                cluster2.AddShard(MakeTransport(oldConfig.Shards[0]));
                cluster2.AddShard(MakeTransport(oldConfig.Shards[1]));
                cluster2.ShardCollection("orders", "pnr");

                for (int i = 0; i < 200; i++)
                    cluster2.Insert("orders", $$"""{"pnr": "INIT{{i:D4}}"}""");
            }

            // Now stand up the NEW cluster with 4 shards
            using var cluster4 = new DocumentForge.Engine.Cluster.DocumentForgeCluster();
            cluster4.AddShard(MakeTransport(newConfig.Shards[0]));
            cluster4.AddShard(MakeTransport(newConfig.Shards[1]));
            cluster4.AddShard(MakeTransport(newConfig.Shards[2]));
            cluster4.AddShard(MakeTransport(newConfig.Shards[3]));
            cluster4.ShardCollection("orders", "pnr");

            // We need a separate set of "previous" transports for the rebalancer
            var previousShards = new List<DocumentForge.Engine.Cluster.IShardTransport>
            {
                MakeTransport(oldConfig.Shards[0]),
                MakeTransport(oldConfig.Shards[1])
            };

            var rebalanceTask = DocumentForge.Engine.Cluster.ClusterRebalancer.RunOnlineAsync(
                cluster4, oldConfig, newConfig, previousShards);

            // Concurrent writes from the client - these go to the NEW ring
            var concurrentIds = new List<string>();
            for (int i = 0; i < 50; i++)
            {
                var pnr = $"LIVE{i:D4}";
                concurrentIds.Add(pnr);
                cluster4.Insert("orders", $$"""{"pnr": "{{pnr}}", "status": "DURING_REBAL"}""");
                if (i % 10 == 0) await System.Threading.Tasks.Task.Delay(10);
            }

            // Wait for rebalance to finish
            var report = await rebalanceTask;

            // Complete the migration (drop the previous ring)
            DocumentForge.Engine.Cluster.ClusterRebalancer.CompleteOnlineRebalance(cluster4, previousShards);

            // Verify: 200 initial + 50 concurrent = 250 docs total, all findable
            long totalAcrossShards = 0;
            for (int i = 0; i < 4; i++)
                totalAcrossShards += dbs[i].Execute("SELECT COUNT(*) FROM orders").Documents[0]["count"].AsInt64;
            Assert.Equal(250, totalAcrossShards);

            // Every concurrent-write PNR is still findable
            foreach (var pnr in concurrentIds)
            {
                var r = cluster4.Execute($"SELECT * FROM orders WHERE pnr = '{pnr}'");
                Assert.Single(r.Documents);
                Assert.Equal("DURING_REBAL", r.Documents[0]["status"].AsString);
            }

            // Every INIT PNR is still findable (they moved during the rebalance)
            for (int i = 0; i < 200; i++)
            {
                var r = cluster4.Execute($"SELECT * FROM orders WHERE pnr = 'INIT{i:D4}'");
                Assert.Single(r.Documents);
            }

            Assert.True(report.TotalMoved > 0, "Rebalance should have moved some docs");
        }
        finally
        {
            foreach (var db in dbs) { try { db.Dispose(); } catch { } }
            foreach (var p in paths)
            {
                try { File.Delete(p); File.Delete(p + ".wal"); File.Delete(p + ".recovery"); } catch { }
            }
        }
    }

    [Fact]
    public void Cluster_Rebalance_ScaleDown_DropsShard()
    {
        var paths = Enumerable.Range(0, 3).Select(i =>
            Path.Combine(Path.GetTempPath(), $"rbd_{i}_{Guid.NewGuid():N}.dfdb")).ToArray();

        try
        {
            var dbs = paths.Select(p => DocumentForgeDb.Create(p)).ToList();
            var names3 = new[] { "A", "B", "C" };

            var oldConfig = new DocumentForge.Engine.Cluster.ClusterConfig
            {
                Shards = names3.Zip(paths, (n, p) => new DocumentForge.Engine.Cluster.ShardDescriptor { Name = n, Endpoint = p }).ToList(),
                Collections = { ["orders"] = new() { Strategy = DocumentForge.Engine.Cluster.ShardingStrategy.Hash, ShardKeyPath = "pnr" } }
            };

            using (var cluster3 = new DocumentForge.Engine.Cluster.DocumentForgeCluster())
            {
                for (int i = 0; i < 3; i++)
                    cluster3.AddShard(new DocumentForge.Engine.Cluster.InProcessShardTransport(names3[i], dbs[i]));
                cluster3.ShardCollection("orders", "pnr");

                for (int i = 0; i < 150; i++)
                    cluster3.Insert("orders", $$"""{"pnr": "DROP{{i:D3}}"}""");
            }

            // Scale DOWN: drop shard C
            var newConfig = new DocumentForge.Engine.Cluster.ClusterConfig
            {
                Shards = new()
                {
                    oldConfig.Shards[0],  // A stays
                    oldConfig.Shards[1],  // B stays
                    // C removed
                },
                Collections = { ["orders"] = new() { Strategy = DocumentForge.Engine.Cluster.ShardingStrategy.Hash, ShardKeyPath = "pnr" } }
            };

            DocumentForge.Engine.Cluster.IShardTransport MakeTransport(DocumentForge.Engine.Cluster.ShardDescriptor d)
            {
                int idx = Array.IndexOf(paths, d.Endpoint);
                return new DocumentForge.Engine.Cluster.InProcessShardTransport(d.Name, dbs[idx]);
            }

            var plan = DocumentForge.Engine.Cluster.ClusterRebalancer.Plan(oldConfig, newConfig, MakeTransport);
            DocumentForge.Engine.Cluster.ClusterRebalancer.Execute(plan, MakeTransport);

            // Shard C should now be empty (all its docs migrated to A or B)
            long cC = dbs[2].Execute("SELECT COUNT(*) FROM orders").Documents[0]["count"].AsInt64;
            Assert.Equal(0, cC);

            // Total count across A + B should still be 150
            long total = dbs[0].Execute("SELECT COUNT(*) FROM orders").Documents[0]["count"].AsInt64
                       + dbs[1].Execute("SELECT COUNT(*) FROM orders").Documents[0]["count"].AsInt64;
            Assert.Equal(150, total);

            foreach (var db in dbs) db.Dispose();
        }
        finally
        {
            foreach (var p in paths)
            {
                try { File.Delete(p); File.Delete(p + ".wal"); File.Delete(p + ".recovery"); } catch { }
            }
        }
    }

    [Fact]
    public void Cluster_ConfigRoundTrip_JsonPersistence()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"cfg_{Guid.NewGuid():N}.json");
        try
        {
            var original = new DocumentForge.Engine.Cluster.ClusterConfig
            {
                Version = 1,
                VirtualNodesPerShard = 200,
                Shards = new()
                {
                    new() { Name = "dubai",     Endpoint = "dubai.example.com:5500" },
                    new() { Name = "singapore", Endpoint = "sg.example.com:5500" }
                },
                Collections =
                {
                    ["orders"]   = new() { Strategy = DocumentForge.Engine.Cluster.ShardingStrategy.Hash,       ShardKeyPath = "pnr" },
                    ["airports"] = new() { Strategy = DocumentForge.Engine.Cluster.ShardingStrategy.Replicated }
                }
            };

            original.Save(configPath);
            var loaded = DocumentForge.Engine.Cluster.ClusterConfig.Load(configPath);

            Assert.Equal(200, loaded.VirtualNodesPerShard);
            Assert.Equal(2, loaded.Shards.Count);
            Assert.Equal("dubai", loaded.Shards[0].Name);
            Assert.Equal("singapore", loaded.Shards[1].Name);
            Assert.Equal(2, loaded.Collections.Count);
            Assert.Equal("pnr", loaded.Collections["orders"].ShardKeyPath);
            Assert.Equal(DocumentForge.Engine.Cluster.ShardingStrategy.Replicated, loaded.Collections["airports"].Strategy);
        }
        finally { try { File.Delete(configPath); } catch { } }
    }

    [Fact]
    public void Cluster_ConsistentHashing_StabilityAcrossRestart()
    {
        // Build ring twice with the same shard names - routing must be IDENTICAL
        var ring1 = new DocumentForge.Engine.Cluster.ConsistentHashRing(
            new[] { "dubai", "singapore", "london" }, virtualNodesPerShard: 150);
        var ring2 = new DocumentForge.Engine.Cluster.ConsistentHashRing(
            new[] { "dubai", "singapore", "london" }, virtualNodesPerShard: 150);

        for (int i = 0; i < 1000; i++)
        {
            var key = $"PNR{i:D5}";
            Assert.Equal(ring1.PickShardIndex(key), ring2.PickShardIndex(key));
        }
    }

    [Fact]
    public void Cluster_ConsistentHashing_AddShardOnlyMovesFractionOfKeys()
    {
        var ring3 = new DocumentForge.Engine.Cluster.ConsistentHashRing(
            new[] { "A", "B", "C" }, virtualNodesPerShard: 150);
        var ring4 = new DocumentForge.Engine.Cluster.ConsistentHashRing(
            new[] { "A", "B", "C", "D" }, virtualNodesPerShard: 150);

        int moved = 0;
        const int total = 10_000;
        for (int i = 0; i < total; i++)
        {
            var key = $"KEY{i:D6}";
            var before = ring3.PickShardIndex(key);
            var after = ring4.PickShardIndex(key);
            // Map shard indices: ring3's shard idx 0/1/2 = A/B/C; ring4's idx 0/1/2/3 = A/B/C/D.
            // A key moved if after != 3 (new shard D) but maps to a different letter OR if after == 3.
            // Simpler: just check if the shard index changed AND the new ring has the key on a new position.
            // Because A/B/C have same names, they're at mostly the same ring positions.
            if (ring3.PickShardIndex(key) != ring4.PickShardIndex(key)) moved++;
        }

        // Naive hash would move ~75%. Consistent hashing should move ~25% (1 / N+1).
        var movedPct = moved * 100.0 / total;
        Assert.True(movedPct < 40, $"Consistent hashing should move ~25% of keys, but moved {movedPct:F1}%");
    }

    [Fact]
    public void Cluster_ReplicatedCollection_EveryShardHasFullCopy()
    {
        var path1 = Path.Combine(Path.GetTempPath(), $"rep1_{Guid.NewGuid():N}.dfdb");
        var path2 = Path.Combine(Path.GetTempPath(), $"rep2_{Guid.NewGuid():N}.dfdb");

        try
        {
            var db1 = DocumentForgeDb.Create(path1);
            var db2 = DocumentForgeDb.Create(path2);

            using var cluster = new DocumentForge.Engine.Cluster.DocumentForgeCluster()
                .AddShard(new DocumentForge.Engine.Cluster.InProcessShardTransport("A", db1, ownsDb: true))
                .AddShard(new DocumentForge.Engine.Cluster.InProcessShardTransport("B", db2, ownsDb: true))
                .ReplicateCollection("airports");

            // Inserting to a replicated collection writes to ALL shards
            cluster.Insert("airports", """{"code": "JFK", "name": "John F. Kennedy"}""");
            cluster.Insert("airports", """{"code": "LAX", "name": "Los Angeles"}""");

            long c1 = db1.Execute("SELECT COUNT(*) FROM airports").Documents[0]["count"].AsInt64;
            long c2 = db2.Execute("SELECT COUNT(*) FROM airports").Documents[0]["count"].AsInt64;
            Assert.Equal(2, c1);
            Assert.Equal(2, c2);
        }
        finally
        {
            try { File.Delete(path1); File.Delete(path1 + ".wal"); File.Delete(path1 + ".recovery"); } catch { }
            try { File.Delete(path2); File.Delete(path2 + ".wal"); File.Delete(path2 + ".recovery"); } catch { }
        }
    }

    [Fact]
    public void Cluster_ScatterGatherAggregate_MergesCountSum()
    {
        var path1 = Path.Combine(Path.GetTempPath(), $"agg1_{Guid.NewGuid():N}.dfdb");
        var path2 = Path.Combine(Path.GetTempPath(), $"agg2_{Guid.NewGuid():N}.dfdb");

        try
        {
            var db1 = DocumentForgeDb.Create(path1);
            var db2 = DocumentForgeDb.Create(path2);

            using var cluster = new DocumentForge.Engine.Cluster.DocumentForgeCluster()
                .AddShard(new DocumentForge.Engine.Cluster.InProcessShardTransport("A", db1, ownsDb: true))
                .AddShard(new DocumentForge.Engine.Cluster.InProcessShardTransport("B", db2, ownsDb: true))
                .ShardCollection("sales", shardKeyPath: "orderId");

            for (int i = 0; i < 20; i++)
                cluster.Insert("sales", $$"""{"orderId": "ORD{{i:D3}}", "amount": {{i * 10}}}""");

            // Verify distribution was real (both shards got some)
            var c1 = db1.Execute("SELECT COUNT(*) FROM sales").Documents[0]["count"].AsInt64;
            var c2 = db2.Execute("SELECT COUNT(*) FROM sales").Documents[0]["count"].AsInt64;
            Assert.Equal(20, c1 + c2);

            // Aggregate across shards
            var sumResult = cluster.Execute("SELECT SUM(amount) FROM sales");
            Assert.Single(sumResult.Documents);
            // Expected: 0 + 10 + 20 + ... + 190 = 1900
            Assert.Equal(1900.0, sumResult.Documents[0]["SUM(amount)"].AsDouble);

            var countResult = cluster.Execute("SELECT COUNT(*) FROM sales");
            Assert.Equal(20, countResult.Documents[0]["count"].AsInt64);
        }
        finally
        {
            try { File.Delete(path1); File.Delete(path1 + ".wal"); File.Delete(path1 + ".recovery"); } catch { }
            try { File.Delete(path2); File.Delete(path2 + ".wal"); File.Delete(path2 + ".recovery"); } catch { }
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task AutoFailover_PromotesOnLeaderSilence()
    {
        int leaderPort = 6000 + System.Random.Shared.Next(50);
        int newLeaderPort = leaderPort + 1;

        var leaderPath = Path.Combine(Path.GetTempPath(), $"af_leader_{Guid.NewGuid():N}.dfdb");
        var followerPath = Path.Combine(Path.GetTempPath(), $"af_follower_{Guid.NewGuid():N}.dfdb");

        try
        {
            // Start leader + follower
            var leader = DocumentForgeDb.Create(leaderPath);
            leader.StartLogicalReplicationServer(leaderPort);
            await System.Threading.Tasks.Task.Delay(200);

            using var follower = DocumentForgeDb.Create(followerPath);
            follower.StartLogicalReplicationFollower("localhost", leaderPort);

            // Wait for connection
            for (int i = 0; i < 30 && leader.GetLogicalFollowerCount() == 0; i++)
                await System.Threading.Tasks.Task.Delay(100);
            Assert.Equal(1, leader.GetLogicalFollowerCount());

            // Insert on leader, replicate
            leader.Insert("orders", """{"pnr": "AF001"}""");
            for (int i = 0; i < 20 && follower.LogicallyReplicatedOps() == 0; i++)
                await System.Threading.Tasks.Task.Delay(100);
            Assert.True(follower.LogicallyReplicatedOps() >= 1);

            // Enable auto-failover on the follower with a short silence timeout
            bool promotedCallbackFired = false;
            int promotedPort = 0;
            follower.EnableAutoFailover(
                newLeaderPort: newLeaderPort,
                silenceTimeout: TimeSpan.FromSeconds(3),
                onPromoted: port => { promotedCallbackFired = true; promotedPort = port; });

            // KILL the leader (simulates crash)
            leader.Dispose();

            // Wait for auto-failover to kick in (silence timeout + checks)
            for (int i = 0; i < 50 && !follower.WasAutoFailoverPromoted; i++)
                await System.Threading.Tasks.Task.Delay(200);

            Assert.True(follower.WasAutoFailoverPromoted, "Follower should have auto-promoted after leader silence");
            Assert.True(promotedCallbackFired, "onPromoted callback should have fired");
            Assert.Equal(newLeaderPort, promotedPort);

            // New leader can accept writes
            Assert.False(follower.IsReadOnly);
            follower.Insert("orders", """{"pnr": "AF002_POST_FAILOVER"}""");

            var result = follower.Execute("SELECT * FROM orders");
            Assert.Equal(2, result.Documents.Count);
        }
        finally
        {
            try { File.Delete(leaderPath); File.Delete(leaderPath + ".wal"); File.Delete(leaderPath + ".recovery"); } catch { }
            try {
                File.Delete(followerPath); File.Delete(followerPath + ".wal");
                File.Delete(followerPath + ".recovery"); File.Delete(followerPath + ".followerseq");
            } catch { }
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task PlannedHandover_ZeroDataLoss()
    {
        // Simulate a datacenter move: old_leader (DC1) hands off to new_leader (DC2)
        // Clients should see no data loss.
        int oldPort = 5900 + System.Random.Shared.Next(50);
        int newPort = oldPort + 1;

        var oldPath = Path.Combine(Path.GetTempPath(), $"old_{Guid.NewGuid():N}.dfdb");
        var newPath = Path.Combine(Path.GetTempPath(), $"new_{Guid.NewGuid():N}.dfdb");

        try
        {
            using var oldLeader = DocumentForgeDb.Create(oldPath);
            oldLeader.StartLogicalReplicationServer(oldPort);
            await System.Threading.Tasks.Task.Delay(200);

            // newLeader starts as a follower of oldLeader
            using var newLeader = DocumentForgeDb.Create(newPath);
            newLeader.StartLogicalReplicationFollower("localhost", oldPort);

            // Wait for connection + handshake to complete
            for (int i = 0; i < 50 && oldLeader.GetLogicalFollowerCount() == 0; i++)
                await System.Threading.Tasks.Task.Delay(100);
            Assert.Equal(1, oldLeader.GetLogicalFollowerCount());

            // Simulate production traffic - writes on old leader replicate to new leader
            for (int i = 0; i < 50; i++)
                oldLeader.Insert("orders", $$"""{"pnr": "PRE_{{i}}"}""");

            // Wait for initial replication
            for (int i = 0; i < 30 && newLeader.FollowerLastSeq < oldLeader.LeaderCurrentSeq; i++)
                await System.Threading.Tasks.Task.Delay(50);

            // ===== THE HANDOVER =====
            // Step 1: oldLeader goes read-only and waits for newLeader to reach final seq
            var finalSeq = oldLeader.BeginPlannedHandover(
                followerLastSeqProbe: () => newLeader.FollowerLastSeq,
                timeout: TimeSpan.FromSeconds(5));

            // Verify oldLeader rejects writes
            Assert.Throws<DocumentForge.Core.DocumentForgeException>(() =>
                oldLeader.Insert("orders", """{"pnr": "REJECTED"}"""));

            // Step 2: newLeader promotes itself
            newLeader.PromoteToLeader(newPort);
            await System.Threading.Tasks.Task.Delay(200);

            // ===== CLIENTS NOW USE NEW LEADER =====
            // Writes succeed on newLeader
            newLeader.Insert("orders", """{"pnr": "POST_1"}""");
            newLeader.Insert("orders", """{"pnr": "POST_2"}""");

            // Verify: all 52 docs are on the new leader (50 pre + 2 post)
            var allDocs = newLeader.Execute("SELECT * FROM orders");
            Assert.Equal(52, allDocs.Documents.Count);

            // Verify the final seq is correct and newLeader is now a leader
            Assert.True(newLeader.LeaderCurrentSeq >= 2); // 2 post-handover writes
            Assert.False(newLeader.IsReadOnly);
            Assert.True(oldLeader.IsReadOnly);
        }
        finally
        {
            try { File.Delete(oldPath); File.Delete(oldPath + ".wal"); File.Delete(oldPath + ".recovery"); } catch { }
            try {
                File.Delete(newPath); File.Delete(newPath + ".wal");
                File.Delete(newPath + ".recovery"); File.Delete(newPath + ".followerseq");
            } catch { }
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task LogicalReplication_CatchupAfterReconnect()
    {
        int port = 5800 + System.Random.Shared.Next(100);
        var leaderPath = Path.Combine(Path.GetTempPath(), $"cleader_{Guid.NewGuid():N}.dfdb");
        var followerPath = Path.Combine(Path.GetTempPath(), $"cfollower_{Guid.NewGuid():N}.dfdb");

        try
        {
            using var leader = DocumentForgeDb.Create(leaderPath);
            leader.StartLogicalReplicationServer(port);
            await System.Threading.Tasks.Task.Delay(150);

            // Follower connects, receives a few inserts, then disconnects
            var follower1 = DocumentForgeDb.Create(followerPath);
            follower1.StartLogicalReplicationFollower("localhost", port);

            for (int i = 0; i < 20 && leader.GetLogicalFollowerCount() == 0; i++)
                await System.Threading.Tasks.Task.Delay(50);

            leader.Insert("orders", """{"pnr": "BEFORE_1"}""");
            leader.Insert("orders", """{"pnr": "BEFORE_2"}""");

            // Wait for follower to receive
            for (int i = 0; i < 20 && follower1.LogicallyReplicatedOps() < 2; i++)
                await System.Threading.Tasks.Task.Delay(50);
            Assert.True(follower1.LogicallyReplicatedOps() >= 2);

            // Disconnect follower
            follower1.Dispose();
            await System.Threading.Tasks.Task.Delay(100);

            // Leader continues to get writes while follower is gone
            leader.Insert("orders", """{"pnr": "DURING_1"}""");
            leader.Insert("orders", """{"pnr": "DURING_2"}""");
            leader.Insert("orders", """{"pnr": "DURING_3"}""");

            // Reconnect - persisted seq should trigger catchup
            using var follower2 = DocumentForgeDb.Open(followerPath);
            follower2.StartLogicalReplicationFollower("localhost", port);

            // Wait for catchup: follower should get the 3 DURING ops
            for (int i = 0; i < 30 && follower2.LogicallyReplicatedOps() < 3; i++)
                await System.Threading.Tasks.Task.Delay(100);

            // Verify all 5 docs are on the follower
            var result = follower2.Execute("SELECT * FROM orders");
            Assert.Equal(5, result.Documents.Count);

            // Gap detection: catchup doesn't involve gaps (we replay in order)
            Assert.Equal(0, follower2.GapsDetected);
        }
        finally
        {
            try { File.Delete(leaderPath); File.Delete(leaderPath + ".wal"); File.Delete(leaderPath + ".recovery"); } catch { }
            try {
                File.Delete(followerPath); File.Delete(followerPath + ".wal");
                File.Delete(followerPath + ".recovery"); File.Delete(followerPath + ".followerseq");
            } catch { }
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task LogicalReplication_FollowerSeesInsertsWithWorkingIndex()
    {
        int port = 5700 + System.Random.Shared.Next(100);
        var leaderPath = Path.Combine(Path.GetTempPath(), $"lleader_{Guid.NewGuid():N}.dfdb");
        var followerPath = Path.Combine(Path.GetTempPath(), $"lfollower_{Guid.NewGuid():N}.dfdb");

        try
        {
            using var leader = DocumentForgeDb.Create(leaderPath);
            leader.StartLogicalReplicationServer(port);
            await System.Threading.Tasks.Task.Delay(200);

            using var follower = DocumentForgeDb.Create(followerPath);
            follower.StartLogicalReplicationFollower("localhost", port);

            // Wait for connect
            for (int i = 0; i < 20 && leader.GetLogicalFollowerCount() == 0; i++)
                await System.Threading.Tasks.Task.Delay(100);
            Assert.Equal(1, leader.GetLogicalFollowerCount());

            // Insert on leader, create index on leader - both should replicate
            leader.Insert("orders", """{"pnr": "A001", "lastName": "Smith"}""");
            leader.Insert("orders", """{"pnr": "A002", "lastName": "Jones"}""");
            leader.Insert("orders", """{"pnr": "A003", "lastName": "Smith"}""");
            leader.CreateIndex("orders", "lastName", "idx_ln");

            // Wait for replication
            for (int i = 0; i < 20 && follower.LogicallyReplicatedOps() < 4; i++)
                await System.Threading.Tasks.Task.Delay(100);

            // Follower should see the data AND use the index (coherent reads!)
            var result = follower.Execute("SELECT * FROM orders WHERE lastName = 'Smith'");
            Assert.True(result.Success);
            Assert.Equal(2, result.Documents.Count);
            Assert.Contains("INDEX_SCAN", result.QueryPlan!);
        }
        finally
        {
            try { File.Delete(leaderPath); File.Delete(leaderPath + ".wal"); File.Delete(leaderPath + ".recovery"); } catch { }
            try { File.Delete(followerPath); File.Delete(followerPath + ".wal"); File.Delete(followerPath + ".recovery"); } catch { }
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Replication_LeaderStreamsToFollower()
    {
        // Pick a free port for this test
        int port = 5500 + System.Random.Shared.Next(100);

        var leaderPath = Path.Combine(Path.GetTempPath(), $"leader_{Guid.NewGuid():N}.dfdb");
        var followerPath = Path.Combine(Path.GetTempPath(), $"follower_{Guid.NewGuid():N}.dfdb");

        try
        {
            // Follower starts from a copy of the leader's initial state (in real setups, use a backup)
            // For this test we'll just copy the file after initial setup.
            using (var seed = DocumentForgeDb.Create(leaderPath))
            {
                seed.Insert("orders", """{"pnr": "INITIAL"}""");
                seed.Flush();
            }
            File.Copy(leaderPath, followerPath, overwrite: true);

            // Leader: start replication server
            using var leader = DocumentForgeDb.Open(leaderPath);
            leader.StartReplicationServer(port);
            await System.Threading.Tasks.Task.Delay(250);

            // Follower: connect to leader
            using var follower = DocumentForgeDb.Open(followerPath);
            follower.StartReplicationFollower("localhost", port);

            // Wait up to 2s for the follower to register
            for (int i = 0; i < 20 && leader.GetFollowerCount() == 0; i++)
                await System.Threading.Tasks.Task.Delay(100);

            Assert.Equal(1, leader.GetFollowerCount());

            // Insert on leader, flush to trigger replication
            leader.Insert("orders", """{"pnr": "REPL001"}""");
            leader.Insert("orders", """{"pnr": "REPL002"}""");
            leader.Flush();

            // Wait for replication to catch up
            await System.Threading.Tasks.Task.Delay(500);

            Assert.True(follower.ReplicatedPageCount() > 0,
                $"Expected follower to have replicated pages, got {follower.ReplicatedPageCount()}");

            // Reopen follower to see the replicated data
            follower.Dispose();
            using var reopened = DocumentForgeDb.Open(followerPath);
            var result = reopened.Execute("SELECT * FROM orders");
            // Follower now has initial doc + any that replicated
            Assert.True(result.Documents.Count >= 1);
        }
        finally
        {
            try { File.Delete(leaderPath); File.Delete(leaderPath + ".wal"); File.Delete(leaderPath + ".recovery"); } catch { }
            try { File.Delete(followerPath); File.Delete(followerPath + ".wal"); File.Delete(followerPath + ".recovery"); } catch { }
        }
    }

    [Fact]
    public void CompositeIndex_TwoFieldEquality()
    {
        _db.Insert("orders", """{"status": "CONFIRMED", "airline": "AA", "amount": 100}""");
        _db.Insert("orders", """{"status": "CONFIRMED", "airline": "UA", "amount": 200}""");
        _db.Insert("orders", """{"status": "CANCELLED", "airline": "AA", "amount": 50}""");
        _db.Insert("orders", """{"status": "CONFIRMED", "airline": "AA", "amount": 300}""");

        // Composite index on (status, airline)
        _db.Execute("CREATE INDEX idx_status_airline ON orders (status, airline)");

        var result = _db.Execute("SELECT * FROM orders WHERE status = 'CONFIRMED' AND airline = 'AA'");
        Assert.True(result.Success);
        Assert.Equal(2, result.Documents.Count);
        Assert.Contains("INDEX_SCAN_COMPOSITE", result.QueryPlan!);

        // Verify the matches
        foreach (var d in result.Documents)
        {
            Assert.Equal("CONFIRMED", d["status"].AsString);
            Assert.Equal("AA", d["airline"].AsString);
        }
    }

    [Fact]
    public void CompositeIndex_WithExtraFilter()
    {
        _db.Insert("orders", """{"status": "CONFIRMED", "airline": "AA", "amount": 100}""");
        _db.Insert("orders", """{"status": "CONFIRMED", "airline": "AA", "amount": 500}""");
        _db.Insert("orders", """{"status": "CONFIRMED", "airline": "UA", "amount": 200}""");

        _db.Execute("CREATE INDEX idx_status_airline ON orders (status, airline)");

        // Composite matches status+airline, amount > 200 becomes a residual filter
        var result = _db.Execute("SELECT * FROM orders WHERE status = 'CONFIRMED' AND airline = 'AA' AND amount > 200");
        Assert.True(result.Success);
        Assert.Single(result.Documents);
        Assert.Equal(500, result.Documents[0]["amount"].AsInt32);
        Assert.Contains("INDEX_SCAN_COMPOSITE", result.QueryPlan!);
    }

    [Fact]
    public void Aggregation_SumAvgMinMax()
    {
        _db.Insert("sales", """{"product": "A", "price": 10}""");
        _db.Insert("sales", """{"product": "B", "price": 20}""");
        _db.Insert("sales", """{"product": "C", "price": 30}""");

        var result = _db.Execute("SELECT SUM(price), AVG(price), MIN(price), MAX(price), COUNT(*) FROM sales");
        Assert.True(result.Success);
        Assert.Single(result.Documents);
        var row = result.Documents[0];
        Assert.Equal(60.0, row["SUM(price)"].AsDouble);
        Assert.Equal(20.0, row["AVG(price)"].AsDouble);
        Assert.Equal(10.0, row["MIN(price)"].AsDouble);
        Assert.Equal(30.0, row["MAX(price)"].AsDouble);
        Assert.Equal(3, row["COUNT(*)"].AsInt64);
    }

    [Fact]
    public void Aggregation_GroupBy()
    {
        _db.Insert("sales", """{"category": "food", "price": 10}""");
        _db.Insert("sales", """{"category": "food", "price": 20}""");
        _db.Insert("sales", """{"category": "tech", "price": 100}""");
        _db.Insert("sales", """{"category": "tech", "price": 200}""");
        _db.Insert("sales", """{"category": "tech", "price": 300}""");

        var result = _db.Execute("SELECT category, SUM(price), COUNT(*) FROM sales GROUP BY category");
        Assert.True(result.Success);
        Assert.Equal(2, result.Documents.Count);

        var food = result.Documents.First(d => d["category"].AsString == "food");
        Assert.Equal(30.0, food["SUM(price)"].AsDouble);
        Assert.Equal(2, food["COUNT(*)"].AsInt64);

        var tech = result.Documents.First(d => d["category"].AsString == "tech");
        Assert.Equal(600.0, tech["SUM(price)"].AsDouble);
        Assert.Equal(3, tech["COUNT(*)"].AsInt64);
    }

    [Fact]
    public void Aggregation_WithWhere()
    {
        _db.Insert("orders", """{"status": "CONFIRMED", "amount": 100}""");
        _db.Insert("orders", """{"status": "CONFIRMED", "amount": 200}""");
        _db.Insert("orders", """{"status": "CANCELLED", "amount": 50}""");

        var result = _db.Execute("SELECT SUM(amount) FROM orders WHERE status = 'CONFIRMED'");
        Assert.True(result.Success);
        Assert.Single(result.Documents);
        Assert.Equal(300.0, result.Documents[0]["SUM(amount)"].AsDouble);
    }

    [Fact]
    public void OverflowPages_LargeDocumentRoundTrip()
    {
        // Build a doc larger than 8156 bytes (single page max)
        var largeString = new string('X', 20_000);  // 20KB of X's
        var json = $$"""{"name": "big doc", "payload": "{{largeString}}", "tag": "A"}""";

        var id = _db.Insert("bigdocs", json);
        var collection = _db.GetCollection("bigdocs");
        Assert.NotNull(collection);

        // Round-trip
        var doc = collection!.FindById(id);
        Assert.NotNull(doc);
        Assert.Equal("big doc", doc!["name"].AsString);
        Assert.Equal("A", doc["tag"].AsString);
        Assert.Equal(20_000, doc["payload"].AsString.Length);
        Assert.Equal(new string('X', 20_000), doc["payload"].AsString);
    }

    [Fact]
    public void OverflowPages_MultipleBigDocs()
    {
        // Insert a few large docs + some small ones, mixed
        var bigPayload = new string('A', 15_000);
        _db.Insert("mixed", """{"type": "small", "n": 1}""");
        _db.Insert("mixed", $$"""{"type": "big", "payload": "{{bigPayload}}"}""");
        _db.Insert("mixed", """{"type": "small", "n": 2}""");

        var results = _db.Execute("SELECT * FROM mixed");
        Assert.True(results.Success);
        Assert.Equal(3, results.Documents.Count);

        var big = results.Documents.First(d => d["type"].AsString == "big");
        Assert.Equal(15_000, big["payload"].AsString.Length);
    }

    [Fact]
    public void OverflowPages_DeletedDocFreesChain()
    {
        var big = new string('Z', 25_000);
        var id1 = _db.Insert("items", $$"""{"big": "{{big}}", "n": 1}""");
        _db.Insert("items", """{"n": 2}""");

        var collection = _db.GetCollection("items")!;
        Assert.NotNull(collection.FindById(id1));

        var result = _db.Execute($"DELETE FROM items WHERE n = 1");
        Assert.Equal(1, result.AffectedCount);

        // After delete, the big doc should be gone
        Assert.Null(collection.FindById(id1));

        // Small doc still there
        var remaining = _db.Execute("SELECT * FROM items");
        Assert.Single(remaining.Documents);
        Assert.Equal(2, remaining.Documents[0]["n"].AsInt32);
    }

    [Fact]
    public void Join_BasicEqualityJoin()
    {
        _db.Insert("orders", """{"pnr": "ABC123", "flightNumber": "AA100"}""");
        _db.Insert("orders", """{"pnr": "DEF456", "flightNumber": "UA200"}""");
        _db.Insert("orders", """{"pnr": "GHI789", "flightNumber": "AA100"}""");

        _db.Insert("flights", """{"flightNumber": "AA100", "departureAirport": "JFK"}""");
        _db.Insert("flights", """{"flightNumber": "UA200", "departureAirport": "LAX"}""");
        _db.Insert("flights", """{"flightNumber": "BA300", "departureAirport": "LHR"}""");

        var result = _db.Execute(
            "SELECT * FROM orders JOIN flights ON orders.flightNumber = flights.flightNumber");

        Assert.True(result.Success);
        Assert.Equal(3, result.Documents.Count); // 2 orders match AA100, 1 matches UA200
        Assert.Contains("HASH_JOIN", result.QueryPlan!);

        // Verify combined structure: should have both "orders" and "flights" sub-documents
        var first = result.Documents[0];
        Assert.NotNull(first["orders"]);
        Assert.NotNull(first["flights"]);
        // The joined flight should match - JFK or LAX
        var depAirport = JsonPathExtractor.Extract(first, "flights.departureAirport").AsString;
        Assert.True(depAirport is "JFK" or "LAX");
    }

    [Fact]
    public void Join_WithWhereClause()
    {
        _db.Insert("orders", """{"pnr": "ABC123", "flightNumber": "AA100", "status": "CONFIRMED"}""");
        _db.Insert("orders", """{"pnr": "DEF456", "flightNumber": "AA100", "status": "CANCELLED"}""");
        _db.Insert("flights", """{"flightNumber": "AA100", "departureAirport": "JFK"}""");

        var result = _db.Execute(
            "SELECT * FROM orders JOIN flights ON orders.flightNumber = flights.flightNumber WHERE orders.status = 'CONFIRMED'");

        Assert.True(result.Success);
        Assert.Single(result.Documents);
        Assert.Equal("ABC123", JsonPathExtractor.Extract(result.Documents[0], "orders.pnr").AsString);
    }

    [Fact]
    public void RangeQuery_UsesIndex()
    {
        _db.Insert("products", """{"name": "Apple", "price": 1.50}""");
        _db.Insert("products", """{"name": "Bread", "price": 3.00}""");
        _db.Insert("products", """{"name": "Cheese", "price": 8.50}""");
        _db.Insert("products", """{"name": "Donut", "price": 2.25}""");
        _db.Insert("products", """{"name": "Egg", "price": 0.50}""");
        _db.CreateIndex("products", "price", "idx_price");

        // WHERE price > 2 should find Bread, Cheese, Donut
        var result = _db.Execute("SELECT * FROM products WHERE price > 2");
        Assert.True(result.Success);
        Assert.Equal(3, result.Documents.Count);
        Assert.Contains("INDEX_RANGE_SCAN", result.QueryPlan!);

        // WHERE price >= 2.25 should find Bread, Cheese, Donut
        result = _db.Execute("SELECT * FROM products WHERE price >= 2.25");
        Assert.Equal(3, result.Documents.Count);

        // WHERE price < 2 should find Apple, Egg
        result = _db.Execute("SELECT * FROM products WHERE price < 2");
        Assert.Equal(2, result.Documents.Count);

        // WHERE price <= 1.50 should find Apple, Egg
        result = _db.Execute("SELECT * FROM products WHERE price <= 1.50");
        Assert.Equal(2, result.Documents.Count);
    }

    [Fact]
    public void PersistentIndex_SurvivesRestart()
    {
        // Insert some documents and create an index
        _db.Insert("orders", """{"pnr": "ABC123", "passenger": {"lastName": "Smith"}}""");
        _db.Insert("orders", """{"pnr": "DEF456", "passenger": {"lastName": "Jones"}}""");
        _db.Insert("orders", """{"pnr": "GHI789", "passenger": {"lastName": "Smith"}}""");
        _db.CreateIndex("orders", "passenger.lastName", "idx_ln");
        _db.CreateIndex("orders", "pnr", "idx_pnr", unique: true);
        _db.Flush();
        _db.Dispose();

        // Reopen and verify indexes exist WITHOUT rebuilding from data
        using var reopened = DocumentForgeDb.Open(_dbPath);

        // Check index metadata
        var indexes = reopened.GetIndexes("orders");
        Assert.Equal(2, indexes.Count);
        Assert.Contains(indexes, i => i.Name == "idx_ln");
        Assert.Contains(indexes, i => i.Name == "idx_pnr" && i.IsUnique);

        // Verify the index actually WORKS (not just metadata) - should hit INDEX_SCAN plan
        var result = reopened.Execute("SELECT * FROM orders WHERE passenger.lastName = 'Smith'");
        Assert.True(result.Success);
        Assert.Equal(2, result.Documents.Count);
        Assert.Contains("INDEX_SCAN", result.QueryPlan!);

        var result2 = reopened.Execute("SELECT * FROM orders WHERE pnr = 'ABC123'");
        Assert.Single(result2.Documents);
        Assert.Contains("INDEX_SCAN", result2.QueryPlan!);
    }

    [Fact]
    public void PersistentIndex_UpdatesPersistAcrossRestart()
    {
        // Set up with index
        _db.Insert("orders", """{"pnr": "ABC123", "status": "CONFIRMED"}""");
        _db.CreateIndex("orders", "status", "idx_status");

        // Insert more docs AFTER index exists
        _db.Insert("orders", """{"pnr": "DEF456", "status": "CANCELLED"}""");
        _db.Insert("orders", """{"pnr": "GHI789", "status": "CONFIRMED"}""");

        _db.Flush();
        _db.Dispose();

        // Reopen - post-index inserts should be findable
        using var reopened = DocumentForgeDb.Open(_dbPath);
        var result = reopened.Execute("SELECT * FROM orders WHERE status = 'CONFIRMED'");

        Assert.True(result.Success);
        Assert.Equal(2, result.Documents.Count);
        Assert.Contains("INDEX_SCAN", result.QueryPlan!);
    }

    [Fact]
    public void OpenExistingDatabase()
    {
        _db.Insert("users", """{"name": "Alice"}""");
        _db.Flush();
        _db.Dispose();

        using var reopened = DocumentForgeDb.Open(_dbPath);
        var result = reopened.Execute("SELECT * FROM users");
        Assert.True(result.Success);
        Assert.Single(result.Documents);
        Assert.Equal("Alice", result.Documents[0]["name"].AsString);
    }

    [Fact]
    public void Query_SelectDistinct_SingleField()
    {
        _db.Insert("flights", """{"airline": "AA", "from": "JFK"}""");
        _db.Insert("flights", """{"airline": "AA", "from": "LAX"}""");
        _db.Insert("flights", """{"airline": "UA", "from": "JFK"}""");
        _db.Insert("flights", """{"airline": "AA", "from": "JFK"}""");

        var result = _db.Execute("SELECT DISTINCT airline FROM flights");

        Assert.True(result.Success);
        Assert.Equal(2, result.Documents.Count);
        Assert.Contains("DISTINCT", result.QueryPlan);

        var airlines = result.Documents.Select(d => d["airline"].AsString).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "AA", "UA" }, airlines);
    }

    [Fact]
    public void Query_SelectDistinct_MultipleFields_DeduplicatesTuples()
    {
        _db.Insert("flights", """{"airline": "AA", "origin": "JFK", "destination": "LAX"}""");
        _db.Insert("flights", """{"airline": "AA", "origin": "JFK", "destination": "LAX"}""");
        _db.Insert("flights", """{"airline": "AA", "origin": "LAX", "destination": "JFK"}""");
        _db.Insert("flights", """{"airline": "UA", "origin": "JFK", "destination": "LAX"}""");

        var result = _db.Execute("SELECT DISTINCT airline, origin, destination FROM flights");

        Assert.True(result.Success);
        // Three unique (airline, origin, destination) tuples - the duplicate AA/JFK/LAX collapses
        Assert.Equal(3, result.Documents.Count);
    }

    [Fact]
    public void Query_SelectDistinct_WithWhere_AppliesWhereThenDedupes()
    {
        _db.Insert("flights", """{"airline": "AA", "status": "ON_TIME"}""");
        _db.Insert("flights", """{"airline": "AA", "status": "DELAYED"}""");
        _db.Insert("flights", """{"airline": "UA", "status": "ON_TIME"}""");
        _db.Insert("flights", """{"airline": "AA", "status": "ON_TIME"}""");

        var result = _db.Execute("SELECT DISTINCT airline FROM flights WHERE status = 'ON_TIME'");

        Assert.True(result.Success);
        Assert.Equal(2, result.Documents.Count);
        var airlines = result.Documents.Select(d => d["airline"].AsString).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "AA", "UA" }, airlines);
    }

    [Fact]
    public void Query_SelectDistinct_NestedPath()
    {
        _db.Insert("orders", """{"pnr": "A1", "passenger": {"lastName": "Smith"}}""");
        _db.Insert("orders", """{"pnr": "A2", "passenger": {"lastName": "Jones"}}""");
        _db.Insert("orders", """{"pnr": "A3", "passenger": {"lastName": "Smith"}}""");

        var result = _db.Execute("SELECT DISTINCT passenger.lastName FROM orders");

        Assert.True(result.Success);
        Assert.Equal(2, result.Documents.Count);
    }

    [Fact]
    public void Replace_UpdatesDocumentAndPreservesId()
    {
        var id = _db.Insert("orders", """{"pnr": "ABC123", "status": "PENDING"}""");

        var ok = _db.Replace("orders", id, """{"pnr": "ABC123", "status": "CONFIRMED", "extra": 42}""");

        Assert.True(ok);
        var coll = _db.GetCollection("orders");
        var doc = coll!.FindById(id);
        Assert.NotNull(doc);
        Assert.Equal("CONFIRMED", doc!["status"].AsString);
        Assert.Equal(42, doc["extra"].AsInt32);
        // _id is preserved on replace - SQL lookup by id still works
        var byIdLookup = _db.Execute($"SELECT * FROM orders WHERE pnr = 'ABC123'");
        Assert.Single(byIdLookup.Documents);
    }

    [Fact]
    public void Replace_KeepsIndexesCoherent()
    {
        var id = _db.Insert("orders", """{"pnr": "OLD123", "passenger": {"lastName": "Original"}}""");
        _db.CreateIndex("orders", "passenger.lastName", "idx_lastname");

        // Pre-replace: indexed lookup finds it
        var before = _db.Execute("SELECT * FROM orders WHERE passenger.lastName = 'Original'");
        Assert.Single(before.Documents);
        Assert.Contains("INDEX_SCAN", before.QueryPlan);

        // Replace with a different lastName
        _db.Replace("orders", id, """{"pnr": "OLD123", "passenger": {"lastName": "Replaced"}}""");

        // Post-replace: old key has nothing, new key has the doc
        var oldQuery = _db.Execute("SELECT * FROM orders WHERE passenger.lastName = 'Original'");
        Assert.Empty(oldQuery.Documents);
        var newQuery = _db.Execute("SELECT * FROM orders WHERE passenger.lastName = 'Replaced'");
        Assert.Single(newQuery.Documents);
        Assert.Contains("INDEX_SCAN", newQuery.QueryPlan);
    }

    [Fact]
    public void Replace_ReturnsFalseWhenIdDoesNotExist()
    {
        var bogus = new DocumentId(Guid.NewGuid());
        var ok = _db.Replace("orders", bogus, """{"x": 1}""");
        Assert.False(ok);
    }

    // --- Regression for github issue #2: PUT same indexed value to same doc ---
    // Before the fix this threw DuplicateKeyException ('Duplicate key in unique
    // index') even though the only existing entry belonged to the doc being
    // updated. The page write committed regardless, leaving the HTTP status and
    // the data state disagreeing.
    [Fact]
    public void Replace_SameUniqueIndexedValue_DoesNotThrowSelfCollision()
    {
        var id = _db.Insert("environments", """{"name": "staging", "ruleBindings": {}}""");
        _db.CreateIndex("environments", "name", "idx_env_name", unique: true);

        // PUT with the SAME name but a different ruleBindings - must succeed.
        var ok = _db.Replace("environments", id, """{"name": "staging", "ruleBindings": {"rule-x": 3}}""");

        Assert.True(ok);
        var updated = _db.GetCollection("environments")!.FindById(id);
        Assert.NotNull(updated);
        Assert.Equal("staging", updated!["name"].AsString);
        Assert.True(updated["ruleBindings"].AsDocument.ContainsKey("rule-x"));

        // Indexed lookup still finds it - this is the issue #1 manifestation
        // we're guarding against (a half-commit would leave the index empty).
        var byField = _db.Execute("SELECT * FROM environments WHERE name = 'staging'");
        Assert.Single(byField.Documents);
        Assert.Contains("INDEX_SCAN", byField.QueryPlan);
    }

    [Fact]
    public void Replace_DifferentDocSameUniqueValue_RejectsAndDoesNotCommit()
    {
        var staging = _db.Insert("environments", """{"name": "staging", "ruleBindings": {}}""");
        var prod    = _db.Insert("environments", """{"name": "prod",    "ruleBindings": {}}""");
        _db.CreateIndex("environments", "name", "idx_env_name", unique: true);

        // Try to rename `prod` to `staging` - should be rejected because `staging`
        // already belongs to a different doc.
        Assert.Throws<DuplicateKeyException>(() =>
            _db.Replace("environments", prod, """{"name": "staging", "ruleBindings": {"rebellion": 1}}"""));

        // Crucial: the page must NOT have been committed. Pre-fix, the page wrote
        // first and the index check failed second, leaving inconsistent state.
        var prodDoc = _db.GetCollection("environments")!.FindById(prod);
        Assert.Equal("prod", prodDoc!["name"].AsString);
        Assert.False(prodDoc["ruleBindings"].AsDocument.ContainsKey("rebellion"));

        // Both indexed lookups still resolve to their original docs.
        var byStaging = _db.Execute("SELECT * FROM environments WHERE name = 'staging'");
        Assert.Single(byStaging.Documents);
        Assert.Equal(staging.ToString(), byStaging.Documents[0].GetId().ToString());
    }

    [Fact]
    public void Replace_ChangingIndexedValueToFreshUniqueValue_UpdatesIndex()
    {
        var id = _db.Insert("environments", """{"name": "old", "v": 1}""");
        _db.CreateIndex("environments", "name", "idx_env_name", unique: true);

        Assert.True(_db.Replace("environments", id, """{"name": "new", "v": 2}"""));

        // Old key is gone from the index; new key resolves correctly.
        Assert.Empty(_db.Execute("SELECT * FROM environments WHERE name = 'old'").Documents);
        var hits = _db.Execute("SELECT * FROM environments WHERE name = 'new'").Documents;
        Assert.Single(hits);
        Assert.Equal(2, hits[0]["v"].AsInt32);
    }

    // --- Regression for github issue #1: surgical per-index rebuild ---
    [Fact]
    public void RebuildIndex_SingleIndex_RestoresLookups()
    {
        _db.Insert("environments", """{"name": "alpha"}""");
        _db.Insert("environments", """{"name": "beta"}""");
        _db.CreateIndex("environments", "name", "idx_env_name", unique: true);

        // Pre-rebuild: lookup works.
        Assert.Single(_db.Execute("SELECT * FROM environments WHERE name = 'alpha'").Documents);

        var ok = _db.RebuildIndex("environments", "idx_env_name");
        Assert.True(ok);

        // Post-rebuild: lookup still works (and the index is freshly populated).
        Assert.Single(_db.Execute("SELECT * FROM environments WHERE name = 'alpha'").Documents);
        Assert.Single(_db.Execute("SELECT * FROM environments WHERE name = 'beta'").Documents);
    }

    [Fact]
    public void RebuildIndex_UnknownIndexName_ReturnsFalse()
    {
        _db.Insert("environments", """{"name": "alpha"}""");
        Assert.False(_db.RebuildIndex("environments", "idx_does_not_exist"));
    }

    // --- Issue #5: bulk insert with per-doc tracking ---
    [Fact]
    public void BulkInsertTracked_AllSucceed_ReturnsIdsNoErrors()
    {
        var docs = new[] {
            BsonDocument.FromJson("""{"name":"alpha"}"""),
            BsonDocument.FromJson("""{"name":"beta"}"""),
            BsonDocument.FromJson("""{"name":"gamma"}"""),
        };

        var r = _db.BulkInsertTracked("widgets", docs);

        Assert.False(r.RolledBack);
        Assert.Empty(r.Errors);
        Assert.Equal(3, r.InsertedIds.Count);
        // Every returned id resolves to a doc on disk.
        var coll = _db.GetCollection("widgets")!;
        foreach (var id in r.InsertedIds)
            Assert.NotNull(coll.FindById(id));
    }

    [Fact]
    public void BulkInsertTracked_PartialFailure_NonAtomic_ReportsErrorsKeepsSuccesses()
    {
        // Set up uniqueness on `name` so we can deterministically force a collision.
        _db.Insert("widgets", """{"name":"alpha"}""");
        _db.CreateIndex("widgets", "name", "idx_widget_name", unique: true);

        var docs = new[] {
            BsonDocument.FromJson("""{"name":"beta"}"""),    // OK
            BsonDocument.FromJson("""{"name":"alpha"}"""),   // duplicate -> fail
            BsonDocument.FromJson("""{"name":"gamma"}"""),   // OK
        };

        var r = _db.BulkInsertTracked("widgets", docs, atomic: false);

        Assert.False(r.RolledBack);
        Assert.Equal(2, r.InsertedIds.Count);
        Assert.Single(r.Errors);
        Assert.Equal(1, r.Errors[0].Index);
        Assert.Contains("alpha", r.Errors[0].Error);

        // beta and gamma are reachable; alpha (the original) is still the only one.
        Assert.Single(_db.Execute("SELECT * FROM widgets WHERE name = 'beta'").Documents);
        Assert.Single(_db.Execute("SELECT * FROM widgets WHERE name = 'gamma'").Documents);
        Assert.Single(_db.Execute("SELECT * FROM widgets WHERE name = 'alpha'").Documents);
    }

    [Fact]
    public void BulkInsertTracked_PartialFailure_Atomic_RollsBackEverything()
    {
        _db.Insert("widgets", """{"name":"alpha"}""");
        _db.CreateIndex("widgets", "name", "idx_widget_name", unique: true);

        var docs = new[] {
            BsonDocument.FromJson("""{"name":"beta"}"""),
            BsonDocument.FromJson("""{"name":"gamma"}"""),
            BsonDocument.FromJson("""{"name":"alpha"}"""),  // collision triggers rollback
            BsonDocument.FromJson("""{"name":"delta"}"""),
        };

        var r = _db.BulkInsertTracked("widgets", docs, atomic: true);

        Assert.True(r.RolledBack);
        Assert.Empty(r.InsertedIds);
        Assert.Single(r.Errors);
        Assert.Equal(2, r.Errors[0].Index);

        // Nothing from the batch should have stuck. Only the original alpha remains.
        Assert.Empty(_db.Execute("SELECT * FROM widgets WHERE name = 'beta'").Documents);
        Assert.Empty(_db.Execute("SELECT * FROM widgets WHERE name = 'gamma'").Documents);
        Assert.Empty(_db.Execute("SELECT * FROM widgets WHERE name = 'delta'").Documents);
        Assert.Single(_db.Execute("SELECT * FROM widgets WHERE name = 'alpha'").Documents);
    }

    [Fact]
    public void BulkInsertTracked_AssignsIdsInInputOrder()
    {
        var docs = new[] {
            BsonDocument.FromJson("""{"label":"first"}"""),
            BsonDocument.FromJson("""{"label":"second"}"""),
            BsonDocument.FromJson("""{"label":"third"}"""),
        };

        var r = _db.BulkInsertTracked("widgets", docs);

        Assert.Equal(3, r.InsertedIds.Count);
        var coll = _db.GetCollection("widgets")!;
        Assert.Equal("first",  coll.FindById(r.InsertedIds[0])!["label"].AsString);
        Assert.Equal("second", coll.FindById(r.InsertedIds[1])!["label"].AsString);
        Assert.Equal("third",  coll.FindById(r.InsertedIds[2])!["label"].AsString);
    }

    [Fact]
    public void Query_SelectDistinct_WithLimit_AppliesDistinctBeforeLimit()
    {
        for (int i = 0; i < 10; i++)
            _db.Insert("flights", $$"""{"airline": "AA", "n": {{i}}}""");
        for (int i = 0; i < 10; i++)
            _db.Insert("flights", $$"""{"airline": "UA", "n": {{i}}}""");

        // 20 docs total, 2 distinct airlines. LIMIT 5 should still only return 2 rows.
        var result = _db.Execute("SELECT DISTINCT airline FROM flights LIMIT 5");

        Assert.True(result.Success);
        Assert.Equal(2, result.Documents.Count);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + ".wal"); } catch { }
        try { File.Delete(_dbPath + ".recovery"); } catch { }
        try { File.Delete(_dbPath + ".followerseq"); } catch { }
    }
}
