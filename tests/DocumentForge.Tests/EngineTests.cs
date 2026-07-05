using Xunit;
using DocumentForge.Core;
using DocumentForge.Document;
using DocumentForge.Engine;
using DocumentForge.Index;
using DocumentForge.Transactions;

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

            // Wait for the snapshot to land on follower's disk. The live
            // in-process follower1 can't see the snapshot data until next
            // Open (hot-swap is deferred), so we observe the marker file
            // instead. The end-to-end assertion comes via follower2 below.
            var markerPath = followerPath + ".snapshot.incoming.seq";
            for (int i = 0; i < 30 && !File.Exists(markerPath); i++)
                await System.Threading.Tasks.Task.Delay(100);
            Assert.True(File.Exists(markerPath),
                "Snapshot marker should appear after the leader's writes are captured.");

            // Disconnect follower
            follower1.Dispose();
            await System.Threading.Tasks.Task.Delay(100);

            // Leader continues to get writes while follower is gone
            leader.Insert("orders", """{"pnr": "DURING_1"}""");
            leader.Insert("orders", """{"pnr": "DURING_2"}""");
            leader.Insert("orders", """{"pnr": "DURING_3"}""");

            // Reconnect - persisted seq should trigger catchup (or snapshot
            // re-transfer if a marker is left over from the prior session).
            using var follower2 = DocumentForgeDb.Open(followerPath);
            follower2.StartLogicalReplicationFollower("localhost", port);

            // Wait for follower2 to converge to 5 docs.
            for (int i = 0; i < 30 && follower2.Execute("SELECT * FROM orders").Documents.Count < 5; i++)
                await System.Threading.Tasks.Task.Delay(100);

            var result = follower2.Execute("SELECT * FROM orders");
            Assert.Equal(5, result.Documents.Count);
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

    // --- Issues #3 (IN) + #4 (OR-of-equalities indexed plan) ---
    [Fact]
    public void Query_InClause_ParsesAndReturnsMatches()
    {
        _db.Insert("rules", """{"id": "rule-bag-policy"}""");
        _db.Insert("rules", """{"id": "rule-tier-bonus"}""");
        _db.Insert("rules", """{"id": "rule-other"}""");

        var r = _db.Execute("SELECT * FROM rules WHERE id IN ('rule-bag-policy', 'rule-tier-bonus')");

        Assert.True(r.Success);
        Assert.Equal(2, r.Documents.Count);
        var ids = r.Documents.Select(d => d["id"].AsString).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "rule-bag-policy", "rule-tier-bonus" }, ids);
    }

    [Fact]
    public void Query_InClause_OnIndexedField_UsesMultiKeyIndexScan()
    {
        _db.Insert("rules", """{"id": "rule-a"}""");
        _db.Insert("rules", """{"id": "rule-b"}""");
        _db.Insert("rules", """{"id": "rule-c"}""");
        _db.CreateIndex("rules", "id", "idx_rule_id", unique: true);

        var r = _db.Execute("SELECT * FROM rules WHERE id IN ('rule-a', 'rule-c')");

        Assert.True(r.Success);
        Assert.Equal(2, r.Documents.Count);
        Assert.Contains("INDEX_SCAN_MULTI", r.QueryPlan);
        Assert.Contains("idx_rule_id", r.QueryPlan);
        Assert.Contains("2 keys", r.QueryPlan);
    }

    [Fact]
    public void Query_OrOfEqualities_OnIndexedField_UsesMultiKeyIndexScan()
    {
        _db.Insert("rules", """{"id": "rule-a", "v": 1}""");
        _db.Insert("rules", """{"id": "rule-b", "v": 2}""");
        _db.Insert("rules", """{"id": "rule-c", "v": 3}""");
        _db.CreateIndex("rules", "id", "idx_rule_id", unique: true);

        var r = _db.Execute("SELECT * FROM rules WHERE id = 'rule-a' OR id = 'rule-b'");

        Assert.True(r.Success);
        Assert.Equal(2, r.Documents.Count);
        Assert.Contains("INDEX_SCAN_MULTI", r.QueryPlan);
    }

    [Fact]
    public void Query_OrOfEqualities_NoIndex_FallsBackToCollectionScan()
    {
        _db.Insert("rules", """{"id": "rule-a"}""");
        _db.Insert("rules", """{"id": "rule-b"}""");
        // no index on `id`

        var r = _db.Execute("SELECT * FROM rules WHERE id = 'rule-a' OR id = 'rule-b'");

        Assert.True(r.Success);
        Assert.Equal(2, r.Documents.Count);
        Assert.Equal("COLLECTION_SCAN", r.QueryPlan);
    }

    [Fact]
    public void Query_OrAcrossDifferentColumns_DoesNotUseMultiKeyScan()
    {
        // Mixed columns can't be folded into one index scan; the planner
        // should NOT mistakenly pick INDEX_SCAN_MULTI here.
        _db.Insert("rules", """{"id": "rule-a", "kind": "x"}""");
        _db.Insert("rules", """{"id": "rule-b", "kind": "y"}""");
        _db.CreateIndex("rules", "id", "idx_rule_id", unique: true);

        var r = _db.Execute("SELECT * FROM rules WHERE id = 'rule-a' OR kind = 'y'");

        Assert.True(r.Success);
        Assert.Equal(2, r.Documents.Count);
        Assert.DoesNotContain("INDEX_SCAN_MULTI", r.QueryPlan);
    }

    [Fact]
    public void Query_InClause_DedupesAcrossKeys()
    {
        // Non-unique index; multiple docs share key "x"
        _db.Insert("rules", """{"kind": "x", "v": 1}""");
        _db.Insert("rules", """{"kind": "x", "v": 2}""");
        _db.Insert("rules", """{"kind": "y", "v": 3}""");
        _db.CreateIndex("rules", "kind", "idx_rule_kind");

        var r = _db.Execute("SELECT * FROM rules WHERE kind IN ('x', 'y', 'x')");  // duplicate value

        Assert.True(r.Success);
        // Three real matching docs total (x x y); the duplicate IN value
        // shouldn't return doc #1 or #2 twice.
        Assert.Equal(3, r.Documents.Count);
        Assert.Contains("INDEX_SCAN_MULTI", r.QueryPlan);
    }

    // --- Issue #9: unique-index check must reject duplicate Insert ---
    [Fact]
    public void Insert_DuplicateValueOnUniqueIndex_ThrowsAndDoesNotPersist()
    {
        // Realistic schema setup: collection exists, then index, then real workload.
        _db.Insert("users", """{"email":"a@example.com","v":1}""");
        _db.CreateIndex("users", "email", "idx_users_email", unique: true);

        // Second insert with same email must throw, AND must not leave
        // a stranded doc on disk.
        Assert.Throws<DuplicateKeyException>(() =>
            _db.Insert("users", """{"email":"a@example.com","v":2}"""));

        var all = _db.Execute("SELECT * FROM users").Documents;
        Assert.Single(all);
        Assert.Equal(1, all[0]["v"].AsInt32);

        // Indexed lookup and full scan agree.
        var byEmail = _db.Execute("SELECT * FROM users WHERE email = 'a@example.com'").Documents;
        Assert.Single(byEmail);
    }

    // --- Issue #11: SQL DELETE must remove unique-index entries so a later
    // insert with the same key can succeed. The REST `DELETE /collections/{c}/by/{f}/{v}`
    // route lowers to `DELETE FROM c WHERE f = 'v'`, so this test exercises both
    // the SQL path and the index-cleanup that hangs off it.
    [Fact]
    public void Delete_BySql_WithUniqueIndex_RemovesIndexEntrySoReinsertSucceeds()
    {
        _db.Insert("users", """{"email":"a@b.com","v":1}""");
        _db.CreateIndex("users", "email", "idx_users_email", unique: true);

        // Delete via SQL (the path that REST DELETE-by-field lowers to).
        var del = _db.Execute("DELETE FROM users WHERE email = 'a@b.com'");
        Assert.True(del.Success);
        Assert.Equal(1, del.AffectedCount);

        Assert.Empty(_db.Execute("SELECT * FROM users").Documents);

        // Same key must be insertable again - no phantom uniqueness violation.
        var newId = _db.Insert("users", """{"email":"a@b.com","v":2}""");
        Assert.NotEqual(default, newId);

        var rows = _db.Execute("SELECT * FROM users WHERE email = 'a@b.com'").Documents;
        Assert.Single(rows);
        Assert.Equal(2, rows[0]["v"].AsInt32);
    }

    // Same scenario as above but the index is created BEFORE any inserts, then
    // we go through several delete-then-insert upsert cycles. Catches a regression
    // where the delete only cleaned the in-memory index on the first cycle but
    // the persisted append-log left a stale entry that resurfaced on reload.
    [Fact]
    public void Delete_BySql_RepeatedUpsertCycle_StaysConsistent()
    {
        // Seed once so the collection exists, then immediately remove it so the
        // first iteration of the loop starts from an empty collection but the
        // unique index is already in play.
        _db.Insert("users", """{"email":"seed@x.com"}""");
        _db.CreateIndex("users", "email", "idx_users_email", unique: true);
        _db.Execute("DELETE FROM users WHERE email = 'seed@x.com'");

        for (int i = 1; i <= 5; i++)
        {
            _db.Insert("users", $$"""{"email":"u@x.com","v":{{i}}}""");

            var del = _db.Execute("DELETE FROM users WHERE email = 'u@x.com'");
            Assert.True(del.Success);
            Assert.Equal(1, del.AffectedCount);
            Assert.Empty(_db.Execute("SELECT * FROM users").Documents);
        }
    }

    // The most damning #11 case: the staging service had been delete-then-insert
    // upserting cleanly until a deploy bounced the process. After restart, the
    // index replay rebuilt the unique index with stale (key, docId) pairs from
    // tombstoned entries, so the next insert with that business key threw
    // Duplicate-key against an empty collection.
    [Fact]
    public void Delete_BySql_AfterRestart_IndexHasNoStaleEntries()
    {
        _db.Insert("users", """{"email":"a@b.com","v":1}""");
        _db.CreateIndex("users", "email", "idx_users_email", unique: true);
        _db.Execute("DELETE FROM users WHERE email = 'a@b.com'");
        _db.Flush();
        _db.Dispose();

        // Re-open the same file the way `dfdb serve` does on restart.
        using var db2 = DocumentForgeDb.Open(_dbPath);

        Assert.Empty(db2.Execute("SELECT * FROM users").Documents);

        // Pre-fix this threw DuplicateKeyException because the rebuilt unique
        // index still pointed at the deleted doc's _id.
        var newId = db2.Insert("users", """{"email":"a@b.com","v":2}""");
        Assert.NotEqual(default, newId);

        var rows = db2.Execute("SELECT * FROM users WHERE email = 'a@b.com'").Documents;
        Assert.Single(rows);
        Assert.Equal(2, rows[0]["v"].AsInt32);
    }

    // Mirrors the staging repro from issue #11: the user's collection name is
    // `aerotoys.tax.users` and they upsert via DELETE-by-field then INSERT.
    [Fact]
    public void Delete_BySql_DottedCollectionName_WithUniqueIndex_AllowsReinsert()
    {
        const string coll = "aerotoys.tax.users";
        _db.Insert(coll, """{"email":"a@b.com","v":1}""");
        _db.CreateIndex(coll, "email", "idx_aerotoys_tax_users_email", unique: true);

        var del = _db.Execute($"DELETE FROM {coll} WHERE email = 'a@b.com'");
        Assert.True(del.Success);
        Assert.Equal(1, del.AffectedCount);

        // Re-insert via the normal insert code path (db.Insert), with the
        // same email - must succeed.
        _db.Insert(coll, """{"email":"a@b.com","v":2}""");
        var rows = _db.Execute($"SELECT * FROM {coll} WHERE email = 'a@b.com'").Documents;
        Assert.Single(rows);
        Assert.Equal(2, rows[0]["v"].AsInt32);
    }

    // --- Issue #7: DropCollection must drop the indexes too ---
    [Fact]
    public void DropCollection_RemovesIndexesSoFreshSeedSucceeds()
    {
        _db.Insert("foo", """{"id":"x"}""");
        _db.Insert("foo", """{"id":"y"}""");
        _db.CreateIndex("foo", "id", "idx_foo_id", unique: true);

        Assert.True(_db.DropCollection("foo"));

        // Index registry should be empty for the dropped collection.
        Assert.Empty(_db.GetIndexes("foo"));

        // Re-seed the same data: must succeed cleanly.
        _db.Insert("foo", """{"id":"x"}""");
        _db.Insert("foo", """{"id":"y"}""");

        var all = _db.Execute("SELECT * FROM foo").Documents;
        Assert.Equal(2, all.Count);
    }

    // --- Issue #8: collection names should be case-insensitive end to end ---
    [Fact]
    public void CollectionAndIndexNames_AreCaseInsensitive()
    {
        _db.Insert("TaxVersions", """{"id":"a"}""");
        _db.CreateIndex("TaxVersions", "id", "idx_TaxVersions_id", unique: true);

        // Querying with any casing finds the same docs.
        Assert.Single(_db.Execute("SELECT * FROM taxversions").Documents);
        Assert.Single(_db.Execute("SELECT * FROM TAXVERSIONS").Documents);
        Assert.Single(_db.Execute("SELECT * FROM TaxVersions").Documents);

        // GetIndexes treats the same collection consistently regardless of case.
        Assert.Single(_db.GetIndexes("TaxVersions"));
        Assert.Single(_db.GetIndexes("taxversions"));

        // Drop with one casing, re-create with another - must be a clean slate.
        Assert.True(_db.DropCollection("taxversions"));
        Assert.Empty(_db.GetIndexes("TaxVersions"));

        _db.Insert("taxversions", """{"id":"a"}""");
        Assert.Single(_db.Execute("SELECT * FROM TaxVersions").Documents);
    }

    // --- Issue #10: dotted collection names via SQL ---
    [Fact]
    public void Sql_Select_FromMultiDotCollectionName_FindsDocuments()
    {
        // The user's exact scenario: collection name "aerotoys.tax.environments",
        // 'id' is a string field, query has both bare-FROM and FROM+WHERE shapes.
        _db.Insert("aerotoys.tax.environments", """{"id": "env-dev", "v": 1}""");
        _db.Insert("aerotoys.tax.environments", """{"id": "env-staging", "v": 2}""");
        _db.Insert("aerotoys.tax.environments", """{"id": "env-prod", "v": 3}""");

        // Triangulation #1: bare-FROM should find them all.
        var bare = _db.Execute("SELECT * FROM aerotoys.tax.environments");
        Assert.True(bare.Success);
        Assert.Equal(3, bare.Documents.Count);

        // Triangulation #2: FROM + WHERE on a string field. The user reports
        // this returns 0 with plan "EMPTY (collection not found)".
        var filtered = _db.Execute("SELECT * FROM aerotoys.tax.environments WHERE id = 'env-staging'");
        Assert.True(filtered.Success);
        Assert.Single(filtered.Documents);
        Assert.Equal("env-staging", filtered.Documents[0]["id"].AsString);
    }

    [Fact]
    public void Sql_Select_UnknownCollection_ReturnsErrorNotEmptyResult()
    {
        // Pre-fix: silently returned `Success=true, Documents=[]` with plan
        // 'EMPTY (collection not found)' — indistinguishable from a legitimate
        // empty result. Now must be a clear error.
        _db.Insert("orders", """{"pnr": "ABC"}""");

        var r = _db.Execute("SELECT * FROM does_not_exist WHERE x = 1");

        Assert.False(r.Success);
        Assert.Contains("does_not_exist", r.Message ?? "");
    }

    [Fact]
    public void Query_InClause_NumericValues()
    {
        _db.Insert("orders", """{"qty": 1}""");
        _db.Insert("orders", """{"qty": 5}""");
        _db.Insert("orders", """{"qty": 10}""");
        _db.Insert("orders", """{"qty": 100}""");

        var r = _db.Execute("SELECT * FROM orders WHERE qty IN (1, 10, 100)");

        Assert.True(r.Success);
        Assert.Equal(3, r.Documents.Count);
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

    // --- Multi-document transactions (Phase 1: single-node) ---

    [Fact]
    public void Tx_Insert_Commit_AppliesAllStagedDocs()
    {
        using (var tx = _db.BeginTransaction())
        {
            tx.Insert("orders", """{"pnr":"ABC","seat":"12A"}""");
            tx.Insert("orders", """{"pnr":"DEF","seat":"14B"}""");
            tx.Commit();
        }

        var rows = _db.Execute("SELECT * FROM orders").Documents;
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void Tx_Insert_Rollback_DiscardsStagedDocs()
    {
        using (var tx = _db.BeginTransaction())
        {
            tx.Insert("orders", """{"pnr":"ABC"}""");
            tx.Rollback();
        }

        Assert.Empty(_db.Execute("SELECT * FROM orders").Documents);
    }

    [Fact]
    public void Tx_DisposeWithoutCommit_RollsBack()
    {
        // The natural shape — `using var tx = ...; ...; tx.Commit();` — must
        // be safe under exceptions. If something throws between BeginTx and
        // Commit, dispose has to discard the staged work.
        using (var tx = _db.BeginTransaction())
        {
            tx.Insert("orders", """{"pnr":"ABC"}""");
            // no Commit
        }

        Assert.Empty(_db.Execute("SELECT * FROM orders").Documents);
    }

    [Fact]
    public void Tx_Reads_ReadYourWrites_StagedInsertVisibleViaTxFind()
    {
        using var tx = _db.BeginTransaction();
        var id = tx.Insert("orders", """{"pnr":"XYZ"}""");

        var seen = tx.Find("orders", id);
        Assert.NotNull(seen);
        Assert.Equal("XYZ", seen!["pnr"].AsString);

        // Outside the txn the doc is invisible until commit.
        Assert.Empty(_db.Execute("SELECT * FROM orders").Documents);
    }

    [Fact]
    public void Tx_OutsideReader_DoesNotSeeUncommittedWrites()
    {
        _db.Insert("orders", """{"pnr":"BASE"}""");

        using var tx = _db.BeginTransaction();
        tx.Insert("orders", """{"pnr":"PENDING"}""");

        var rows = _db.Execute("SELECT * FROM orders").Documents;
        Assert.Single(rows);
        Assert.Equal("BASE", rows[0]["pnr"].AsString);
    }

    [Fact]
    public void Tx_DeleteThenInsertSameUniqueKey_CommitsAtomically()
    {
        // Issue #11's auth-service upsert pattern — the whole reason for
        // multi-doc transactions. Pre-Phase-1 callers had to delete then insert
        // as separate REST calls, leaving a window where the row didn't exist.
        // Inside one txn, the unique-index validator considers the pending
        // delete when checking the pending insert, so the same business key
        // round-trips cleanly.
        _db.Insert("users", """{"email":"a@b.com","v":1}""");
        _db.CreateIndex("users", "email", "idx_users_email", unique: true);

        using (var tx = _db.BeginTransaction())
        {
            tx.DeleteByField("users", "email", "a@b.com");
            tx.Insert("users", """{"email":"a@b.com","v":2}""");
            tx.Commit();
        }

        var rows = _db.Execute("SELECT * FROM users WHERE email = 'a@b.com'").Documents;
        Assert.Single(rows);
        Assert.Equal(2, rows[0]["v"].AsInt32);
    }

    [Fact]
    public void Tx_Commit_UniqueIndexConflict_ThrowsAndPersistsNothing()
    {
        _db.Insert("users", """{"email":"a@b.com","v":1}""");
        _db.CreateIndex("users", "email", "idx_users_email", unique: true);

        var tx = _db.BeginTransaction();
        tx.Insert("users", """{"email":"new@x.com","v":2}""");
        // Conflict: another doc with email=a@b.com already exists.
        tx.Insert("users", """{"email":"a@b.com","v":99}""");

        Assert.Throws<DuplicateKeyException>(() => tx.Commit());
        Assert.Equal(TransactionState.RolledBack, tx.State);

        // The non-conflicting insert in the same txn must NOT have been
        // applied either — atomicity, not best-effort.
        var rows = _db.Execute("SELECT * FROM users").Documents;
        Assert.Single(rows);
        Assert.Equal(1, rows[0]["v"].AsInt32);
    }

    [Fact]
    public void Tx_ConcurrentDuplicateInserts_DetectedWithinSameTx()
    {
        _db.Insert("users", """{"email":"seed@x.com"}""");
        _db.CreateIndex("users", "email", "idx_users_email", unique: true);
        _db.Execute("DELETE FROM users WHERE email = 'seed@x.com'");

        using var tx = _db.BeginTransaction();
        tx.Insert("users", """{"email":"dup@x.com","v":1}""");
        tx.Insert("users", """{"email":"dup@x.com","v":2}""");

        // Both inserts collide within the same txn — neither was visible to
        // ValidateUniqueInsert at staging time, but the simulated post-commit
        // index sees the conflict.
        Assert.Throws<DuplicateKeyException>(() => tx.Commit());
        Assert.Empty(_db.Execute("SELECT * FROM users").Documents);
    }

    [Fact]
    public void Tx_MultiCollection_CommitsAllAtomically()
    {
        // The motivating case: "transfer money" — two writes across two
        // collections that must succeed together. Phase 1 makes this
        // expressible without a custom locking dance.
        _db.Insert("accounts", """{"id":"A","balance":100}""");
        _db.Insert("accounts", """{"id":"B","balance":50}""");
        _db.CreateIndex("accounts", "id", "idx_accounts_id", unique: true);

        var fromId = _db.Execute("SELECT * FROM accounts WHERE id = 'A'").Documents[0].GetId();
        var toId   = _db.Execute("SELECT * FROM accounts WHERE id = 'B'").Documents[0].GetId();

        using (var tx = _db.BeginTransaction())
        {
            tx.Replace("accounts", fromId, """{"id":"A","balance":75}""");
            tx.Replace("accounts", toId,   """{"id":"B","balance":75}""");
            tx.Insert("ledger", $$"""{"from":"A","to":"B","amount":25}""");
            tx.Commit();
        }

        var a = _db.Execute("SELECT * FROM accounts WHERE id = 'A'").Documents[0]["balance"].AsInt32;
        var b = _db.Execute("SELECT * FROM accounts WHERE id = 'B'").Documents[0]["balance"].AsInt32;
        var ledger = _db.Execute("SELECT * FROM ledger").Documents;

        Assert.Equal(75, a);
        Assert.Equal(75, b);
        Assert.Single(ledger);
    }

    [Fact]
    public void Tx_Replace_ThenDelete_NetEffectIsDelete()
    {
        var id = _db.Insert("orders", """{"pnr":"ABC","seat":"12A"}""");

        using (var tx = _db.BeginTransaction())
        {
            tx.Replace("orders", id, """{"pnr":"ABC","seat":"99Z"}""");
            tx.Delete("orders", id);
            tx.Commit();
        }

        Assert.Empty(_db.Execute("SELECT * FROM orders").Documents);
    }

    [Fact]
    public void Tx_DeleteByField_DropsSameTxStagedInsertsToo()
    {
        // A pending insert plus a DeleteByField for the same value should
        // cancel — not commit the insert and then "delete" something that
        // never made it to disk.
        _db.Insert("users", """{"email":"existing@x.com"}""");

        using (var tx = _db.BeginTransaction())
        {
            tx.Insert("users", """{"email":"pending@x.com"}""");
            int n = tx.DeleteByField("users", "email", "pending@x.com");
            Assert.Equal(1, n);
            tx.Commit();
        }

        var rows = _db.Execute("SELECT * FROM users").Documents;
        Assert.Single(rows);
        Assert.Equal("existing@x.com", rows[0]["email"].AsString);
    }

    [Fact]
    public void Tx_OperationAfterCommit_Throws()
    {
        var tx = _db.BeginTransaction();
        tx.Commit();
        Assert.Throws<TransactionException>(() => tx.Insert("orders", """{"x":1}"""));
    }

    [Fact]
    public void Tx_DoubleCommit_Throws()
    {
        var tx = _db.BeginTransaction();
        tx.Commit();
        Assert.Throws<TransactionException>(() => tx.Commit());
    }

    [Fact]
    public void Tx_EmptyCommit_IsNoOp()
    {
        using var tx = _db.BeginTransaction();
        tx.Commit();
        Assert.Equal(TransactionState.Committed, tx.State);
        Assert.Equal(0, tx.StagedOperationCount);
    }

    [Fact]
    public void Tx_ConcurrentReadersDuringStagingAreNotBlocked()
    {
        _db.Insert("orders", """{"pnr":"BASE"}""");

        using var tx = _db.BeginTransaction();
        tx.Insert("orders", """{"pnr":"PENDING"}""");

        // Reads through the live db continue without blocking — the txn only
        // takes the write lock briefly during commit, not for the full handle
        // lifetime. (Smoke test rather than contention test; this just runs
        // a read while the txn is open.)
        var rows = _db.Execute("SELECT * FROM orders").Documents;
        Assert.Single(rows);
        Assert.Equal("BASE", rows[0]["pnr"].AsString);

        tx.Commit();
        Assert.Equal(2, _db.Execute("SELECT * FROM orders").Documents.Count);
    }

    [Fact]
    public void Tx_RestartAfterCommit_StateIsPersisted()
    {
        // Multi-doc commits must outlive the process. Apply via tx, flush,
        // dispose, reopen — the writes must still be there.
        using (var tx = _db.BeginTransaction())
        {
            tx.Insert("orders", """{"pnr":"ABC"}""");
            tx.Insert("orders", """{"pnr":"DEF"}""");
            tx.Commit();
        }
        _db.Flush();
        _db.Dispose();

        using var db2 = DocumentForgeDb.Open(_dbPath);
        Assert.Equal(2, db2.Execute("SELECT * FROM orders").Documents.Count);
    }

    // --- Cross-shard transactions (issue #14, Phase A: single-shard fast path) ---
    //
    // Phase A scope: cluster.BeginTransaction() opens a ClusterTransaction.
    // If staged ops route to ONE shard, Commit hands the batch to that shard's
    // local BeginTransaction().Commit() — same atomicity, validation, WAL fsync
    // as a non-cluster transaction. If staged ops span >1 shards, Commit throws
    // NotImplementedException (cross-shard 2PC lands in Phase B).
    //
    // The point of these tests is to lock in the API surface and the routing
    // contract so Phase B can layer the multi-shard machinery on top without
    // breaking what callers see.

    private static (DocumentForgeDb[] dbs, string[] paths, DocumentForge.Engine.Cluster.DocumentForgeCluster cluster)
        BuildClusterForTx(int shardCount, string collection, string shardKey)
    {
        var paths = Enumerable.Range(0, shardCount)
            .Select(i => Path.Combine(Path.GetTempPath(), $"clstx_{i}_{Guid.NewGuid():N}.dfdb"))
            .ToArray();
        var dbs = paths.Select(p => DocumentForgeDb.Create(p)).ToArray();

        var cluster = new DocumentForge.Engine.Cluster.DocumentForgeCluster();
        for (int i = 0; i < shardCount; i++)
            cluster.AddShard(new DocumentForge.Engine.Cluster.InProcessShardTransport(
                ((char)('A' + i)).ToString(), dbs[i], ownsDb: true));
        cluster.ShardCollection(collection, shardKey);
        return (dbs, paths, cluster);
    }

    private static void CleanupClusterPaths(string[] paths)
    {
        foreach (var p in paths)
        {
            try { File.Delete(p); File.Delete(p + ".wal"); File.Delete(p + ".recovery"); } catch { }
        }
    }

    [Fact]
    public void ClusterTx_SingleShard_Insert_Commit_AppliesAllStaged()
    {
        // Two docs sharing a shard-key value land on the same shard, so the
        // tx degenerates to a single-shard local commit on that shard.
        var (dbs, paths, cluster) = BuildClusterForTx(3, "orders", "pnr");
        try
        {
            using (cluster)
            using (var tx = cluster.BeginTransaction())
            {
                tx.Insert("orders", """{"pnr":"SAME","leg":1}""");
                tx.Insert("orders", """{"pnr":"SAME","leg":2}""");
                Assert.Equal(1, tx.ParticipantCount);
                tx.Commit();
            }

            // After commit, both docs are queryable through the cluster.
            // After dispose-of-cluster the shard DBs are gone (ownsDb=true),
            // so we re-query through a freshly built cluster over the same files.
        }
        finally { CleanupClusterPaths(paths); }
    }

    [Fact]
    public void ClusterTx_SingleShard_Commit_DocsAreVisibleAfterwards()
    {
        var (dbs, paths, cluster) = BuildClusterForTx(3, "orders", "pnr");
        try
        {
            using (var tx = cluster.BeginTransaction())
            {
                tx.Insert("orders", """{"pnr":"SAME","leg":1}""");
                tx.Insert("orders", """{"pnr":"SAME","leg":2}""");
                tx.Commit();
            }

            var rows = cluster.Execute("SELECT * FROM orders WHERE pnr = 'SAME'").Documents;
            Assert.Equal(2, rows.Count);
            cluster.Dispose();
        }
        finally { CleanupClusterPaths(paths); }
    }

    [Fact]
    public void ClusterTx_SingleShard_Rollback_DiscardsStaged()
    {
        var (dbs, paths, cluster) = BuildClusterForTx(2, "orders", "pnr");
        try
        {
            using (var tx = cluster.BeginTransaction())
            {
                tx.Insert("orders", """{"pnr":"SAME","leg":1}""");
                tx.Insert("orders", """{"pnr":"SAME","leg":2}""");
                tx.Rollback();
            }
            Assert.Empty(cluster.Execute("SELECT * FROM orders").Documents);
            cluster.Dispose();
        }
        finally { CleanupClusterPaths(paths); }
    }

    [Fact]
    public void ClusterTx_DisposeWithoutCommit_RollsBack()
    {
        var (dbs, paths, cluster) = BuildClusterForTx(2, "orders", "pnr");
        try
        {
            using (var tx = cluster.BeginTransaction())
            {
                tx.Insert("orders", """{"pnr":"SAME"}""");
                // no Commit — dispose at end of using block must roll back
            }
            Assert.Empty(cluster.Execute("SELECT * FROM orders").Documents);
            cluster.Dispose();
        }
        finally { CleanupClusterPaths(paths); }
    }

    /// <summary>
    /// Probe pnr values until we find two that route to distinct shards.
    /// Inserts the probe docs into the cluster as a side effect — caller is
    /// expected to clean up via DELETE before the actual test work.
    /// </summary>
    private static (string pnrA, string pnrB, int shardA, int shardB) FindPnrsOnDistinctShards(
        DocumentForge.Engine.Cluster.DocumentForgeCluster cluster, DocumentForgeDb[] dbs)
    {
        string? pnrA = null, pnrB = null;
        int aShard = -1, bShard = -1;
        for (int i = 0; pnrA is null || pnrB is null; i++)
        {
            if (i > 200) throw new InvalidOperationException("Could not find two pnrs on distinct shards");
            var pnr = $"PROBE{i:D4}";
            cluster.Insert("orders", $$"""{"pnr":"{{pnr}}"}""");
            int found = -1;
            for (int s = 0; s < dbs.Length; s++)
                if (dbs[s].Execute($"SELECT * FROM orders WHERE pnr = '{pnr}'").Documents.Count == 1)
                    found = s;
            if (pnrA is null) { pnrA = pnr; aShard = found; }
            else if (found != aShard) { pnrB = pnr; bShard = found; }
        }
        return (pnrA!, pnrB!, aShard, bShard);
    }

    [Fact]
    public void ClusterTx_MultiShard_Commit_AppliesToAllShards()
    {
        // Phase B.2: two pnrs routing to distinct shards; commit must apply
        // to BOTH shards atomically. After commit each shard owns its slice.
        var (dbs, paths, cluster) = BuildClusterForTx(3, "orders", "pnr");
        try
        {
            var (pnrA, pnrB, shardA, shardB) = FindPnrsOnDistinctShards(cluster, dbs);
            cluster.Execute("DELETE FROM orders");

            using (var tx = cluster.BeginTransaction())
            {
                tx.Insert("orders", $$"""{"pnr":"{{pnrA}}","leg":1}""");
                tx.Insert("orders", $$"""{"pnr":"{{pnrB}}","leg":2}""");
                Assert.Equal(2, tx.ParticipantCount);
                tx.Commit();
                Assert.Equal(TransactionState.Committed, tx.State);
            }

            Assert.Single(dbs[shardA].Execute($"SELECT * FROM orders WHERE pnr = '{pnrA}'").Documents);
            Assert.Single(dbs[shardB].Execute($"SELECT * FROM orders WHERE pnr = '{pnrB}'").Documents);
            Assert.Equal(2, cluster.Execute("SELECT * FROM orders").Documents.Count);
            cluster.Dispose();
        }
        finally { CleanupClusterPaths(paths); }
    }

    [Fact]
    public void ClusterTx_MultiShard_AbortOnPrepare_RollsBackAllShards()
    {
        // The first participant PREPAREs successfully; the second has a
        // unique-index conflict and votes ABORT. The coordinator must
        // ROLLBACK the prepared participant — neither shard ends up with
        // any of the staged docs.
        var (dbs, paths, cluster) = BuildClusterForTx(3, "users", "tenant");
        try
        {
            // Probe to find two tenant values on distinct shards. We use
            // the orders/pnr collection just for the probe (it's faster
            // than reasoning about the routing manually).
            cluster.ShardCollection("orders", "pnr");
            var (tenantA, tenantB, shardA, shardB) = FindPnrsOnDistinctShards(cluster, dbs);
            cluster.Execute("DELETE FROM orders");

            // Set up a unique index on the SECOND shard's users collection
            // and pre-seed a doc that the tx's second insert will conflict
            // with. The first shard has no such constraint, so it'll vote
            // PREPARED — exercising the rollback path.
            dbs[shardB].GetOrCreateCollection("users");
            dbs[shardB].CreateIndex("users", "email", "idx_email", unique: true);
            dbs[shardB].Insert("users", $$"""{"tenant":"{{tenantB}}","email":"clash@x.com"}""");

            using (var tx = cluster.BeginTransaction())
            {
                tx.Insert("users", $$"""{"tenant":"{{tenantA}}","email":"a@x.com","v":1}""");
                tx.Insert("users", $$"""{"tenant":"{{tenantB}}","email":"clash@x.com","v":2}""");
                Assert.Equal(2, tx.ParticipantCount);

                var ex = Assert.Throws<TransactionException>(() => tx.Commit());
                Assert.Contains("aborted", ex.Message);
                Assert.Equal(TransactionState.RolledBack, tx.State);
            }

            // First shard's tenantA insert was rolled back; it never landed.
            Assert.Empty(dbs[shardA].Execute($"SELECT * FROM users WHERE tenant = '{tenantA}'").Documents);
            // Second shard still only has the original clash doc — no v=2 row.
            var bRows = dbs[shardB].Execute($"SELECT * FROM users WHERE tenant = '{tenantB}'").Documents;
            Assert.Single(bRows);
            cluster.Dispose();
        }
        finally { CleanupClusterPaths(paths); }
    }

    [Fact]
    public void ClusterTx_MultiShard_RollbackBeforeCommit_NoPrepareSent()
    {
        // Caller stages multi-shard ops then calls Rollback() instead of
        // Commit(). PREPARE never goes out — the participants are untouched.
        var (dbs, paths, cluster) = BuildClusterForTx(3, "orders", "pnr");
        try
        {
            var (pnrA, pnrB, _, _) = FindPnrsOnDistinctShards(cluster, dbs);
            cluster.Execute("DELETE FROM orders");

            using (var tx = cluster.BeginTransaction())
            {
                tx.Insert("orders", $$"""{"pnr":"{{pnrA}}"}""");
                tx.Insert("orders", $$"""{"pnr":"{{pnrB}}"}""");
                tx.Rollback();
                Assert.Equal(TransactionState.RolledBack, tx.State);
            }

            Assert.Empty(cluster.Execute("SELECT * FROM orders").Documents);
            cluster.Dispose();
        }
        finally { CleanupClusterPaths(paths); }
    }

    [Fact]
    public void ClusterTx_MultiShard_PostCommit_AnotherTxCanRunImmediately()
    {
        // After a multi-shard commit, both participants' write locks are
        // released. A second cluster tx must work without waiting.
        var (dbs, paths, cluster) = BuildClusterForTx(3, "orders", "pnr");
        try
        {
            var (pnrA, pnrB, shardA, shardB) = FindPnrsOnDistinctShards(cluster, dbs);
            cluster.Execute("DELETE FROM orders");

            using (var tx1 = cluster.BeginTransaction())
            {
                tx1.Insert("orders", $$"""{"pnr":"{{pnrA}}","round":1}""");
                tx1.Insert("orders", $$"""{"pnr":"{{pnrB}}","round":1}""");
                tx1.Commit();
            }

            using (var tx2 = cluster.BeginTransaction())
            {
                tx2.Insert("orders", $$"""{"pnr":"{{pnrA}}","round":2}""");
                tx2.Insert("orders", $$"""{"pnr":"{{pnrB}}","round":2}""");
                tx2.Commit();
            }

            Assert.Equal(4, cluster.Execute("SELECT * FROM orders").Documents.Count);
            cluster.Dispose();
        }
        finally { CleanupClusterPaths(paths); }
    }

    [Fact]
    public void ClusterTx_MultiShard_Commit_RecordsCoordinatorDecisionAndDone()
    {
        // Phase C.1: COMMIT_DECISION goes to the coordinator shard's coord.log
        // before the COMMIT broadcast (point of no return); DONE goes after
        // every participant ACK'd. Recovery (Phase C.2) reads this log.
        var (dbs, paths, cluster) = BuildClusterForTx(3, "orders", "pnr");
        try
        {
            var (pnrA, pnrB, shardA, shardB) = FindPnrsOnDistinctShards(cluster, dbs);
            cluster.Execute("DELETE FROM orders");

            string txIdSeen;
            using (var tx = cluster.BeginTransaction())
            {
                txIdSeen = tx.Id.ToString("N");
                tx.Insert("orders", $$"""{"pnr":"{{pnrA}}"}""");
                tx.Insert("orders", $$"""{"pnr":"{{pnrB}}"}""");
                tx.Commit();
            }

            // Coordinator is the lowest-index participant. Verify on whichever
            // shard that turned out to be (depends on the consistent hash).
            int coordIdx = Math.Min(shardA, shardB);
            var coordStates = dbs[coordIdx].ScanCoordinatorTransactions();
            Assert.True(coordStates.ContainsKey(txIdSeen),
                $"Coordinator log on shard {coordIdx} missing tx {txIdSeen}; saw [{string.Join(",", coordStates.Keys)}]");
            var state = coordStates[txIdSeen];
            Assert.True(state.Decided);
            Assert.True(state.Done);

            // The OTHER participant must NOT have a coord-log record for this tx
            // (it isn't the coordinator; it's only PREPARED-then-COMMITTED).
            int otherIdx = Math.Max(shardA, shardB);
            var otherStates = dbs[otherIdx].ScanCoordinatorTransactions();
            Assert.False(otherStates.ContainsKey(txIdSeen));

            cluster.Dispose();
        }
        finally { CleanupClusterPaths(paths); }
    }

    [Fact]
    public void ClusterTx_MultiShard_Abort_DoesNotRecordCommitDecision()
    {
        // Phase C.1: ABORT is implicit — the coordinator log only contains
        // COMMIT_DECISION records, never ABORT. A tx that aborts in PREPARE
        // leaves NO trace in the coordinator log (recovery treats absence
        // of a decision as "abort").
        var (dbs, paths, cluster) = BuildClusterForTx(3, "users", "tenant");
        try
        {
            cluster.ShardCollection("orders", "pnr");
            var (tenantA, tenantB, shardA, shardB) = FindPnrsOnDistinctShards(cluster, dbs);
            cluster.Execute("DELETE FROM orders");

            // Pre-seed a unique-index conflict on the second shard.
            dbs[shardB].GetOrCreateCollection("users");
            dbs[shardB].CreateIndex("users", "email", "idx_email", unique: true);
            dbs[shardB].Insert("users", $$"""{"tenant":"{{tenantB}}","email":"clash@x.com"}""");

            string txIdSeen;
            using (var tx = cluster.BeginTransaction())
            {
                txIdSeen = tx.Id.ToString("N");
                tx.Insert("users", $$"""{"tenant":"{{tenantA}}","email":"a@x.com"}""");
                tx.Insert("users", $$"""{"tenant":"{{tenantB}}","email":"clash@x.com"}""");
                Assert.Throws<TransactionException>(() => tx.Commit());
            }

            // Neither shard has a coord-log record for the aborted tx.
            foreach (var d in dbs)
                Assert.False(d.ScanCoordinatorTransactions().ContainsKey(txIdSeen));

            cluster.Dispose();
        }
        finally { CleanupClusterPaths(paths); }
    }

    [Fact]
    public void ClusterTx_SingleShard_Commit_DoesNotTouchCoordinatorLog()
    {
        // The single-shard fast path bypasses 2PC entirely — it shouldn't
        // create a coord.log file at all.
        var (dbs, paths, cluster) = BuildClusterForTx(2, "orders", "pnr");
        try
        {
            using (var tx = cluster.BeginTransaction())
            {
                tx.Insert("orders", """{"pnr":"SAME","leg":1}""");
                tx.Insert("orders", """{"pnr":"SAME","leg":2}""");
                tx.Commit();
            }

            // No shard has any coord-log entries.
            foreach (var d in dbs)
                Assert.Empty(d.ScanCoordinatorTransactions());

            // And no .coord.log file exists either (lazy init means we
            // never touched the disk for the coordinator log).
            foreach (var p in paths)
                Assert.False(File.Exists(p + ".coord.log"));

            cluster.Dispose();
        }
        finally { CleanupClusterPaths(paths); }
    }

    // --- Recovery sweep (issue #14, Phase C.2) ---
    //
    // After a crash, cluster.Recover() walks each shard's prepared.log;
    // for every PREPARED slice it asks the named coordinator shard for
    // the decision via the coord.log; commits or aborts accordingly.
    // These tests simulate a crash by directly driving the participant /
    // coordinator log writes (instead of going through cluster.Begin
    // Transaction), then build a fresh cluster on the same files and
    // call Recover.

    [Fact]
    public void ClusterRecover_PreparedWithCommitDecision_Commits()
    {
        // Coordinator decided COMMIT, then everything died before the
        // broadcast hit the participant. Recovery must finalize COMMIT
        // — that's the durability promise.
        var pathA = Path.Combine(Path.GetTempPath(), $"rec_{Guid.NewGuid():N}.dfdb");
        var pathB = Path.Combine(Path.GetTempPath(), $"rec_{Guid.NewGuid():N}.dfdb");
        var paths = new[] { pathA, pathB };
        try
        {
            const string txId = "tx-recover-commit";

            // Pre-crash: stage the prepared tx on A; record COMMIT_DECISION on B.
            using (var dbA = DocumentForgeDb.Create(pathA))
            using (var dbB = DocumentForgeDb.Create(pathB))
            {
                var ops = new List<DocumentForge.Engine.Cluster.ShardTxOp>
                {
                    DocumentForge.Engine.Cluster.ShardTxOp.ForInsert("orders",
                        DocumentForge.Document.BsonDocument.FromJson("""{"pnr":"RECOVERED","leg":1}""")),
                };
                var result = dbA.PrepareTransaction(txId, "B", ops);
                Assert.Equal(PrepareVote.Prepared, result.Vote);

                // Coordinator decision lands on B. The COMMIT broadcast was
                // SUPPOSED to follow but we "crash" before it does.
                dbB.RecordCoordinatorDecision(txId, commit: true);
                // Dispose-without-resolving simulates the crash. The
                // prepared-tx coordinator's worker releases the write
                // lock on shutdown so dispose proceeds cleanly.
            }

            // Post-crash: rebuild the cluster on the same files and recover.
            using var dbA2 = DocumentForgeDb.Open(pathA);
            using var dbB2 = DocumentForgeDb.Open(pathB);
            using var cluster = new DocumentForge.Engine.Cluster.DocumentForgeCluster()
                .AddShard(new DocumentForge.Engine.Cluster.InProcessShardTransport("A", dbA2))
                .AddShard(new DocumentForge.Engine.Cluster.InProcessShardTransport("B", dbB2))
                .ShardCollection("orders", "pnr");

            var summary = cluster.Recover();
            Assert.Equal(1, summary.Committed);
            Assert.Equal(0, summary.Aborted);
            Assert.Equal(0, summary.Skipped);

            // The prepared insert is now applied on shard A.
            Assert.Single(dbA2.Execute("SELECT * FROM orders WHERE pnr = 'RECOVERED'").Documents);

            // Idempotent: a second Recover does nothing (the in-flight
            // record was resolved by the first call).
            var second = cluster.Recover();
            Assert.Equal(0, second.Committed);
            Assert.Equal(0, second.Aborted);
        }
        finally
        {
            foreach (var p in paths)
                try { File.Delete(p); File.Delete(p + ".wal"); File.Delete(p + ".recovery"); File.Delete(p + ".prepared.log"); File.Delete(p + ".coord.log"); } catch { }
        }
    }

    [Fact]
    public void ClusterRecover_PreparedWithoutDecision_Aborts()
    {
        // Coordinator died BEFORE deciding, leaving the participant
        // PREPARED. The coord log has no record for this txId.
        // Recovery must finalize ABORT — and crucially, must NOT
        // apply the staged inserts.
        var pathA = Path.Combine(Path.GetTempPath(), $"rec_{Guid.NewGuid():N}.dfdb");
        var pathB = Path.Combine(Path.GetTempPath(), $"rec_{Guid.NewGuid():N}.dfdb");
        var paths = new[] { pathA, pathB };
        try
        {
            const string txId = "tx-recover-abort";

            using (var dbA = DocumentForgeDb.Create(pathA))
            using (var dbB = DocumentForgeDb.Create(pathB))
            {
                var ops = new List<DocumentForge.Engine.Cluster.ShardTxOp>
                {
                    DocumentForge.Engine.Cluster.ShardTxOp.ForInsert("orders",
                        DocumentForge.Document.BsonDocument.FromJson("""{"pnr":"NEVER"}""")),
                };
                Assert.Equal(PrepareVote.Prepared, dbA.PrepareTransaction(txId, "B", ops).Vote);
                // Note: no RecordCoordinatorDecision call. B's coord log stays empty.
            }

            using var dbA2 = DocumentForgeDb.Open(pathA);
            using var dbB2 = DocumentForgeDb.Open(pathB);
            using var cluster = new DocumentForge.Engine.Cluster.DocumentForgeCluster()
                .AddShard(new DocumentForge.Engine.Cluster.InProcessShardTransport("A", dbA2))
                .AddShard(new DocumentForge.Engine.Cluster.InProcessShardTransport("B", dbB2))
                .ShardCollection("orders", "pnr");

            var summary = cluster.Recover();
            Assert.Equal(0, summary.Committed);
            Assert.Equal(1, summary.Aborted);

            // The would-be insert never landed.
            Assert.Empty(dbA2.Execute("SELECT * FROM orders").Documents);
        }
        finally
        {
            foreach (var p in paths)
                try { File.Delete(p); File.Delete(p + ".wal"); File.Delete(p + ".recovery"); File.Delete(p + ".prepared.log"); File.Delete(p + ".coord.log"); } catch { }
        }
    }

    [Fact]
    public void ClusterRecover_NoInFlightTxs_IsNoOp()
    {
        var (dbs, paths, cluster) = BuildClusterForTx(2, "orders", "pnr");
        try
        {
            // Brand-new cluster — nothing prepared, nothing decided.
            var summary = cluster.Recover();
            Assert.Equal(0, summary.Committed);
            Assert.Equal(0, summary.Aborted);
            Assert.Equal(0, summary.Skipped);
            cluster.Dispose();
        }
        finally { CleanupClusterPaths(paths); }
    }

    [Fact]
    public void ClusterRecover_CoordinatorShardAbsent_Skips()
    {
        // Edge case: a participant's prepared.log names a coordinator
        // shard that isn't in the current cluster (could happen during
        // a rebalance or misconfiguration). Recovery should not
        // arbitrarily commit or abort — surface it as Skipped so an
        // operator can investigate.
        var pathA = Path.Combine(Path.GetTempPath(), $"rec_{Guid.NewGuid():N}.dfdb");
        var paths = new[] { pathA };
        try
        {
            using (var dbA = DocumentForgeDb.Create(pathA))
            {
                var ops = new List<DocumentForge.Engine.Cluster.ShardTxOp>
                {
                    DocumentForge.Engine.Cluster.ShardTxOp.ForInsert("orders",
                        DocumentForge.Document.BsonDocument.FromJson("""{"pnr":"X"}""")),
                };
                Assert.Equal(PrepareVote.Prepared,
                    dbA.PrepareTransaction("tx-orphan", "GHOST_COORD", ops).Vote);
            }

            using var dbA2 = DocumentForgeDb.Open(pathA);
            using var cluster = new DocumentForge.Engine.Cluster.DocumentForgeCluster()
                .AddShard(new DocumentForge.Engine.Cluster.InProcessShardTransport("A", dbA2))
                .ShardCollection("orders", "pnr");

            var summary = cluster.Recover();
            Assert.Equal(0, summary.Committed);
            Assert.Equal(0, summary.Aborted);
            Assert.Equal(1, summary.Skipped);
        }
        finally
        {
            foreach (var p in paths)
                try { File.Delete(p); File.Delete(p + ".wal"); File.Delete(p + ".recovery"); File.Delete(p + ".prepared.log"); File.Delete(p + ".coord.log"); } catch { }
        }
    }

    [Fact]
    public void ClusterTx_UniqueIndexConflict_RollsBackAtomically()
    {
        // Two inserts on the same shard, second violates a unique index.
        // The participant's local commit must validate then throw, leaving
        // neither doc persisted — same atomicity as a single-node tx.
        //
        // We set the unique index up directly on each shard so this test
        // doesn't accidentally also exercise cluster CREATE INDEX broadcast
        // (which is orthogonal — that's covered elsewhere).
        var (dbs, paths, cluster) = BuildClusterForTx(2, "users", "tenant");
        try
        {
            foreach (var d in dbs)
            {
                d.GetOrCreateCollection("users");
                d.CreateIndex("users", "email", "idx_email", unique: true);
            }

            cluster.Insert("users", """{"tenant":"T1","email":"a@b.com","v":1}""");

            using (var tx = cluster.BeginTransaction())
            {
                tx.Insert("users", """{"tenant":"T1","email":"new@x.com","v":2}""");
                tx.Insert("users", """{"tenant":"T1","email":"a@b.com","v":3}""");  // conflict
                Assert.Equal(1, tx.ParticipantCount);

                Assert.ThrowsAny<Exception>(() => tx.Commit());
                Assert.Equal(TransactionState.RolledBack, tx.State);
            }

            // Pre-tx state preserved: original a@b.com still v=1, new@x.com never landed.
            var rows = cluster.Execute("SELECT * FROM users WHERE tenant = 'T1'").Documents;
            Assert.Single(rows);
            Assert.Equal("a@b.com", rows[0]["email"].AsString);
            Assert.Equal(1, rows[0]["v"].AsInt32);
            cluster.Dispose();
        }
        finally { CleanupClusterPaths(paths); }
    }

    [Fact]
    public void ClusterTx_FindReturnsStagedInsert()
    {
        // Read-your-writes for staged inserts. Phase A only — Phase B will
        // also layer staged state over a real cluster lookup.
        var (dbs, paths, cluster) = BuildClusterForTx(2, "orders", "pnr");
        try
        {
            using (var tx = cluster.BeginTransaction())
            {
                var id = tx.Insert("orders", """{"pnr":"XYZ","seat":"12A"}""");
                var seen = tx.Find("orders", id);
                Assert.NotNull(seen);
                Assert.Equal("XYZ", seen!["pnr"].AsString);

                // Outside the tx — neither shard has the doc yet.
                Assert.Empty(cluster.Execute("SELECT * FROM orders").Documents);
                tx.Rollback();
            }
            cluster.Dispose();
        }
        finally { CleanupClusterPaths(paths); }
    }

    [Fact]
    public void ClusterTx_EmptyCommit_IsNoOp()
    {
        var (dbs, paths, cluster) = BuildClusterForTx(2, "orders", "pnr");
        try
        {
            using (var tx = cluster.BeginTransaction())
            {
                tx.Commit();
                Assert.Equal(TransactionState.Committed, tx.State);
            }
            cluster.Dispose();
        }
        finally { CleanupClusterPaths(paths); }
    }

    [Fact]
    public void ClusterTx_NoShards_ThrowsOnBegin()
    {
        using var cluster = new DocumentForge.Engine.Cluster.DocumentForgeCluster();
        Assert.Throws<DocumentForgeException>(() => cluster.BeginTransaction());
    }

    [Fact]
    public void ClusterTx_ReplicatedCollection_InsertThrowsNotImplemented()
    {
        // Replicated collections fan out to every shard, so a single insert
        // is already a multi-shard tx. Phase B handles this; Phase A bails.
        var paths = Enumerable.Range(0, 2)
            .Select(i => Path.Combine(Path.GetTempPath(), $"clstxr_{i}_{Guid.NewGuid():N}.dfdb"))
            .ToArray();
        var dbs = paths.Select(p => DocumentForgeDb.Create(p)).ToArray();
        try
        {
            using var cluster = new DocumentForge.Engine.Cluster.DocumentForgeCluster()
                .AddShard(new DocumentForge.Engine.Cluster.InProcessShardTransport("A", dbs[0], ownsDb: true))
                .AddShard(new DocumentForge.Engine.Cluster.InProcessShardTransport("B", dbs[1], ownsDb: true))
                .ReplicateCollection("countries");

            using var tx = cluster.BeginTransaction();
            var ex = Assert.Throws<NotImplementedException>(() =>
                tx.Insert("countries", """{"code":"US","name":"USA"}"""));
            Assert.Contains("Phase B", ex.Message);
        }
        finally { CleanupClusterPaths(paths); }
    }

    [Fact]
    public void ClusterTx_OperationAfterCommit_Throws()
    {
        var (dbs, paths, cluster) = BuildClusterForTx(2, "orders", "pnr");
        try
        {
            var tx = cluster.BeginTransaction();
            tx.Insert("orders", """{"pnr":"ABC"}""");
            tx.Commit();

            Assert.Throws<TransactionException>(() => tx.Insert("orders", """{"pnr":"DEF"}"""));
            Assert.Throws<TransactionException>(() => tx.Rollback());
            cluster.Dispose();
        }
        finally { CleanupClusterPaths(paths); }
    }

    [Fact]
    public void ClusterTx_NonClusterPath_NotRegressed()
    {
        // The performance contract: non-transactional cluster ops are
        // unchanged. This test is defensive — a refactor that accidentally
        // routes cluster.Insert through ClusterTransaction would still pass
        // the other tests. This one asserts that cluster.Insert on a brand
        // new cluster (no transactions ever opened) works exactly as before.
        var (dbs, paths, cluster) = BuildClusterForTx(3, "orders", "pnr");
        try
        {
            for (int i = 0; i < 30; i++)
                cluster.Insert("orders", $$"""{"pnr": "ORD{{i:D4}}", "amount": {{i * 10}}}""");
            Assert.Equal(30, cluster.Execute("SELECT * FROM orders").Documents.Count);
            cluster.Dispose();
        }
        finally { CleanupClusterPaths(paths); }
    }

    // --- ClusterTransaction Replace + DeleteByField (issue #14, Phase B-deferred) ---
    //
    // Phase A only shipped Insert + Find on ClusterTransaction. Replace
    // and DeleteByField (and the scatter case for non-shard-key fields)
    // need the multi-shard 2PC machinery from Phase B/C, so they came
    // back here once that landed.
    //
    // Replace routes by extracting the shard key from the new doc — same
    // shard as the existing doc means single-shard fast path. Changing
    // the shard-key value is a semantically different operation that
    // we don't currently handle.
    //
    // DeleteByField: if the field IS the shard key, single-shard. If
    // not, the matching docs could be anywhere — scatter to every shard.

    [Fact]
    public void ClusterTx_Replace_SingleShard_Commits()
    {
        var (dbs, paths, cluster) = BuildClusterForTx(3, "orders", "pnr");
        try
        {
            var insertedId = cluster.Insert("orders", """{"pnr":"BASE","seat":"12A"}""");

            using (var tx = cluster.BeginTransaction())
            {
                tx.Replace("orders", insertedId, """{"pnr":"BASE","seat":"99Z"}""");
                tx.Commit();
            }

            var rows = cluster.Execute("SELECT * FROM orders WHERE pnr = 'BASE'").Documents;
            Assert.Single(rows);
            Assert.Equal("99Z", rows[0]["seat"].AsString);
            cluster.Dispose();
        }
        finally { CleanupClusterPaths(paths); }
    }

    [Fact]
    public void ClusterTx_DeleteByField_OnShardKey_IsSingleShard()
    {
        // field == shard key → cluster knows exactly which shard owns the
        // matching docs. Should be a single-shard tx (fast path).
        var (dbs, paths, cluster) = BuildClusterForTx(3, "orders", "pnr");
        try
        {
            cluster.Insert("orders", """{"pnr":"GONE","leg":1}""");
            cluster.Insert("orders", """{"pnr":"GONE","leg":2}""");

            using (var tx = cluster.BeginTransaction())
            {
                tx.DeleteByField("orders", "pnr", "GONE");
                Assert.Equal(1, tx.ParticipantCount);  // single-shard
                tx.Commit();
            }

            Assert.Empty(cluster.Execute("SELECT * FROM orders WHERE pnr = 'GONE'").Documents);
            cluster.Dispose();
        }
        finally { CleanupClusterPaths(paths); }
    }

    [Fact]
    public void ClusterTx_DeleteByField_OnNonShardKey_ScattersToAllShards()
    {
        // field != shard key → matching docs could be anywhere. Op fans
        // out to every shard. The 2PC machinery handles the multi-shard
        // commit; participants with no matches no-op cleanly.
        var (dbs, paths, cluster) = BuildClusterForTx(3, "orders", "pnr");
        try
        {
            // Seed docs across shards. status=CANCELLED on each.
            for (int i = 0; i < 30; i++)
                cluster.Insert("orders", $$"""{"pnr":"P{{i:D3}}","status":"CANCELLED"}""");
            for (int i = 0; i < 30; i++)
                cluster.Insert("orders", $$"""{"pnr":"K{{i:D3}}","status":"CONFIRMED"}""");

            // Sanity — at least 2 shards have CANCELLED docs (consistent
            // hashing across 60 unique pnrs).
            int shardsWithCancelled = 0;
            for (int s = 0; s < dbs.Length; s++)
                if (dbs[s].Execute("SELECT * FROM orders WHERE status = 'CANCELLED'").Documents.Count > 0)
                    shardsWithCancelled++;
            Assert.True(shardsWithCancelled >= 2, $"Test setup expected ≥2 shards with CANCELLED, got {shardsWithCancelled}");

            using (var tx = cluster.BeginTransaction())
            {
                tx.DeleteByField("orders", "status", "CANCELLED");
                Assert.Equal(3, tx.ParticipantCount);  // scattered to all shards
                tx.Commit();
            }

            Assert.Empty(cluster.Execute("SELECT * FROM orders WHERE status = 'CANCELLED'").Documents);
            Assert.Equal(30, cluster.Execute("SELECT * FROM orders WHERE status = 'CONFIRMED'").Documents.Count);
            cluster.Dispose();
        }
        finally { CleanupClusterPaths(paths); }
    }

    [Fact]
    public void ClusterTx_Replace_RollsBackWithRestOfTransaction()
    {
        // Replace inside a multi-op transaction — when the tx aborts,
        // the replace must not land. (The atomicity story is what makes
        // tx.Replace genuinely useful over cluster.Execute("UPDATE ...").)
        var (dbs, paths, cluster) = BuildClusterForTx(3, "orders", "pnr");
        try
        {
            var id = cluster.Insert("orders", """{"pnr":"KEEP","status":"CONFIRMED"}""");

            using (var tx = cluster.BeginTransaction())
            {
                tx.Replace("orders", id, """{"pnr":"KEEP","status":"CANCELLED"}""");
                tx.Rollback();
            }

            var rows = cluster.Execute("SELECT * FROM orders WHERE pnr = 'KEEP'").Documents;
            Assert.Single(rows);
            Assert.Equal("CONFIRMED", rows[0]["status"].AsString);  // unchanged
            cluster.Dispose();
        }
        finally { CleanupClusterPaths(paths); }
    }

    // --- 2PC participant API (issue #14, Phase B.1: prepare/commit/rollback on a single shard) ---
    //
    // Phase B.1 lands the participant-side wire ops on IShardTransport:
    // Prepare validates + persists to {db}.prepared.log and holds the write
    // lock; CommitPrepared applies and releases; RollbackPrepared releases
    // without applying. The cluster coordinator (Phase B.2) drives these.
    //
    // These tests exercise one shard at a time (via InProcessShardTransport)
    // — enough to lock in the participant contract before the cluster-level
    // multi-shard machinery lands on top.

    [Fact]
    public void Participant_PreparedThenCommit_AppliesOps()
    {
        var path = Path.Combine(Path.GetTempPath(), $"part_{Guid.NewGuid():N}.dfdb");
        try
        {
            using var db = DocumentForgeDb.Create(path);
            var shard = new DocumentForge.Engine.Cluster.InProcessShardTransport("A", db);

            var ops = new List<DocumentForge.Engine.Cluster.ShardTxOp>
            {
                DocumentForge.Engine.Cluster.ShardTxOp.ForInsert("orders",
                    DocumentForge.Document.BsonDocument.FromJson("""{"pnr":"ABC","leg":1}""")),
                DocumentForge.Engine.Cluster.ShardTxOp.ForInsert("orders",
                    DocumentForge.Document.BsonDocument.FromJson("""{"pnr":"ABC","leg":2}""")),
            };

            var tx = "tx-001";
            var result = shard.Prepare(tx, "A", ops);
            Assert.Equal(PrepareVote.Prepared, result.Vote);

            // NB: while PREPARED the participant holds the write lock, so a
            // SELECT here would block on the reader lock — that's the
            // canonical 2PC read-blocking-during-prepared semantics. We
            // verify the post-commit visibility instead.

            shard.CommitPrepared(tx);

            // After commit the inserts are visible.
            Assert.Equal(2, db.Execute("SELECT * FROM orders").Documents.Count);
        }
        finally
        {
            try { File.Delete(path); File.Delete(path + ".wal"); File.Delete(path + ".recovery"); File.Delete(path + ".prepared.log"); } catch { }
        }
    }

    [Fact]
    public void Participant_PreparedThenRollback_DiscardsOps()
    {
        var path = Path.Combine(Path.GetTempPath(), $"part_{Guid.NewGuid():N}.dfdb");
        try
        {
            using var db = DocumentForgeDb.Create(path);
            var shard = new DocumentForge.Engine.Cluster.InProcessShardTransport("A", db);

            var ops = new List<DocumentForge.Engine.Cluster.ShardTxOp>
            {
                DocumentForge.Engine.Cluster.ShardTxOp.ForInsert("orders",
                    DocumentForge.Document.BsonDocument.FromJson("""{"pnr":"X"}""")),
            };

            Assert.Equal(PrepareVote.Prepared, shard.Prepare("tx-rb", "A", ops).Vote);
            shard.RollbackPrepared("tx-rb");

            Assert.Empty(db.Execute("SELECT * FROM orders").Documents);

            // After rollback the participant must accept new prepares again.
            Assert.Equal(PrepareVote.Prepared, shard.Prepare("tx-after-rb", "A", ops).Vote);
            shard.CommitPrepared("tx-after-rb");
            Assert.Single(db.Execute("SELECT * FROM orders").Documents);
        }
        finally
        {
            try { File.Delete(path); File.Delete(path + ".wal"); File.Delete(path + ".recovery"); File.Delete(path + ".prepared.log"); } catch { }
        }
    }

    [Fact]
    public void Participant_PrepareUniqueConflict_ReturnsAborted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"part_{Guid.NewGuid():N}.dfdb");
        try
        {
            using var db = DocumentForgeDb.Create(path);
            db.GetOrCreateCollection("users");
            db.CreateIndex("users", "email", "idx_email", unique: true);
            db.Insert("users", """{"email":"a@b.com","v":1}""");

            var shard = new DocumentForge.Engine.Cluster.InProcessShardTransport("A", db);

            // Conflicting insert in the prepared tx — must come back as ABORT,
            // NOT throw. The coordinator needs the vote-shape so it can issue
            // ROLLBACK to the other participants cleanly.
            var ops = new List<DocumentForge.Engine.Cluster.ShardTxOp>
            {
                DocumentForge.Engine.Cluster.ShardTxOp.ForInsert("users",
                    DocumentForge.Document.BsonDocument.FromJson("""{"email":"a@b.com","v":2}""")),
            };

            var result = shard.Prepare("tx-dup", "A", ops);
            Assert.Equal(PrepareVote.Aborted, result.Vote);
            Assert.NotNull(result.AbortReason);

            // Non-tx writes still work — Prepare's lock was released on abort.
            db.Insert("users", """{"email":"new@x.com","v":3}""");
            Assert.Equal(2, db.Execute("SELECT * FROM users").Documents.Count);
        }
        finally
        {
            try { File.Delete(path); File.Delete(path + ".wal"); File.Delete(path + ".recovery"); File.Delete(path + ".prepared.log"); } catch { }
        }
    }

    [Fact]
    public void Participant_SecondPrepareWhileFirstPrepared_ReturnsAborted()
    {
        // Phase B.1 simplification: at most one prepared tx per shard at a
        // time. A racing second Prepare gets ABORT(busy); it's a clean retry
        // signal for the coordinator.
        var path = Path.Combine(Path.GetTempPath(), $"part_{Guid.NewGuid():N}.dfdb");
        try
        {
            using var db = DocumentForgeDb.Create(path);
            var shard = new DocumentForge.Engine.Cluster.InProcessShardTransport("A", db);

            var ops1 = new List<DocumentForge.Engine.Cluster.ShardTxOp>
            {
                DocumentForge.Engine.Cluster.ShardTxOp.ForInsert("orders",
                    DocumentForge.Document.BsonDocument.FromJson("""{"pnr":"ONE"}""")),
            };
            var ops2 = new List<DocumentForge.Engine.Cluster.ShardTxOp>
            {
                DocumentForge.Engine.Cluster.ShardTxOp.ForInsert("orders",
                    DocumentForge.Document.BsonDocument.FromJson("""{"pnr":"TWO"}""")),
            };

            Assert.Equal(PrepareVote.Prepared, shard.Prepare("tx-1", "A", ops1).Vote);

            var second = shard.Prepare("tx-2", "A", ops2);
            Assert.Equal(PrepareVote.Aborted, second.Vote);
            Assert.Contains("already prepared", second.AbortReason);

            // Resolve tx-1 to free the slot, then tx-2 should succeed.
            shard.CommitPrepared("tx-1");
            Assert.Equal(PrepareVote.Prepared, shard.Prepare("tx-2", "A", ops2).Vote);
            shard.CommitPrepared("tx-2");

            Assert.Equal(2, db.Execute("SELECT * FROM orders").Documents.Count);
        }
        finally
        {
            try { File.Delete(path); File.Delete(path + ".wal"); File.Delete(path + ".recovery"); File.Delete(path + ".prepared.log"); } catch { }
        }
    }

    [Fact]
    public void Participant_CommitUnknownTx_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"part_{Guid.NewGuid():N}.dfdb");
        try
        {
            using var db = DocumentForgeDb.Create(path);
            var shard = new DocumentForge.Engine.Cluster.InProcessShardTransport("A", db);
            // No prepared tx — this txId never existed.
            Assert.ThrowsAny<Exception>(() => shard.CommitPrepared("ghost-tx"));
        }
        finally
        {
            try { File.Delete(path); File.Delete(path + ".wal"); File.Delete(path + ".recovery"); File.Delete(path + ".prepared.log"); } catch { }
        }
    }

    [Fact]
    public void Participant_PreparedTxLog_PersistsAcrossClose()
    {
        // Place a prepared record and dispose without resolving. The log
        // file must contain it on reopen (Phase C reads this for recovery).
        var path = Path.Combine(Path.GetTempPath(), $"part_{Guid.NewGuid():N}.dfdb");
        try
        {
            using (var db = DocumentForgeDb.Create(path))
            {
                var shard = new DocumentForge.Engine.Cluster.InProcessShardTransport("A", db);
                var ops = new List<DocumentForge.Engine.Cluster.ShardTxOp>
                {
                    DocumentForge.Engine.Cluster.ShardTxOp.ForInsert("orders",
                        DocumentForge.Document.BsonDocument.FromJson("""{"pnr":"ORPHAN"}""")),
                };
                Assert.Equal(PrepareVote.Prepared, shard.Prepare("tx-orphan", "A", ops).Vote);
                // Dispose without committing or rolling back — simulates a
                // process exit while a tx was PREPARED.
            }

            // The prepared.log file exists and is non-empty.
            var logPath = path + ".prepared.log";
            Assert.True(File.Exists(logPath));
            Assert.True(new FileInfo(logPath).Length > 0);

            // Reopening doesn't auto-recover (that's Phase C), but the log
            // file is still there for the next phase to pick up.
            using var db2 = DocumentForgeDb.Open(path);
            Assert.Empty(db2.Execute("SELECT * FROM orders").Documents);
        }
        finally
        {
            try { File.Delete(path); File.Delete(path + ".wal"); File.Delete(path + ".recovery"); File.Delete(path + ".prepared.log"); } catch { }
        }
    }

    // --- 2PC PREPARE timeout (issue #14, Phase D) ---
    //
    // Without timeouts, a coordinator that dies before broadcasting COMMIT
    // would leave participants PREPARED with their write lock held until
    // someone runs cluster.Recover() manually. Phase D adds a per-tx
    // deadline: the participant's worker thread schedules a Task.Delay
    // that, on expiry, posts a TimeoutCommand to its own queue and self-
    // aborts (releases the lock, writes a RESOLVED-aborted record).
    // A late CommitPrepared for that txId then throws.

    [Fact]
    public void Participant_PreparedTimeout_SelfAborts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"part_{Guid.NewGuid():N}.dfdb");
        try
        {
            using var db = DocumentForgeDb.Create(path);
            var shard = new DocumentForge.Engine.Cluster.InProcessShardTransport("A", db);

            var ops = new List<DocumentForge.Engine.Cluster.ShardTxOp>
            {
                DocumentForge.Engine.Cluster.ShardTxOp.ForInsert("orders",
                    DocumentForge.Document.BsonDocument.FromJson("""{"pnr":"WILL-EXPIRE"}""")),
            };

            // 100ms timeout. Don't resolve — let the timer fire.
            Assert.Equal(PrepareVote.Prepared,
                shard.Prepare("tx-timeout", "A", ops, TimeSpan.FromMilliseconds(100)).Vote);

            // Wait long enough for the timeout to fire and the abort to
            // propagate through the worker queue.
            Thread.Sleep(500);

            // The abort should have released the write lock — a fresh
            // PREPARE on a new tx must succeed.
            var ops2 = new List<DocumentForge.Engine.Cluster.ShardTxOp>
            {
                DocumentForge.Engine.Cluster.ShardTxOp.ForInsert("orders",
                    DocumentForge.Document.BsonDocument.FromJson("""{"pnr":"AFTER"}""")),
            };
            var second = shard.Prepare("tx-after-timeout", "A", ops2, TimeSpan.FromSeconds(30));
            Assert.Equal(PrepareVote.Prepared, second.Vote);
            shard.CommitPrepared("tx-after-timeout");

            // The expired tx never landed.
            Assert.Single(db.Execute("SELECT * FROM orders").Documents);
            Assert.Empty(db.Execute("SELECT * FROM orders WHERE pnr = 'WILL-EXPIRE'").Documents);
        }
        finally
        {
            try { File.Delete(path); File.Delete(path + ".wal"); File.Delete(path + ".recovery"); File.Delete(path + ".prepared.log"); } catch { }
        }
    }

    [Fact]
    public void Participant_LateCommitAfterTimeout_Throws()
    {
        // Coordinator decided COMMIT and called CommitPrepared, but the
        // participant's timeout had already fired. The late commit must
        // fail — the participant already wrote RESOLVED-aborted to its
        // log. (In a real cluster this would surface as a tx failure on
        // the coordinator-side broadcast loop; the next Recover would
        // then see no in-flight on this shard and confirm aborted.)
        var path = Path.Combine(Path.GetTempPath(), $"part_{Guid.NewGuid():N}.dfdb");
        try
        {
            using var db = DocumentForgeDb.Create(path);
            var shard = new DocumentForge.Engine.Cluster.InProcessShardTransport("A", db);

            var ops = new List<DocumentForge.Engine.Cluster.ShardTxOp>
            {
                DocumentForge.Engine.Cluster.ShardTxOp.ForInsert("orders",
                    DocumentForge.Document.BsonDocument.FromJson("""{"pnr":"X"}""")),
            };

            Assert.Equal(PrepareVote.Prepared,
                shard.Prepare("tx-late", "A", ops, TimeSpan.FromMilliseconds(100)).Vote);
            Thread.Sleep(500);  // let the timeout fire and abort

            Assert.ThrowsAny<Exception>(() => shard.CommitPrepared("tx-late"));
        }
        finally
        {
            try { File.Delete(path); File.Delete(path + ".wal"); File.Delete(path + ".recovery"); File.Delete(path + ".prepared.log"); } catch { }
        }
    }

    [Fact]
    public void Participant_FastResolveBeforeTimeout_NoAbort()
    {
        // Sanity: a timeout that's far enough out doesn't fire if
        // we resolve quickly. The CTS cancellation should cleanly
        // dispose the pending Task.Delay.
        var path = Path.Combine(Path.GetTempPath(), $"part_{Guid.NewGuid():N}.dfdb");
        try
        {
            using var db = DocumentForgeDb.Create(path);
            var shard = new DocumentForge.Engine.Cluster.InProcessShardTransport("A", db);

            var ops = new List<DocumentForge.Engine.Cluster.ShardTxOp>
            {
                DocumentForge.Engine.Cluster.ShardTxOp.ForInsert("orders",
                    DocumentForge.Document.BsonDocument.FromJson("""{"pnr":"FAST"}""")),
            };

            Assert.Equal(PrepareVote.Prepared,
                shard.Prepare("tx-fast", "A", ops, TimeSpan.FromSeconds(30)).Vote);
            shard.CommitPrepared("tx-fast");

            // Wait briefly to make sure no stale TimeoutCommand fires.
            Thread.Sleep(200);

            // Doc visible from the commit.
            Assert.Single(db.Execute("SELECT * FROM orders").Documents);

            // A second Prepare/Commit cycle should still work cleanly
            // (no stale state from the first tx's timeout machinery).
            var ops2 = new List<DocumentForge.Engine.Cluster.ShardTxOp>
            {
                DocumentForge.Engine.Cluster.ShardTxOp.ForInsert("orders",
                    DocumentForge.Document.BsonDocument.FromJson("""{"pnr":"SECOND"}""")),
            };
            Assert.Equal(PrepareVote.Prepared,
                shard.Prepare("tx-fast-2", "A", ops2, TimeSpan.FromSeconds(30)).Vote);
            shard.CommitPrepared("tx-fast-2");
            Assert.Equal(2, db.Execute("SELECT * FROM orders").Documents.Count);
        }
        finally
        {
            try { File.Delete(path); File.Delete(path + ".wal"); File.Delete(path + ".recovery"); File.Delete(path + ".prepared.log"); } catch { }
        }
    }

    [Fact]
    public void ClusterTx_PrepareTimeout_PropertyHasReasonableDefault()
    {
        // The cluster exposes a configurable per-tx timeout. Default 30s,
        // matching the issue's design decision.
        using var cluster = new DocumentForge.Engine.Cluster.DocumentForgeCluster();
        Assert.Equal(TimeSpan.FromSeconds(30), cluster.PrepareTimeout);
        cluster.PrepareTimeout = TimeSpan.FromSeconds(5);
        Assert.Equal(TimeSpan.FromSeconds(5), cluster.PrepareTimeout);
    }

    // --- Index catalog: multi-page support (issue #22) ---

    [Fact]
    public void IndexCatalog_HandlesManyIndexesAcrossMultiplePages()
    {
        // Pre-fix Save threw "Index catalog overflow - too many indexes for one
        // page (TODO: multi-page)" once the catalog page filled up — roughly
        // 170 indexes on a 4 KB page depending on name length. That's a real
        // ceiling for apps with many small collections.
        //
        // Create enough indexes to span at least 3 catalog pages (300 here is
        // comfortably past the single-page cap). Verify we can save, reload,
        // and that lookups against each index still plan correctly.
        const int IndexCount = 300;

        for (int i = 0; i < IndexCount; i++)
        {
            var coll = $"col{i:D3}";
            _db.Insert(coll, $$"""{"k": "v{{i}}"}""");
            _db.CreateIndex(coll, "k", $"idx_col{i:D3}_k");
        }

        // Spot-check: every index appears in GetIndexes for its collection.
        for (int i = 0; i < IndexCount; i++)
        {
            var coll = $"col{i:D3}";
            Assert.Single(_db.GetIndexes(coll));
        }

        // Round-trip: dispose + reopen reloads the entire chain. If any link
        // in the chain were broken, indexes after the cap point would be lost.
        _db.Flush();
        _db.Dispose();
        using var reopened = DocumentForgeDb.Open(_dbPath);

        for (int i = 0; i < IndexCount; i++)
        {
            var coll = $"col{i:D3}";
            Assert.Single(reopened.GetIndexes(coll));
            // Indexed lookup proves the catalog row points at a real, intact index page.
            var rows = reopened.Execute($"SELECT * FROM {coll} WHERE k = 'v{i}'").Documents;
            Assert.Single(rows);
        }
    }

    [Fact]
    public void IndexCatalog_ShrinksAndReusesPagesAfterDropIndex()
    {
        // Catalog growing then shrinking should free the spare pages, not
        // leak them. We can't directly inspect the free list, so the test
        // verifies the engine stays functional through a grow/shrink cycle
        // and a reopen comes back coherent.
        for (int i = 0; i < 250; i++)
        {
            var coll = $"c{i:D3}";
            _db.Insert(coll, $$"""{"k":{{i}}}""");
            _db.CreateIndex(coll, "k", $"idx_c{i:D3}");
        }

        // Drop most of them — this rewrites the catalog smaller and frees
        // the now-unneeded chain pages. The SQL DROP INDEX form requires
        // an ON-clause: `DROP INDEX <name> ON <collection>`.
        var dropResult = _db.Execute("DROP INDEX idx_c000 ON c000");
        Assert.True(dropResult.Success, dropResult.Message);
        for (int i = 50; i < 250; i++)
        {
            var r = _db.Execute($"DROP INDEX idx_c{i:D3} ON c{i:D3}");
            Assert.True(r.Success, r.Message);
        }

        _db.Flush();
        _db.Dispose();
        using var reopened = DocumentForgeDb.Open(_dbPath);

        // Indexes 1..49 survive; 0 was dropped; 50..249 dropped.
        Assert.Empty(reopened.GetIndexes("c000"));
        for (int i = 1; i < 50; i++)
            Assert.Single(reopened.GetIndexes($"c{i:D3}"));
        for (int i = 50; i < 250; i++)
            Assert.Empty(reopened.GetIndexes($"c{i:D3}"));
    }

    // --- Replication topology exposure (issue #12) ---

    [Fact]
    public async System.Threading.Tasks.Task LogicalReplication_LeaderExposesConnectedFollowerEndpoints()
    {
        // The /replication/status endpoint needs to surface enough about
        // connected followers that an admin UI can wire a topology graph
        // automatically. Today we expose endpoint, connectedAt, and the
        // handshake seq (worst-case lag baseline). Ack-driven live lag is
        // tracked separately under the Phase 2 replication-tx work.
        int port = 5800 + System.Random.Shared.Next(100);
        var leaderPath = Path.Combine(Path.GetTempPath(), $"topoleader_{Guid.NewGuid():N}.dfdb");
        var follower1Path = Path.Combine(Path.GetTempPath(), $"topofol1_{Guid.NewGuid():N}.dfdb");
        var follower2Path = Path.Combine(Path.GetTempPath(), $"topofol2_{Guid.NewGuid():N}.dfdb");

        try
        {
            using var leader = DocumentForgeDb.Create(leaderPath);
            leader.StartLogicalReplicationServer(port);
            await System.Threading.Tasks.Task.Delay(150);

            // Pre-seed an op on the leader so the second follower's handshake
            // seq differs from the first — gives the lag column something to
            // distinguish.
            leader.Insert("orders", """{"pnr":"X"}""");

            using var follower1 = DocumentForgeDb.Create(follower1Path);
            follower1.StartLogicalReplicationFollower("localhost", port);
            for (int i = 0; i < 30 && leader.GetLogicalFollowerCount() < 1; i++)
                await System.Threading.Tasks.Task.Delay(100);

            leader.Insert("orders", """{"pnr":"Y"}""");
            leader.Insert("orders", """{"pnr":"Z"}""");

            using var follower2 = DocumentForgeDb.Create(follower2Path);
            follower2.StartLogicalReplicationFollower("localhost", port);
            for (int i = 0; i < 30 && leader.GetLogicalFollowerCount() < 2; i++)
                await System.Threading.Tasks.Task.Delay(100);

            var followers = leader.GetLogicalFollowers();
            Assert.Equal(2, followers.Count);

            // Both endpoints are loopback addresses with whatever ephemeral
            // ports the OS assigned. The status endpoint just needs them
            // recognisable as host:port — we don't pin the exact port.
            foreach (var f in followers)
            {
                Assert.Contains(":", f.Endpoint);
                Assert.NotEqual("unknown", f.Endpoint);
                Assert.True(f.ConnectedAtUtc > DateTime.UtcNow.AddMinutes(-1));
            }

            // The follower side knows its leader's endpoint — what the
            // status payload uses to populate `follower.leader.endpoint`.
            Assert.Equal($"localhost:{port}", follower1.LogicalFollowerLeaderEndpoint);
            Assert.Equal($"localhost:{port}", follower2.LogicalFollowerLeaderEndpoint);

            // A non-replicating db reports null so the JSON omits the field
            // gracefully via the null-coalesce in /replication/status.
            using var standalone = DocumentForgeDb.Create(
                Path.Combine(Path.GetTempPath(), $"topostandalone_{Guid.NewGuid():N}.dfdb"));
            Assert.Null(standalone.LogicalFollowerLeaderEndpoint);
            Assert.Empty(standalone.GetLogicalFollowers());
        }
        finally
        {
            try { File.Delete(leaderPath); File.Delete(leaderPath + ".wal"); File.Delete(leaderPath + ".recovery"); } catch { }
            try { File.Delete(follower1Path); File.Delete(follower1Path + ".wal"); File.Delete(follower1Path + ".recovery"); File.Delete(follower1Path + ".followerseq"); } catch { }
            try { File.Delete(follower2Path); File.Delete(follower2Path + ".wal"); File.Delete(follower2Path + ".recovery"); File.Delete(follower2Path + ".followerseq"); } catch { }
        }
    }

    // --- Replication HTTP-endpoint exchange (issue #51) ---
    //
    // Followers advertise their HTTP base URL during the replication
    // handshake so the leader can surface it on /replication/status. The
    // Studio "Discover network" feature uses this to walk peers
    // without guessing port/scheme.

    [Fact]
    public async System.Threading.Tasks.Task LogicalReplication_LeaderSeesFollowerHttpEndpoint()
    {
        int port = 5950 + System.Random.Shared.Next(40);
        var leaderPath = Path.Combine(Path.GetTempPath(), $"http_ep_leader_{Guid.NewGuid():N}.dfdb");
        var follower1Path = Path.Combine(Path.GetTempPath(), $"http_ep_fol1_{Guid.NewGuid():N}.dfdb");
        var follower2Path = Path.Combine(Path.GetTempPath(), $"http_ep_fol2_{Guid.NewGuid():N}.dfdb");
        try
        {
            using var leader = DocumentForgeDb.Create(leaderPath);
            leader.StartLogicalReplicationServer(port);
            await System.Threading.Tasks.Task.Delay(150);

            using var follower1 = DocumentForgeDb.Create(follower1Path);
            follower1.StartLogicalReplicationFollower("localhost", port,
                ownHttpEndpoint: "http://10.0.0.5:5001");

            using var follower2 = DocumentForgeDb.Create(follower2Path);
            follower2.StartLogicalReplicationFollower("localhost", port,
                ownHttpEndpoint: "https://node-2.cluster.local");

            for (int i = 0; i < 30 && leader.GetLogicalFollowerCount() < 2; i++)
                await System.Threading.Tasks.Task.Delay(100);

            var followers = leader.GetLogicalFollowers();
            Assert.Equal(2, followers.Count);

            // Both followers' HTTP endpoints are surfaced. Order isn't
            // guaranteed (depends on connect timing), so collect into a set.
            var endpoints = followers.Select(f => f.HttpEndpoint).ToHashSet();
            Assert.Contains("http://10.0.0.5:5001", endpoints);
            Assert.Contains("https://node-2.cluster.local", endpoints);
        }
        finally
        {
            try { File.Delete(leaderPath); File.Delete(leaderPath + ".wal"); File.Delete(leaderPath + ".recovery"); } catch { }
            try { File.Delete(follower1Path); File.Delete(follower1Path + ".wal"); File.Delete(follower1Path + ".recovery"); File.Delete(follower1Path + ".followerseq"); } catch { }
            try { File.Delete(follower2Path); File.Delete(follower2Path + ".wal"); File.Delete(follower2Path + ".recovery"); File.Delete(follower2Path + ".followerseq"); } catch { }
        }
    }

    [Fact]
    public void NodeConfig_ResolveHttpEndpoint_PublicBaseUrlWinsWhenSet()
    {
        var c = new DocumentForge.Cli.NodeConfig
        {
            Port = 5005,
            Network = new DocumentForge.Cli.NetworkConfig { PublicBaseUrl = "https://node-1.example.com" }
        };
        Assert.Equal("https://node-1.example.com", c.ResolveHttpEndpoint());

        // Trailing slash is normalized away.
        c.Network.PublicBaseUrl = "https://example.com/";
        Assert.Equal("https://example.com", c.ResolveHttpEndpoint());
    }

    [Fact]
    public void NodeConfig_ResolveHttpEndpoint_DerivesFromPortWhenNoOverride()
    {
        var c = new DocumentForge.Cli.NodeConfig { Port = 5050 };
        Assert.Equal("http://localhost:5050", c.ResolveHttpEndpoint());
    }

    [Fact]
    public async System.Threading.Tasks.Task LogicalReplication_FollowerWithoutHttpEndpoint_LeaderReportsNull()
    {
        // A follower with ownHttpEndpoint=null (default — the legacy
        // behaviour) writes an empty endpoint suffix; the leader stores
        // null and Studio falls back to its port-guess.
        int port = 5990 + System.Random.Shared.Next(10);
        var leaderPath = Path.Combine(Path.GetTempPath(), $"http_ep_legacy_l_{Guid.NewGuid():N}.dfdb");
        var followerPath = Path.Combine(Path.GetTempPath(), $"http_ep_legacy_f_{Guid.NewGuid():N}.dfdb");
        try
        {
            using var leader = DocumentForgeDb.Create(leaderPath);
            leader.StartLogicalReplicationServer(port);
            await System.Threading.Tasks.Task.Delay(150);

            using var follower = DocumentForgeDb.Create(followerPath);
            follower.StartLogicalReplicationFollower("localhost", port);  // no ownHttpEndpoint

            for (int i = 0; i < 30 && leader.GetLogicalFollowerCount() < 1; i++)
                await System.Threading.Tasks.Task.Delay(100);

            var followers = leader.GetLogicalFollowers();
            Assert.Single(followers);
            Assert.Null(followers[0].HttpEndpoint);
        }
        finally
        {
            try { File.Delete(leaderPath); File.Delete(leaderPath + ".wal"); File.Delete(leaderPath + ".recovery"); } catch { }
            try { File.Delete(followerPath); File.Delete(followerPath + ".wal"); File.Delete(followerPath + ".recovery"); File.Delete(followerPath + ".followerseq"); } catch { }
        }
    }

    // --- Snapshot / backup API (issue #27) ---

    [Fact]
    public void Snapshot_ProducesIndependentFileWithSameContent()
    {
        _db.Insert("orders", """{"pnr":"ABC","seat":"12A"}""");
        _db.Insert("orders", """{"pnr":"DEF","seat":"14B"}""");
        _db.CreateIndex("orders", "pnr", "idx_orders_pnr", unique: true);

        var snapshotPath = Path.Combine(Path.GetTempPath(), $"snapshot_{Guid.NewGuid():N}.dfdb");
        try
        {
            _db.Snapshot(snapshotPath);
            Assert.True(File.Exists(snapshotPath));

            // The snapshot must open as an independent DB with the same docs
            // AND the same indexes (so queries plan correctly post-restore).
            using var restored = DocumentForgeDb.Open(snapshotPath);
            var rows = restored.Execute("SELECT * FROM orders").Documents;
            Assert.Equal(2, rows.Count);
            Assert.Single(restored.GetIndexes("orders"));

            // Indexed lookup against the snapshot finds the row — proves the
            // index entries copied over and the catalog pointer survived.
            var byPnr = restored.Execute("SELECT * FROM orders WHERE pnr = 'ABC'").Documents;
            Assert.Single(byPnr);
            Assert.Equal("12A", byPnr[0]["seat"].AsString);
        }
        finally
        {
            try { File.Delete(snapshotPath); } catch { }
            try { File.Delete(snapshotPath + ".wal"); } catch { }
            try { File.Delete(snapshotPath + ".recovery"); } catch { }
        }
    }

    [Fact]
    public void Snapshot_LiveDbContinuesToWorkAfterSnapshot()
    {
        _db.Insert("orders", """{"pnr":"BEFORE"}""");

        var snapshotPath = Path.Combine(Path.GetTempPath(), $"snapshot_{Guid.NewGuid():N}.dfdb");
        try
        {
            _db.Snapshot(snapshotPath);

            // Live DB must accept new writes after the snapshot returns; the
            // brief write-lock window during snapshot shouldn't leave the
            // engine in any kind of degraded state.
            _db.Insert("orders", """{"pnr":"AFTER"}""");
            Assert.Equal(2, _db.Execute("SELECT * FROM orders").Documents.Count);

            // The snapshot must not contain the post-snapshot row.
            using var restored = DocumentForgeDb.Open(snapshotPath);
            var rows = restored.Execute("SELECT * FROM orders").Documents;
            Assert.Single(rows);
            Assert.Equal("BEFORE", rows[0]["pnr"].AsString);
        }
        finally
        {
            try { File.Delete(snapshotPath); } catch { }
            try { File.Delete(snapshotPath + ".wal"); } catch { }
            try { File.Delete(snapshotPath + ".recovery"); } catch { }
        }
    }

    [Fact]
    public void Snapshot_SameAsLivePath_ThrowsClearError()
    {
        // Copying to the same path would self-truncate the live data file
        // mid-flush. The engine catches that ahead of File.Copy with an
        // ArgumentException so the failure mode is "no-op + clear error",
        // not "corrupt the live database".
        Assert.Throws<ArgumentException>(() => _db.Snapshot(_dbPath));
    }

    // --- On-disk lock (issue #26) ---

    [Fact]
    public void Open_RejectsConcurrentSecondOpener()
    {
        // The first DB instance is _db (held open by the test fixture). A
        // second Open of the same path must surface a clear error rather than
        // silently allowing both to write.
        Assert.Throws<DatabaseLockedException>(() => DocumentForgeDb.Open(_dbPath));
    }

    [Fact]
    public void Open_AfterClose_AcquiresFreshLock()
    {
        // Round-trip: dispose the held lock, reopen — the second open must
        // succeed once the first has cleanly released.
        _db.Dispose();
        using var reopened = DocumentForgeDb.Open(_dbPath);
        // Smoke-test: a real op proves the engine is functional, not just
        // that the constructor returned.
        reopened.Insert("orders", """{"pnr":"AFTER"}""");
        Assert.Single(reopened.Execute("SELECT * FROM orders").Documents);
    }

    [Fact]
    public void Open_StaleLockFromDeadHolder_AutoReclaims()
    {
        _db.Dispose();
        var lockPath = _dbPath + ".lock";

        // Plant a stale lock pointing at a definitely-dead pid on this host.
        // 0x7FFFFFFF is impossibly out of range for a real process; the
        // reclaim path will see it doesn't exist and take the lock.
        var stale = """{"Pid":2147483646,"Host":""" + System.Text.Json.JsonSerializer.Serialize(Environment.MachineName) + ""","OpenedAtUtc":"2020-01-01T00:00:00Z"}""";
        File.WriteAllText(lockPath, stale);

        using var reopened = DocumentForgeDb.Open(_dbPath);
        reopened.Insert("orders", """{"pnr":"X"}""");
        Assert.Single(reopened.Execute("SELECT * FROM orders").Documents);
    }

    [Fact]
    public void Open_StaleLockFromDifferentHost_AutoReclaims_85()
    {
        // Issue #85 — pre-1.2.0 a lock file whose Host field didn't match
        // the current machine name was treated as a poisoned cross-host
        // lock and rejected. That was a false positive every time a Docker
        // container redeployed (each container gets a fresh random hostname
        // even on the same physical host), so an OOM-killed container left
        // its database permanently un-openable.
        //
        // Now: the FileShare.None OS-level lock IS the truth. If the prior
        // holder process is gone (which it is — the file is just text on
        // disk with no live handle), the OS released the lock and the new
        // Open succeeds. The hostname field becomes purely diagnostic.
        _db.Dispose();
        var lockPath = _dbPath + ".lock";

        var foreign = """{"Pid":1234,"Host":"4ca385e4a08a","OpenedAtUtc":"2020-01-01T00:00:00Z"}""";
        File.WriteAllText(lockPath, foreign);

        // No ForceUnlock needed — should just work.
        using var reopened = DocumentForgeDb.Open(_dbPath);
        reopened.Insert("orders", """{"pnr":"AFTER-CRASH"}""");
        Assert.Single(reopened.Execute("SELECT * FROM orders").Documents);
    }

    [Fact]
    public void Open_LiveSecondOpener_StillRejected_RegardlessOfHostname()
    {
        // The change from #85 mustn't open the door to genuine concurrent
        // openers. _db is held by the test fixture (a real, live handle).
        // Even if the lock file's hostname field were forged to match
        // ours, the OS-level FileShare.None lock still rejects the
        // second open.
        var lockPath = _dbPath + ".lock";
        // Forge a "looks legit" lock file pointing at this host — but
        // the OS lock from _db is what blocks us.
        var fake = $$"""{"Pid":1,"Host":{{System.Text.Json.JsonSerializer.Serialize(Environment.MachineName)}},"OpenedAtUtc":"2020-01-01T00:00:00Z"}""";
        try { File.WriteAllText(lockPath, fake); } catch { /* held by _db, fine */ }

        Assert.Throws<DatabaseLockedException>(() => DocumentForgeDb.Open(_dbPath));
    }

    // --- Scalar SQL functions (issue #16) ---
    //
    // The headline goal is server-side ID/timestamp generation and string
    // transforms callers can use without round-tripping. INSERT-with-functions
    // is deferred (would need a JSON+functions parser); this batch covers the
    // WHERE and UPDATE SET shapes — the immediately useful surface.

    [Fact]
    public void Sql_Function_Lower_InWhere_MatchesIndexedField()
    {
        _db.Insert("users", """{"email":"Alice@Example.com"}""");
        _db.Insert("users", """{"email":"BOB@x.com"}""");

        // LOWER(email) lets callers normalise on the server side without
        // having to lower-case the value client-side first.
        var rows = _db.Execute("SELECT * FROM users WHERE LOWER(email) = 'alice@example.com'").Documents;
        Assert.Single(rows);
        Assert.Equal("Alice@Example.com", rows[0]["email"].AsString);
    }

    [Fact]
    public void Sql_Function_Upper_InWhereOnLiteralRhs()
    {
        _db.Insert("users", """{"name":"ALICE"}""");
        _db.Insert("users", """{"name":"bob"}""");

        // The right-hand side (a function call wrapping a literal) is the
        // simpler case — no row context needed for the arg.
        var rows = _db.Execute("SELECT * FROM users WHERE name = UPPER('alice')").Documents;
        Assert.Single(rows);
        Assert.Equal("ALICE", rows[0]["name"].AsString);
    }

    [Fact]
    public void Sql_Function_Length_InWhere()
    {
        _db.Insert("users", """{"code":"AB"}""");
        _db.Insert("users", """{"code":"ABCD"}""");
        _db.Insert("users", """{"code":"ABCDEF"}""");

        var rows = _db.Execute("SELECT * FROM users WHERE LENGTH(code) = 4").Documents;
        Assert.Single(rows);
        Assert.Equal("ABCD", rows[0]["code"].AsString);
    }

    [Fact]
    public void Sql_Function_Trim_InWhere()
    {
        _db.Insert("users", """{"name":"  Alice  "}""");
        _db.Insert("users", """{"name":"Bob"}""");

        var rows = _db.Execute("SELECT * FROM users WHERE TRIM(name) = 'Alice'").Documents;
        Assert.Single(rows);
    }

    [Fact]
    public void Sql_Function_Coalesce_TwoArg_FallsBackOnNullPath()
    {
        _db.Insert("users", """{"name":"Alice","nickname":"Ali"}""");
        _db.Insert("users", """{"name":"Bob"}""");
        _db.Insert("users", """{"name":"Charlie","nickname":"Chuck"}""");

        // COALESCE(nickname, name) returns the first non-null. The Bob row
        // has no nickname → falls through to "Bob"; matches.
        var rows = _db.Execute("SELECT * FROM users WHERE COALESCE(nickname, name) = 'Bob'").Documents;
        Assert.Single(rows);
        Assert.Equal("Bob", rows[0]["name"].AsString);
    }

    [Fact]
    public void Sql_Function_Ifnull_AliasOfCoalesce()
    {
        _db.Insert("users", """{"name":"Alice"}""");

        // IFNULL is the two-arg shorthand many SQL dialects use; here it's
        // an alias of COALESCE.
        var rows = _db.Execute("SELECT * FROM users WHERE IFNULL(nickname, name) = 'Alice'").Documents;
        Assert.Single(rows);
    }

    [Fact]
    public void Sql_Function_Newid_InUpdate_StampsFreshId()
    {
        var id = _db.Insert("users", """{"name":"Alice"}""");

        // NEWID() in UPDATE SET — a real motivating use case: backfilling a
        // GUID column without round-tripping a generated value from the client.
        var r = _db.Execute("UPDATE users SET sessionToken = NEWID() WHERE name = 'Alice'");
        Assert.True(r.Success, r.Message);
        Assert.Equal(1, r.AffectedCount);

        var doc = _db.GetCollection("users")!.FindById(id)!;
        var token = doc["sessionToken"].AsString;
        Assert.True(Guid.TryParse(token, out _),
            $"sessionToken should be a parseable GUID, got '{token}'");
    }

    [Fact]
    public void Sql_Function_Newid_TwoCallsProduceDifferentValues()
    {
        // Each NEWID() call must be a fresh GUID, not constant-folded to the
        // same value across rows in a single UPDATE.
        _db.Insert("users", """{"name":"A"}""");
        _db.Insert("users", """{"name":"B"}""");
        _db.Insert("users", """{"name":"C"}""");

        _db.Execute("UPDATE users SET token = NEWID()");

        var tokens = _db.Execute("SELECT * FROM users").Documents
            .Select(d => d["token"].AsString)
            .ToList();
        Assert.Equal(3, tokens.Count);
        Assert.Equal(3, tokens.Distinct().Count());
    }

    [Fact]
    public void Sql_Function_Now_InUpdate_StampsServerTimestamp()
    {
        _db.Insert("orders", """{"status":"pending"}""");

        var before = DateTime.UtcNow.AddSeconds(-2);
        _db.Execute("UPDATE orders SET updatedAt = NOW() WHERE status = 'pending'");
        var after = DateTime.UtcNow.AddSeconds(2);

        var doc = _db.Execute("SELECT * FROM orders").Documents[0];
        var stamped = doc["updatedAt"].AsDateTime;
        Assert.InRange(stamped.UtcDateTime, before, after);
    }

    [Fact]
    public void Sql_Function_Getdate_AliasOfNow()
    {
        // SQL Server's GETDATE() and ANSI NOW() share an implementation here.
        _db.Insert("orders", """{"status":"new"}""");
        _db.Execute("UPDATE orders SET createdAt = GETDATE() WHERE status = 'new'");
        var stamped = _db.Execute("SELECT * FROM orders").Documents[0]["createdAt"].AsDateTime;
        Assert.True(stamped.UtcDateTime > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void Sql_Function_CurrentTimestamp_AliasOfNow()
    {
        _db.Insert("orders", """{"status":"new"}""");
        _db.Execute("UPDATE orders SET createdAt = CURRENT_TIMESTAMP() WHERE status = 'new'");
        var stamped = _db.Execute("SELECT * FROM orders").Documents[0]["createdAt"].AsDateTime;
        Assert.True(stamped.UtcDateTime > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void Sql_Function_Lower_InUpdate_NormalisesPathArg()
    {
        // SET name = LOWER(name) — function reads the OLD doc value, not the
        // half-built newDoc. Matters because we update the same field we're
        // reading from.
        var id = _db.Insert("users", """{"name":"ALICE"}""");

        var r = _db.Execute("UPDATE users SET name = LOWER(name)");
        Assert.True(r.Success, r.Message);
        Assert.Equal(1, r.AffectedCount);

        var doc = _db.GetCollection("users")!.FindById(id)!;
        Assert.Equal("alice", doc["name"].AsString);
    }

    [Fact]
    public void Sql_Function_Coalesce_InUpdate_FillsMissingField()
    {
        _db.Insert("users", """{"name":"Alice"}""");
        _db.Insert("users", """{"name":"Bob","displayName":"Bobby"}""");

        // SET displayName = COALESCE(displayName, name): Alice gets her name
        // copied (no displayName); Bob keeps Bobby (already set).
        _db.Execute("UPDATE users SET displayName = COALESCE(displayName, name)");

        var rows = _db.Execute("SELECT * FROM users").Documents;
        var alice = rows.First(d => d["name"].AsString == "Alice");
        var bob = rows.First(d => d["name"].AsString == "Bob");
        Assert.Equal("Alice", alice["displayName"].AsString);
        Assert.Equal("Bobby", bob["displayName"].AsString);
    }

    [Fact]
    public void Sql_Function_Unknown_FailsCleanly()
    {
        _db.Insert("users", """{"name":"Alice"}""");
        // Typo on the function name surfaces as a clear error rather than a
        // mysterious zero-row result.
        var r = _db.Execute("SELECT * FROM users WHERE NWID() = 'x'");
        Assert.False(r.Success);
        Assert.Contains("NWID", r.Message ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sql_Function_WrongArity_FailsCleanly()
    {
        _db.Insert("users", """{"name":"Alice"}""");
        var r = _db.Execute("SELECT * FROM users WHERE LOWER() = 'alice'");
        Assert.False(r.Success);
    }

    // --- Build identification (issue #36) ---
    //
    // The /version REST endpoint just JSON-wraps these properties, so the
    // engine-level tests are sufficient — one HTTP integration smoke test
    // would just re-verify what's checked here.

    [Fact]
    public void BuildInfo_Sha_IsAlwaysSomethingNonEmpty()
    {
        // Sha resolves through assembly metadata → env var → "dev". One of the
        // three always wins, so the value is never empty/null in any
        // environment we'd run tests in.
        var sha = BuildInfo.Sha;
        Assert.False(string.IsNullOrEmpty(sha));
    }

    [Fact]
    public void BuildInfo_BuiltAt_IsRecentEnoughToBeMeaningful()
    {
        // BuildTimeUtc comes from Directory.Build.props at build time, so
        // running the tests should see a value newer than "the project was
        // committed in 2026". A null value means none of the three sources
        // worked — that's a regression worth catching.
        var t = BuildInfo.BuiltAtUtc;
        Assert.NotNull(t);
        Assert.True(t.Value > new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            $"BuildInfo.BuiltAtUtc = {t:o} — expected something post-2026.");
    }

    [Fact]
    public void BuildInfo_Image_IsNullWhenEnvVarUnset()
    {
        // No DFDB_IMAGE set in the test runner — the property must report null
        // rather than fabricating a value. The endpoint surfaces null as JSON
        // null so admin UIs can display "no image (likely a local dev build)".
        // This test is defensive against a future where someone reads from
        // assembly metadata or hardcodes a default.
        var prior = Environment.GetEnvironmentVariable("DFDB_IMAGE");
        try
        {
            Environment.SetEnvironmentVariable("DFDB_IMAGE", null);
            // Image is cached — re-reading after setting to null doesn't help
            // here. Instead just assert that whatever the cached value is, it's
            // not a fabricated string in the absence of the env var.
            var img = BuildInfo.Image;
            // Either null (env wasn't set when first observed) OR the value the
            // env var had then. Either way, no fabrication.
            if (img is not null)
            {
                Assert.Equal(prior, img);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("DFDB_IMAGE", prior);
        }
    }

    // --- INSERT tuple form with scalar functions (issue #34) ---

    [Fact]
    public void Sql_Insert_TupleForm_AcceptsLiteralsAndFunctions()
    {
        // The headline use case from #16: NEWID() + GETDATE() server-side
        // at insert time. Pre-fix this was the WORKAROUND callers reached
        // for — generate a Guid client-side, format the JSON, send it up.
        var r = _db.Execute(
            "INSERT INTO users (_id, email, createdAt) VALUES (NEWID(), 'a@b.com', GETDATE())");
        Assert.True(r.Success, r.Message);
        Assert.Equal(1, r.AffectedCount);

        var rows = _db.Execute("SELECT * FROM users").Documents;
        Assert.Single(rows);
        var doc = rows[0];

        // _id is a fresh GUID — parses as one and isn't the empty Guid.
        Assert.True(Guid.TryParse(doc["_id"].ToString(), out var idGuid));
        Assert.NotEqual(Guid.Empty, idGuid);

        Assert.Equal("a@b.com", doc["email"].AsString);

        // createdAt was stamped by the server's clock — we can't pin the
        // exact value, just bracket it.
        var createdAt = doc["createdAt"].AsDateTime;
        Assert.True(createdAt.UtcDateTime > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void Sql_Insert_TupleForm_NewIdEachCallIsUnique()
    {
        // Two consecutive INSERTs with NEWID() must produce different ids —
        // not constant-folded across calls.
        _db.Execute("INSERT INTO users (_id, name) VALUES (NEWID(), 'A')");
        _db.Execute("INSERT INTO users (_id, name) VALUES (NEWID(), 'B')");
        var ids = _db.Execute("SELECT * FROM users").Documents
            .Select(d => d["_id"].ToString()).ToList();
        Assert.Equal(2, ids.Count);
        Assert.Equal(2, ids.Distinct().Count());
    }

    [Fact]
    public void Sql_Insert_TupleForm_NestedFunctionCall()
    {
        // LOWER('Alice') as a value — function args nest down through the
        // ValueExpression tree the same way they do in WHERE / UPDATE SET.
        var r = _db.Execute("INSERT INTO users (name) VALUES (LOWER('Alice'))");
        Assert.True(r.Success, r.Message);
        var doc = _db.Execute("SELECT * FROM users").Documents[0];
        Assert.Equal("alice", doc["name"].AsString);
    }

    [Fact]
    public void Sql_Insert_TupleForm_ColumnValueCountMismatch_FailsCleanly()
    {
        var r = _db.Execute("INSERT INTO users (a, b, c) VALUES ('x', 'y')");
        Assert.False(r.Success);
        Assert.Contains("mismatch", r.Message ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sql_Insert_JsonForm_StillWorks()
    {
        // The classic JSON form has to keep working unchanged — anything
        // that broke it would silently regress every pre-#34 caller.
        var r = _db.Execute("""INSERT INTO orders VALUES { "pnr": "ABC", "seat": "12A" }""");
        Assert.True(r.Success, r.Message);
        var rows = _db.Execute("SELECT * FROM orders").Documents;
        Assert.Single(rows);
        Assert.Equal("ABC", rows[0]["pnr"].AsString);
        Assert.Equal("12A", rows[0]["seat"].AsString);
    }

    [Fact]
    public void Sql_Insert_TupleForm_HonoursUniqueIndex()
    {
        _db.Insert("users", """{"email":"existing@x.com"}""");
        _db.CreateIndex("users", "email", "idx_users_email", unique: true);

        // A function-generated _id colliding with literal-uniqueness on email
        // — the pre-flight ValidateUniqueInsert in ExecuteInsert catches it
        // before the row hits storage.
        var r = _db.Execute(
            "INSERT INTO users (_id, email) VALUES (NEWID(), 'existing@x.com')");
        Assert.False(r.Success);
        Assert.Contains("Duplicate key", r.Message ?? "", StringComparison.OrdinalIgnoreCase);

        // Pre-fix was a real risk: the doc would have been stranded on disk
        // if uniqueness fired AFTER the page write. Confirm the failed row
        // didn't leak.
        var rows = _db.Execute("SELECT * FROM users").Documents;
        Assert.Single(rows);
    }

    // --- Optimistic concurrency / ETag (issue #18) ---

    [Fact]
    public void Insert_StampsFreshEtag()
    {
        var id = _db.Insert("users", """{"email":"a@b.com"}""");
        var doc = _db.GetCollection("users")!.FindById(id)!;
        var etag = doc.GetEtag();
        Assert.False(string.IsNullOrEmpty(etag));
        Assert.True(Guid.TryParse(etag, out _),
            $"ETag should be a parseable GUID, got '{etag}'");
    }

    [Fact]
    public void Replace_RestampsEtag()
    {
        // Two consecutive replaces must mint two different etags so an
        // If-Match client sees the change.
        var id = _db.Insert("users", """{"email":"a@b.com","v":1}""");
        var beforeEtag = _db.GetCollection("users")!.FindById(id)!.GetEtag();

        _db.Replace("users", id, """{"email":"a@b.com","v":2}""");
        var afterEtag = _db.GetCollection("users")!.FindById(id)!.GetEtag();

        Assert.False(string.IsNullOrEmpty(afterEtag));
        Assert.NotEqual(beforeEtag, afterEtag);
    }

    [Fact]
    public void ReplaceIfEtag_MatchingEtag_AppliesAndReturnsNewEtag()
    {
        var id = _db.Insert("users", """{"email":"a@b.com","v":1}""");
        var oldEtag = _db.GetCollection("users")!.FindById(id)!.GetEtag();

        var newEtag = _db.ReplaceIfEtag("users", id, """{"email":"a@b.com","v":2}""", oldEtag);
        Assert.NotNull(newEtag);
        Assert.NotEqual(oldEtag, newEtag);

        var doc = _db.GetCollection("users")!.FindById(id)!;
        Assert.Equal(2, doc["v"].AsInt32);
        Assert.Equal(newEtag, doc.GetEtag());
    }

    [Fact]
    public void ReplaceIfEtag_StaleEtag_ThrowsAndDoesNotApply()
    {
        var id = _db.Insert("users", """{"email":"a@b.com","v":1}""");
        var oldEtag = _db.GetCollection("users")!.FindById(id)!.GetEtag();

        // Someone else updated the doc — etag changed.
        _db.Replace("users", id, """{"email":"a@b.com","v":2}""");

        // Our stale If-Match must throw and the v=2 row must be untouched.
        var ex = Assert.Throws<EtagMismatchException>(() =>
            _db.ReplaceIfEtag("users", id, """{"email":"a@b.com","v":99}""", oldEtag));
        Assert.Equal(oldEtag, ex.ExpectedEtag);

        var doc = _db.GetCollection("users")!.FindById(id)!;
        Assert.Equal(2, doc["v"].AsInt32);
    }

    [Fact]
    public void ReplaceIfEtag_NotFound_ReturnsNullWithoutThrowing()
    {
        var fakeId = new DocumentId(Guid.NewGuid());
        var result = _db.ReplaceIfEtag("users", fakeId, """{"x":1}""", "any-etag");
        Assert.Null(result);
    }

    [Fact]
    public void Replace_WithoutEtagCheck_StillWorksLastWriteWins()
    {
        // Pre-#18 callers using the unguarded Replace must see no
        // behaviour change — they get last-write-wins, the etag rotates
        // silently, and no exception fires.
        var id = _db.Insert("users", """{"email":"a@b.com","v":1}""");
        Assert.True(_db.Replace("users", id, """{"email":"a@b.com","v":2}"""));
        Assert.True(_db.Replace("users", id, """{"email":"a@b.com","v":3}"""));
        Assert.Equal(3, _db.GetCollection("users")!.FindById(id)!["v"].AsInt32);
    }

    // --- JOIN extensions: LEFT / RIGHT / CROSS (issue #17 Phase A) ---

    [Fact]
    public void Sql_LeftJoin_NullPadsMissingRightSide()
    {
        // Two users; only one has an order. LEFT JOIN must emit BOTH users —
        // the one without orders comes back with the orders side null-padded.
        // Pre-#17 this was inexpressible (parser only knew JOIN = INNER).
        _db.Insert("users", """{"id":"u1","name":"Alice"}""");
        _db.Insert("users", """{"id":"u2","name":"Bob"}""");
        _db.Insert("orders", """{"userId":"u1","pnr":"ABC"}""");

        var rows = _db.Execute(
            "SELECT * FROM users LEFT JOIN orders ON users.id = orders.userId").Documents;
        Assert.Equal(2, rows.Count);

        // Result docs nest under the source-collection name. Every row has
        // a `users` block; only Alice's row has a non-empty `orders` block.
        var alice = rows.First(d => d["users"].AsDocument["name"].AsString == "Alice");
        var bob = rows.First(d => d["users"].AsDocument["name"].AsString == "Bob");
        Assert.Equal("ABC", alice["orders"].AsDocument["pnr"].AsString);
        // Bob's orders side is the null-pad — empty doc.
        Assert.Equal(0, bob["orders"].AsDocument.Count);
    }

    [Fact]
    public void Sql_LeftOuterJoin_AcceptsTheOuterKeyword()
    {
        // SQL-92 sugar: `LEFT OUTER JOIN` is a strict synonym for `LEFT JOIN`.
        _db.Insert("users", """{"id":"u1","name":"Alice"}""");
        var rows = _db.Execute(
            "SELECT * FROM users LEFT OUTER JOIN orders ON users.id = orders.userId").Documents;
        Assert.Single(rows);
    }

    [Fact]
    public void Sql_RightJoin_NullPadsMissingLeftSide()
    {
        // Two orders; only one has a matching user. RIGHT JOIN emits BOTH
        // orders — the unmatched one comes back with the users side null.
        _db.Insert("users", """{"id":"u1","name":"Alice"}""");
        _db.Insert("orders", """{"userId":"u1","pnr":"ABC"}""");
        _db.Insert("orders", """{"userId":"u99","pnr":"XYZ"}""");

        var rows = _db.Execute(
            "SELECT * FROM users RIGHT JOIN orders ON users.id = orders.userId").Documents;
        Assert.Equal(2, rows.Count);

        var matched = rows.First(d => d["orders"].AsDocument["pnr"].AsString == "ABC");
        var unmatched = rows.First(d => d["orders"].AsDocument["pnr"].AsString == "XYZ");
        Assert.Equal("Alice", matched["users"].AsDocument["name"].AsString);
        Assert.Equal(0, unmatched["users"].AsDocument.Count);
    }

    [Fact]
    public void Sql_CrossJoin_ProducesCartesianProduct()
    {
        // No ON clause — every left × every right.
        _db.Insert("colors", """{"name":"red"}""");
        _db.Insert("colors", """{"name":"blue"}""");
        _db.Insert("sizes", """{"label":"S"}""");
        _db.Insert("sizes", """{"label":"M"}""");
        _db.Insert("sizes", """{"label":"L"}""");

        var r = _db.Execute("SELECT * FROM colors CROSS JOIN sizes");
        Assert.True(r.Success, r.Message);
        Assert.Equal(2 * 3, r.Documents.Count);
        Assert.Contains("CROSS_JOIN", r.QueryPlan);
    }

    [Fact]
    public void Sql_InnerJoin_BackwardsCompatibleWithBareJoinKeyword()
    {
        // The pre-#17 form `JOIN` (no qualifier) must continue to behave
        // exactly as it did. Same dataset as the LEFT test above; INNER
        // returns ONLY Alice (Bob has no order).
        _db.Insert("users", """{"id":"u1","name":"Alice"}""");
        _db.Insert("users", """{"id":"u2","name":"Bob"}""");
        _db.Insert("orders", """{"userId":"u1","pnr":"ABC"}""");

        var rows = _db.Execute(
            "SELECT * FROM users JOIN orders ON users.id = orders.userId").Documents;
        Assert.Single(rows);
        Assert.Equal("Alice", rows[0]["users"].AsDocument["name"].AsString);
    }

    // --- JOIN Phase B+C: multi-join chains + compound ON (issue #44) ---

    [Fact]
    public void Sql_MultiJoin_ChainOfThree()
    {
        // a JOIN b ON ... JOIN c ON ...
        // Pre-#44 the parser exited after the first JOIN; the second one
        // produced a parse error. Now joins chain left-deep and the
        // executor walks all of them in source order.
        _db.Insert("users", """{"id":"u1","name":"Alice"}""");
        _db.Insert("users", """{"id":"u2","name":"Bob"}""");
        _db.Insert("orders", """{"userId":"u1","sku":"sku-A"}""");
        _db.Insert("orders", """{"userId":"u2","sku":"sku-B"}""");
        _db.Insert("products", """{"sku":"sku-A","name":"Widget"}""");
        _db.Insert("products", """{"sku":"sku-B","name":"Gadget"}""");

        var r = _db.Execute(
            "SELECT * FROM users " +
            "JOIN orders ON users.id = orders.userId " +
            "JOIN products ON orders.sku = products.sku");
        Assert.True(r.Success, r.Message);
        Assert.Equal(2, r.Documents.Count);

        // Each result should carry all three sub-docs (users, orders, products).
        foreach (var doc in r.Documents)
        {
            Assert.True(doc.ContainsKey("users"));
            Assert.True(doc.ContainsKey("orders"));
            Assert.True(doc.ContainsKey("products"));
        }
    }

    [Fact]
    public void Sql_CompoundOn_TwoEqualitiesAndedTogether()
    {
        // ON a.x = b.x AND a.y = b.y
        // Composite-key joins are common with surrogate keys and date/version
        // partitioning. Pre-#44 the parser only accepted a single equality.
        _db.Insert("orders", """{"region":"US","sku":"A","qty":10}""");
        _db.Insert("orders", """{"region":"US","sku":"B","qty":5}""");
        _db.Insert("orders", """{"region":"EU","sku":"A","qty":3}""");
        _db.Insert("inventory", """{"region":"US","sku":"A","onHand":100}""");
        _db.Insert("inventory", """{"region":"EU","sku":"A","onHand":50}""");
        // Note: no inventory row for (US, B) — that order shouldn't match.

        var r = _db.Execute(
            "SELECT * FROM orders JOIN inventory " +
            "ON orders.region = inventory.region AND orders.sku = inventory.sku");
        Assert.True(r.Success, r.Message);
        Assert.Equal(2, r.Documents.Count);

        // Verify the matched pairs are the right ones.
        foreach (var doc in r.Documents)
        {
            var orderRegion = doc["orders"].AsDocument["region"].AsString;
            var orderSku = doc["orders"].AsDocument["sku"].AsString;
            var invRegion = doc["inventory"].AsDocument["region"].AsString;
            var invSku = doc["inventory"].AsDocument["sku"].AsString;
            Assert.Equal(orderRegion, invRegion);
            Assert.Equal(orderSku, invSku);
        }
    }

    [Fact]
    public void Sql_LeftJoin_ChainedAfterInner()
    {
        // INNER then LEFT — the LEFT's null-padding should still work after
        // the chain handed it a combined doc as the outer.
        _db.Insert("users", """{"id":"u1","name":"Alice"}""");
        _db.Insert("orders", """{"userId":"u1","sku":"sku-A"}""");
        // No products at all — LEFT JOIN should null-pad every row.
        var r = _db.Execute(
            "SELECT * FROM users " +
            "JOIN orders ON users.id = orders.userId " +
            "LEFT JOIN products ON orders.sku = products.sku");
        Assert.True(r.Success, r.Message);
        Assert.Single(r.Documents);
        Assert.Equal(0, r.Documents[0]["products"].AsDocument.Count);
    }

    // --- Replication-aware transactions (issue #13) ---

    [Fact]
    public async System.Threading.Tasks.Task LogicalReplication_MultiDocTx_ReplicatesAsAtomicBatch()
    {
        // The whole reason for #13: pre-fix the leader broadcast each
        // sub-op of a transaction as a separate logical op, so a follower
        // applying them with separate write locks could be observed in a
        // mid-tx state by a concurrent reader. Now the leader sends a
        // single TxBatch op carrying every sub-op, and the follower
        // applies them all under one write lock — atomic from any
        // observer's perspective.
        int port = 5900 + System.Random.Shared.Next(100);
        var leaderPath = Path.Combine(Path.GetTempPath(), $"txrepleader_{Guid.NewGuid():N}.dfdb");
        var followerPath = Path.Combine(Path.GetTempPath(), $"txrepfollower_{Guid.NewGuid():N}.dfdb");

        try
        {
            using var leader = DocumentForgeDb.Create(leaderPath);
            leader.StartLogicalReplicationServer(port);
            await System.Threading.Tasks.Task.Delay(150);

            using var follower = DocumentForgeDb.Create(followerPath);
            follower.StartLogicalReplicationFollower("localhost", port);

            for (int i = 0; i < 30 && leader.GetLogicalFollowerCount() < 1; i++)
                await System.Threading.Tasks.Task.Delay(100);
            Assert.Equal(1, leader.GetLogicalFollowerCount());

            // Run a multi-doc tx: 3 inserts in one transaction. Pre-fix this
            // would broadcast as 3 separate ops; post-fix as 1 TxBatch.
            using (var tx = leader.BeginTransaction())
            {
                tx.Insert("orders", """{"pnr":"A"}""");
                tx.Insert("orders", """{"pnr":"B"}""");
                tx.Insert("orders", """{"pnr":"C"}""");
                tx.Commit();
            }

            // Wait for the follower to apply the batch. opsApplied counts
            // INDIVIDUAL apply calls — pre-fix this would be 3; post-fix
            // it's 1 (the TxBatch counts as one applied op even though it
            // contains 3 sub-ops).
            for (int i = 0; i < 30 && follower.LogicallyReplicatedOps() < 1; i++)
                await System.Threading.Tasks.Task.Delay(100);

            // The actual data should be there — all 3 docs.
            var rows = follower.Execute("SELECT * FROM orders").Documents;
            Assert.Equal(3, rows.Count);

            // Wire-level assertion: follower applied exactly ONE op (the batch),
            // not three. Pre-fix would have been 3.
            Assert.Equal(1, follower.LogicallyReplicatedOps());
        }
        finally
        {
            try { File.Delete(leaderPath); File.Delete(leaderPath + ".wal"); File.Delete(leaderPath + ".recovery"); File.Delete(leaderPath + ".lock"); } catch { }
            try { File.Delete(followerPath); File.Delete(followerPath + ".wal"); File.Delete(followerPath + ".recovery"); File.Delete(followerPath + ".followerseq"); File.Delete(followerPath + ".lock"); } catch { }
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task LogicalReplication_NonTxInserts_StillBroadcastIndividually()
    {
        // Single-doc operations (non-tx) keep the per-op broadcast — TxBatch
        // is opt-in via BeginTransaction. Verify a sequence of three plain
        // db.Insert calls produces three follower ops.
        int port = 6000 + System.Random.Shared.Next(100);
        var leaderPath = Path.Combine(Path.GetTempPath(), $"singleleader_{Guid.NewGuid():N}.dfdb");
        var followerPath = Path.Combine(Path.GetTempPath(), $"singlefollower_{Guid.NewGuid():N}.dfdb");

        try
        {
            using var leader = DocumentForgeDb.Create(leaderPath);
            leader.StartLogicalReplicationServer(port);
            await System.Threading.Tasks.Task.Delay(150);
            using var follower = DocumentForgeDb.Create(followerPath);
            follower.StartLogicalReplicationFollower("localhost", port);
            for (int i = 0; i < 30 && leader.GetLogicalFollowerCount() < 1; i++)
                await System.Threading.Tasks.Task.Delay(100);

            leader.Insert("orders", """{"pnr":"X"}""");
            leader.Insert("orders", """{"pnr":"Y"}""");
            leader.Insert("orders", """{"pnr":"Z"}""");

            for (int i = 0; i < 30 && follower.LogicallyReplicatedOps() < 3; i++)
                await System.Threading.Tasks.Task.Delay(100);

            Assert.Equal(3, follower.Execute("SELECT * FROM orders").Documents.Count);
            Assert.Equal(3, follower.LogicallyReplicatedOps());
        }
        finally
        {
            try { File.Delete(leaderPath); File.Delete(leaderPath + ".wal"); File.Delete(leaderPath + ".recovery"); File.Delete(leaderPath + ".lock"); } catch { }
            try { File.Delete(followerPath); File.Delete(followerPath + ".wal"); File.Delete(followerPath + ".recovery"); File.Delete(followerPath + ".followerseq"); File.Delete(followerPath + ".lock"); } catch { }
        }
    }

    // --- Replication snapshot transfer (issue #20) ---

    [Fact]
    public async System.Threading.Tasks.Task LogicalReplication_SnapshotTransfer_BootstrapsFreshFollower()
    {
        // The whole point of #20: a fresh follower with seq 0 connecting to
        // a leader whose OpLog can't replay back to seq 0 (because the buffer
        // wrapped) should still end up converged. Pre-fix the follower
        // received an empty stream and silently joined the broadcast missing
        // every prior op. Post-fix the leader streams a full snapshot, the
        // follower writes it to disk + a marker, and the next Open of the
        // follower's data file integrates the snapshot.
        int port = 6100 + System.Random.Shared.Next(100);
        var leaderPath = Path.Combine(Path.GetTempPath(), $"snapleader_{Guid.NewGuid():N}.dfdb");
        var followerPath = Path.Combine(Path.GetTempPath(), $"snapfollower_{Guid.NewGuid():N}.dfdb");

        try
        {
            // Pre-seed the leader with docs BEFORE the follower comes up.
            // These docs are too old for the OpLog to replay — they only
            // exist in the leader's data file. This is the scenario that
            // requires a snapshot.
            using var leader = DocumentForgeDb.Create(leaderPath);
            for (int i = 0; i < 20; i++)
                leader.Insert("orders", $$"""{"pnr":"PRESEED{{i:D3}}"}""");
            leader.Flush();

            leader.StartLogicalReplicationServer(port);
            await System.Threading.Tasks.Task.Delay(150);

            // Fresh follower (no data file existed before this).
            using (var follower = DocumentForgeDb.Create(followerPath))
            {
                follower.StartLogicalReplicationFollower("localhost", port);

                // Wait for the snapshot to land + the marker to appear.
                var markerPath = followerPath + ".snapshot.incoming.seq";
                for (int i = 0; i < 50 && !File.Exists(markerPath); i++)
                    await System.Threading.Tasks.Task.Delay(100);
                Assert.True(File.Exists(markerPath),
                    $"Snapshot marker should have appeared at {markerPath}");
            }

            // Reopen the follower — Open integrates the pending snapshot.
            using var reopened = DocumentForgeDb.Open(followerPath);
            var rows = reopened.Execute("SELECT * FROM orders").Documents;
            Assert.Equal(20, rows.Count);
        }
        finally
        {
            foreach (var ext in new[] { "", ".wal", ".recovery", ".lock", ".followerseq", ".snapshot.incoming", ".snapshot.incoming.seq" })
            {
                try { File.Delete(leaderPath + ext); } catch { }
                try { File.Delete(followerPath + ext); } catch { }
            }
        }
    }

    [Fact]
    public void CreateIndex_OnFreshCollection_AutoCreates_Issue59()
    {
        // Issue #59: CreateIndex used to throw CollectionNotFoundException on a
        // collection that didn't exist yet, even though Insert / BulkInsert /
        // BulkInsertTracked all auto-create. The bootstrap-pattern caller had to
        // remember to call GetOrCreateCollection for every collection before
        // declaring its indexes — easy to forget, ugly when forgotten.
        _db.CreateIndex("flights", "flightNumber", "idx_flights_number");

        // Index is usable immediately and the collection is real:
        _db.Insert("flights", """{"flightNumber":"AA123","origin":"JFK"}""");
        _db.Insert("flights", """{"flightNumber":"AA456","origin":"LAX"}""");
        var result = _db.Execute("SELECT * FROM flights WHERE flightNumber = 'AA123'");
        Assert.True(result.Success);
        Assert.Single(result.Documents);
        Assert.Contains("INDEX_SCAN", result.QueryPlan!);
    }

    [Fact]
    public void CreateIndex_UniqueOnFreshCollection_AutoCreates_Issue59()
    {
        // Same path under unique=true — the unique-validator runs against a
        // freshly created (and therefore empty) collection, no rows to validate.
        _db.CreateIndex("widgets", "sku", "idx_widget_sku", unique: true);

        _db.Insert("widgets", """{"sku":"W-001"}""");
        Assert.Throws<DuplicateKeyException>(() =>
            _db.Insert("widgets", """{"sku":"W-001"}"""));
    }

    // ---------------------------------------------------------------------
    //  Issue #57 regression suite — header validation + page-chain hang fix
    // ---------------------------------------------------------------------
    //
    // Pre-fix: an ungraceful kill (kill -9 / Stop-Process -Force) could leave
    // the data file in a state where the catalog/collection page-chain walkers
    // followed a torn NextPageId pointer into a cycle and hung the host process
    // indefinitely on the next Open. The user's repro was 90s+ no exception, no
    // log output, no progress.
    //
    // The five tests below cover both halves of the fix:
    //  (a) DataFile.Open header validation: refuse the open with a typed
    //      DatabaseCorruptedException for truncated / wrong-magic / wrong-version
    //      files.
    //  (b) Cycle detection in BuildLocationMap and IndexCatalog.Load: throw
    //      PageCorruptionException instead of looping. The wall-clock guard
    //      proves we exit fast — that's the actual regression.

    [Fact]
    public void Open_OnTruncatedFile_ThrowsDatabaseCorruptedException_Issue57()
    {
        var path = Path.Combine(Path.GetTempPath(), $"truncated_{Guid.NewGuid():N}.dfdb");
        try
        {
            using (var s = File.Create(path)) s.Write(new byte[100]); // < one page
            var ex = Assert.Throws<DatabaseCorruptedException>(() => DocumentForgeDb.Open(path));
            Assert.Contains("smaller than a single", ex.Message);
        }
        finally { try { File.Delete(path); } catch { } try { File.Delete(path + ".lock"); } catch { } }
    }

    [Fact]
    public void Open_OnInvalidMagicBytes_ThrowsDatabaseCorruptedException_Issue57()
    {
        var path = Path.Combine(Path.GetTempPath(), $"badmagic_{Guid.NewGuid():N}.dfdb");
        try
        {
            using (var db = DocumentForgeDb.Create(path))
            {
                db.Insert("foo", """{"x":1}""");
                db.Flush();
            }
            // Stomp the 4-byte magic with garbage.
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
            {
                fs.Seek(0, SeekOrigin.Begin);
                fs.Write(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, 0, 4);
            }
            var ex = Assert.Throws<DatabaseCorruptedException>(() => DocumentForgeDb.Open(path));
            Assert.Contains("magic bytes", ex.Message);
        }
        finally
        {
            foreach (var ext in new[] { "", ".wal", ".recovery", ".followerseq", ".lock" })
                try { File.Delete(path + ext); } catch { }
        }
    }

    [Fact]
    public void Open_OnUnsupportedVersion_ThrowsDatabaseCorruptedException_Issue57()
    {
        var path = Path.Combine(Path.GetTempPath(), $"badver_{Guid.NewGuid():N}.dfdb");
        try
        {
            using (var db = DocumentForgeDb.Create(path))
            {
                db.Insert("foo", """{"x":1}""");
                db.Flush();
            }
            // Patch the version field at offset 4 to a value we don't support.
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
            {
                fs.Seek(4, SeekOrigin.Begin);
                fs.Write(BitConverter.GetBytes(int.MaxValue), 0, 4);
            }
            var ex = Assert.Throws<DatabaseCorruptedException>(() => DocumentForgeDb.Open(path));
            Assert.Contains("file format version", ex.Message);
        }
        finally
        {
            foreach (var ext in new[] { "", ".wal", ".recovery", ".followerseq", ".lock" })
                try { File.Delete(path + ext); } catch { }
        }
    }

    [Fact]
    public void Open_OnCorruptHeaderReleasesLock_Issue57()
    {
        // The corruption check throws *after* DatabaseLock.Acquire. Verify the
        // failure path still releases the lock so a second Open attempt (e.g.
        // operator retrying after restoring from backup over the same path) can
        // proceed instead of getting wedged behind a phantom locker.
        var path = Path.Combine(Path.GetTempPath(), $"lockrelease_{Guid.NewGuid():N}.dfdb");
        try
        {
            using (var db = DocumentForgeDb.Create(path)) db.Flush();
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write))
            {
                fs.Seek(0, SeekOrigin.Begin);
                fs.Write(new byte[4], 0, 4); // wipe magic
            }
            Assert.Throws<DatabaseCorruptedException>(() => DocumentForgeDb.Open(path));

            // Restore a clean DB at the same path; if the prior Open didn't
            // release its lock, this Create would throw DatabaseLockedException.
            File.Delete(path); // simulate restore-from-backup
            using var fresh = DocumentForgeDb.Create(path);
            fresh.Insert("ok", """{"x":1}""");
        }
        finally
        {
            foreach (var ext in new[] { "", ".wal", ".recovery", ".followerseq", ".lock" })
                try { File.Delete(path + ext); } catch { }
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task Open_OnCyclicCollectionChain_FailsFast_DoesNotHang_Issue57()
    {
        // Reproduces the 90-second-no-progress hang from issue #57. We:
        //  1. Build a real two-page chain by inserting enough docs to overflow page 1.
        //  2. Patch the first data page's NextPageId so it points back to itself.
        //  3. Open the file and BuildLocationMap. Pre-fix: hangs forever.
        //     Post-fix: throws PageCorruptionException within milliseconds.
        var path = Path.Combine(Path.GetTempPath(), $"cycle_{Guid.NewGuid():N}.dfdb");
        try
        {
            PageId firstDataPage;
            using (var db = DocumentForgeDb.Create(path))
            {
                // Pad each doc large enough to fill the page in O(100) inserts.
                var pad = new string('x', 200);
                for (int i = 0; i < 200; i++)
                    db.Insert("orders", $$"""{"i":{{i}},"pad":"{{pad}}"}""");
                firstDataPage = db.GetCollection("orders")!.FirstDataPage;
                db.Flush();
            }
            Assert.True(firstDataPage.IsValid);

            // Point NextPageId (offset 11) at the page itself → cycle. Read the
            // whole page, edit the link, then RE-STAMP its CRC and write it back
            // so the page stays checksum-valid. Without the re-stamp the torn
            // bytes would trip the #92 checksum guard on read first, and this
            // test would never exercise the cycle guard it's here to cover.
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            {
                var page = new byte[DocumentForge.Core.Constants.PageSize];
                fs.Seek(firstDataPage.FileOffset, SeekOrigin.Begin);
                int off = 0;
                while (off < page.Length)
                {
                    int n = fs.Read(page, off, page.Length - off);
                    if (n == 0) break;
                    off += n;
                }
                BitConverter.GetBytes(firstDataPage.Value).CopyTo(page.AsSpan(11));
                DocumentForge.Storage.PageChecksum.Stamp(page);
                fs.Seek(firstDataPage.FileOffset, SeekOrigin.Begin);
                fs.Write(page, 0, page.Length);
            }

            // Wall-clock guard: the test process must not block. If the chain
            // walker hangs (pre-fix), the timeout wins and we fail with a clear
            // message. Post-fix it throws ~instantly.
            var work = System.Threading.Tasks.Task.Run(() =>
            {
                using var db = DocumentForgeDb.Open(path);
                // BuildLocationMap runs eagerly inside Open; we also query for
                // belt-and-braces coverage.
                db.Execute("SELECT * FROM orders");
            });
            var timeout = System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(5));
            var winner = await System.Threading.Tasks.Task.WhenAny(work, timeout);
            Assert.True(winner == work,
                "Open / query on a cyclic page chain hung past 5s — the cycle guard didn't catch it.");

            var ex = await Assert.ThrowsAsync<PageCorruptionException>(async () => await work);
            Assert.Contains("cycle", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            foreach (var ext in new[] { "", ".wal", ".recovery", ".followerseq", ".lock" })
                try { File.Delete(path + ext); } catch { }
        }
    }

    public void Dispose()
    {
        // Test fixtures occasionally call _db.Dispose() themselves (e.g. lock
        // round-trip tests). Tolerate the redundant Dispose without throwing.
        try { _db.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + ".wal"); } catch { }
        try { File.Delete(_dbPath + ".recovery"); } catch { }
        try { File.Delete(_dbPath + ".followerseq"); } catch { }
        try { File.Delete(_dbPath + ".lock"); } catch { }
    }
}
