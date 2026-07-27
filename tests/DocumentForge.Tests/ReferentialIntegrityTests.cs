using DocumentForge.Core;
using DocumentForge.Document;
using DocumentForge.Engine;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// Issue #151 — referential integrity via the schema 'refs' section. Inserts
/// and updates of a constrained field must point at an existing document;
/// deletes of referenced documents honour restrict / setNull / cascade,
/// resolved plan-then-apply so a refusal never leaves a partial cascade.
/// Also covers the delete wire-format fix: replicated deletes carry the
/// 16-byte id the follower actually parses.
/// </summary>
public sealed class ReferentialIntegrityTests : IDisposable
{
    private readonly string _dbPath;
    private DocumentForgeDb _db;

    public ReferentialIntegrityTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ri_{Guid.NewGuid():N}.dfdb");
        _db = DocumentForgeDb.Create(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var ext in new[] { "", ".wal", ".recovery", ".lock", ".followerseq", ".term" })
            try { File.Delete(_dbPath + ext); } catch { }
    }

    private static CollectionSchema RefSchema(string collection, params RefConstraint[] refs) =>
        new(collection,
            Array.Empty<string>(),
            new Dictionary<string, FieldTypeConstraint>(),
            Array.Empty<UpdateCondition>(),
            refs);

    private DocumentId InsertCustomer(string name = "acme")
        => _db.Insert("customers", $$"""{"name":"{{name}}"}""");

    // ---- insert/update-side enforcement ----

    [Fact]
    public void Insert_DanglingRef_Rejected_ValidRef_Accepted()
    {
        _db.ConfigureSchema(RefSchema("orders",
            new RefConstraint("customerId", "customers", "_id", OnDeleteAction.Restrict)));

        // Dangling: no customers at all yet.
        Assert.Throws<SchemaViolationException>(() =>
            _db.Insert("orders", $$"""{"customerId":"{{Guid.NewGuid():N}}"}"""));

        var custId = InsertCustomer();
        var orderId = _db.Insert("orders", $$"""{"customerId":"{{custId}}"}""");
        Assert.NotNull(_db.GetCollection("orders")!.FindById(orderId));
    }

    [Fact]
    public void Insert_AbsentOrNullRefField_Allowed()
    {
        // Absence is a 'required' concern, not a ref concern.
        _db.ConfigureSchema(RefSchema("orders",
            new RefConstraint("customerId", "customers", "_id", OnDeleteAction.Restrict)));

        Assert.NotEqual(default, _db.Insert("orders", """{"note":"walk-in"}"""));
        Assert.NotEqual(default, _db.Insert("orders", """{"customerId":null}"""));
    }

    [Fact]
    public void Replace_DanglingRef_Rejected()
    {
        _db.ConfigureSchema(RefSchema("orders",
            new RefConstraint("customerId", "customers", "_id", OnDeleteAction.Restrict)));
        var custId = InsertCustomer();
        var orderId = _db.Insert("orders", $$"""{"customerId":"{{custId}}"}""");

        Assert.Throws<SchemaViolationException>(() =>
            _db.Replace("orders", orderId, $$"""{"customerId":"{{Guid.NewGuid():N}}"}"""));
    }

    [Fact]
    public void Insert_BusinessKeyRef_TargetFieldNotId()
    {
        _db.ConfigureSchema(RefSchema("orders",
            new RefConstraint("sku", "products", "code", OnDeleteAction.Restrict)));
        _db.Insert("products", """{"code":"WIDGET-1"}""");

        Assert.Throws<SchemaViolationException>(() =>
            _db.Insert("orders", """{"sku":"NOPE-9"}"""));
        Assert.NotEqual(default, _db.Insert("orders", """{"sku":"WIDGET-1"}"""));
    }

    // ---- delete-side enforcement ----

    [Fact]
    public void Delete_Restrict_Blocked_BothDocsSurvive()
    {
        _db.ConfigureSchema(RefSchema("orders",
            new RefConstraint("customerId", "customers", "_id", OnDeleteAction.Restrict)));
        var custId = InsertCustomer();
        var orderId = _db.Insert("orders", $$"""{"customerId":"{{custId}}"}""");

        Assert.Throws<ReferentialIntegrityException>(() => _db.Delete("customers", custId));
        Assert.NotNull(_db.GetCollection("customers")!.FindById(custId));
        Assert.NotNull(_db.GetCollection("orders")!.FindById(orderId));

        // Remove the referencing doc → the parent delete goes through.
        Assert.True(_db.Delete("orders", orderId));
        Assert.True(_db.Delete("customers", custId));
        Assert.Null(_db.GetCollection("customers")!.FindById(custId));
    }

    [Fact]
    public void Delete_Cascade_RemovesChildren_Recursively()
    {
        // customers ← orders (cascade) ← items (cascade): deleting the
        // customer must take the whole subtree.
        _db.ConfigureSchema(RefSchema("orders",
            new RefConstraint("customerId", "customers", "_id", OnDeleteAction.Cascade)));
        _db.ConfigureSchema(RefSchema("items",
            new RefConstraint("orderId", "orders", "_id", OnDeleteAction.Cascade)));

        var custId = InsertCustomer();
        var orderId = _db.Insert("orders", $$"""{"customerId":"{{custId}}"}""");
        var itemId = _db.Insert("items", $$"""{"orderId":"{{orderId}}"}""");
        var unrelatedOrder = _db.Insert("orders", """{"note":"no customer"}""");

        Assert.True(_db.Delete("customers", custId));
        Assert.Null(_db.GetCollection("customers")!.FindById(custId));
        Assert.Null(_db.GetCollection("orders")!.FindById(orderId));
        Assert.Null(_db.GetCollection("items")!.FindById(itemId));
        // Docs outside the graph are untouched.
        Assert.NotNull(_db.GetCollection("orders")!.FindById(unrelatedOrder));
    }

    [Fact]
    public void Delete_SetNull_NullsChildField_ChildSurvives()
    {
        _db.ConfigureSchema(RefSchema("orders",
            new RefConstraint("customerId", "customers", "_id", OnDeleteAction.SetNull)));
        var custId = InsertCustomer();
        var orderId = _db.Insert("orders", $$"""{"customerId":"{{custId}}","note":"keep me"}""");

        Assert.True(_db.Delete("customers", custId));
        var order = _db.GetCollection("orders")!.FindById(orderId);
        Assert.NotNull(order);
        Assert.True(order!["customerId"].IsNull);
        Assert.Equal("keep me", order["note"].AsString);
    }

    [Fact]
    public void Delete_CascadeCycle_Terminates()
    {
        // a ←cascade– b and b ←cascade– a. Build the cycle with a second
        // write (both docs must exist before the refs can point at each other).
        _db.ConfigureSchema(RefSchema("a", new RefConstraint("bRef", "b", "_id", OnDeleteAction.Cascade)));
        _db.ConfigureSchema(RefSchema("b", new RefConstraint("aRef", "a", "_id", OnDeleteAction.Cascade)));

        var aId = _db.Insert("a", """{"tag":"a1"}""");
        var bId = _db.Insert("b", $$"""{"aRef":"{{aId}}"}""");
        Assert.True(_db.Replace("a", aId, $$"""{"tag":"a1","bRef":"{{bId}}"}"""));

        Assert.True(_db.Delete("a", aId)); // must not stack-overflow / hang
        Assert.Null(_db.GetCollection("a")!.FindById(aId));
        Assert.Null(_db.GetCollection("b")!.FindById(bId));
    }

    [Fact]
    public void Delete_NoSchema_PlainDeleteStillWorks()
    {
        var id = _db.Insert("scratch", """{"x":1}""");
        Assert.True(_db.Delete("scratch", id));
        Assert.Null(_db.GetCollection("scratch")!.FindById(id));
        Assert.False(_db.Delete("scratch", id));          // already gone
        Assert.False(_db.Delete("ghost_collection", id)); // no such collection
    }

    // ---- schema config validation + persistence ----

    [Fact]
    public void ConfigureSchema_SetNullOnRequiredField_Rejected()
    {
        var schema = new CollectionSchema("orders",
            new[] { "customerId" },
            new Dictionary<string, FieldTypeConstraint>(),
            Array.Empty<UpdateCondition>(),
            new[] { new RefConstraint("customerId", "customers", "_id", OnDeleteAction.SetNull) });
        Assert.Throws<ArgumentException>(() => _db.ConfigureSchema(schema));
    }

    [Fact]
    public void Refs_PersistAcrossReopen()
    {
        _db.ConfigureSchema(RefSchema("orders",
            new RefConstraint("customerId", "customers", "_id", OnDeleteAction.Cascade)));
        var custId = InsertCustomer();
        _db.Insert("orders", $$"""{"customerId":"{{custId}}"}""");

        _db.Dispose();
        _db = DocumentForgeDb.Open(_dbPath);

        var schema = _db.GetSchema("orders");
        Assert.NotNull(schema);
        var rf = Assert.Single(schema!.RefsOrEmpty);
        Assert.Equal("customerId", rf.Field);
        Assert.Equal("customers", rf.Collection);
        Assert.Equal("_id", rf.TargetField);
        Assert.Equal(OnDeleteAction.Cascade, rf.OnDelete);

        // And it still enforces: dangling insert rejected, cascade delete works.
        Assert.Throws<SchemaViolationException>(() =>
            _db.Insert("orders", $$"""{"customerId":"{{Guid.NewGuid():N}}"}"""));
        Assert.True(_db.Delete("customers", custId));
        Assert.Empty(_db.Execute("SELECT * FROM orders").Documents);
    }

    // ---- replication: deletes must actually land on the follower ----

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task Delete_ReplicatesToFollower_With16ByteIdPayload()
    {
        // Guards the wire-format fix: Delete broadcasts the 16-byte id
        // (what ApplyFollowerOp parses), not a serialized document.
        var leaderPath = Path.Combine(Path.GetTempPath(), $"ri_leader_{Guid.NewGuid():N}.dfdb");
        var followerPath = Path.Combine(Path.GetTempPath(), $"ri_follower_{Guid.NewGuid():N}.dfdb");
        int port = FreePort();
        try
        {
            using var leader = DocumentForgeDb.Create(leaderPath);
            leader.StartLogicalReplicationServer(port);
            using var follower = DocumentForgeDb.Create(followerPath);
            follower.StartLogicalReplicationFollower("localhost", port);
            for (int i = 0; i < 30 && leader.GetLogicalFollowerCount() == 0; i++)
                await Task.Delay(100);

            var id = leader.Insert("orders", """{"pnr":"DEL-1"}""");
            Assert.True(leader.Delete("orders", id));

            // Follower applies insert then delete → ends up empty.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            bool gone = false;
            while (DateTime.UtcNow < deadline)
            {
                var rows = follower.Execute("SELECT * FROM orders").Documents;
                // Insert must have arrived first (OpsApplied >= 2 proves both landed).
                if (follower.LogicallyReplicatedOps() >= 2 && rows.Count == 0) { gone = true; break; }
                await Task.Delay(100);
            }
            Assert.True(gone, "Follower never applied the replicated delete.");
        }
        finally
        {
            foreach (var p in new[] { leaderPath, followerPath })
                foreach (var ext in new[] { "", ".wal", ".recovery", ".lock", ".followerseq", ".term",
                                            ".snapshot.incoming", ".snapshot.incoming.seq" })
                    try { File.Delete(p + ext); } catch { }
        }
    }

    [Fact]
    public async Task Replace_ReplicatesToFollower_NoGhostDuplicate()
    {
        // Pre-fix, Replace broadcast its delete-half as a full serialized doc;
        // the follower's DocumentId.FromBytes threw, the delete was dropped,
        // and the follower kept BOTH versions. Post-fix it must hold exactly one.
        var leaderPath = Path.Combine(Path.GetTempPath(), $"ri_rleader_{Guid.NewGuid():N}.dfdb");
        var followerPath = Path.Combine(Path.GetTempPath(), $"ri_rfollower_{Guid.NewGuid():N}.dfdb");
        int port = FreePort();
        try
        {
            using var leader = DocumentForgeDb.Create(leaderPath);
            leader.StartLogicalReplicationServer(port);
            using var follower = DocumentForgeDb.Create(followerPath);
            follower.StartLogicalReplicationFollower("localhost", port);
            for (int i = 0; i < 30 && leader.GetLogicalFollowerCount() == 0; i++)
                await Task.Delay(100);

            var id = leader.Insert("orders", """{"pnr":"OLD"}""");
            Assert.True(leader.Replace("orders", id, """{"pnr":"NEW"}"""));

            var deadline = DateTime.UtcNow.AddSeconds(10);
            List<BsonDocument> rows = new();
            while (DateTime.UtcNow < deadline)
            {
                rows = follower.Execute("SELECT * FROM orders").Documents.ToList();
                if (follower.LogicallyReplicatedOps() >= 3 && rows.Count == 1) break;
                await Task.Delay(100);
            }
            var row = Assert.Single(rows);
            Assert.Equal("NEW", row["pnr"].AsString);
        }
        finally
        {
            foreach (var p in new[] { leaderPath, followerPath })
                foreach (var ext in new[] { "", ".wal", ".recovery", ".lock", ".followerseq", ".term",
                                            ".snapshot.incoming", ".snapshot.incoming.seq" })
                    try { File.Delete(p + ext); } catch { }
        }
    }
}
