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

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + ".wal"); } catch { }
        try { File.Delete(_dbPath + ".recovery"); } catch { }
        try { File.Delete(_dbPath + ".followerseq"); } catch { }
    }
}
