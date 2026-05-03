using System.Net;
using System.Net.Sockets;
using DocumentForge.Api;
using DocumentForge.Core;
using DocumentForge.Engine;
using DocumentForge.Engine.Cluster;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// End-to-end tests for the 2PC HTTP wire (issue #14 Phase E.1).
///
/// Each test boots a minimal Kestrel server on a free localhost port,
/// wires the shared <see cref="TransactionEndpoints.Map"/> against a
/// temp DocumentForgeDb, and drives <see cref="HttpShardTransport"/>
/// against it. Verifies the wire round-trips correctly — request/response
/// shapes, JSON serialization for ShardTxOp, status codes — without
/// mocking the server.
/// </summary>
public class HttpShardTransportTests : IDisposable
{
    private readonly List<IDisposable> _disposables = new();
    private readonly List<string> _paths = new();

    private (DocumentForgeDb db, WebApplication app, HttpShardTransport transport, string baseUrl)
        BootServer(string shardName = "HTTP")
    {
        int port = FindFreePort();
        var path = Path.Combine(Path.GetTempPath(), $"http_{Guid.NewGuid():N}.dfdb");
        _paths.Add(path);

        var db = DocumentForgeDb.Create(path);
        _disposables.Add(db);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();   // keep test output clean
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        TransactionEndpoints.Map(app, db);

        // Run the host on a background task; it's stopped via app.StopAsync in Dispose.
        app.StartAsync().GetAwaiter().GetResult();
        _disposables.Add(new HostStopper(app));

        var baseUrl = $"http://127.0.0.1:{port}";
        var transport = new HttpShardTransport(shardName, baseUrl);
        _disposables.Add(transport);

        return (db, app, transport, baseUrl);
    }

    private static int FindFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        // Stop the WebApplications first (HostStopper), then dispose DBs, in
        // reverse order of creation. Best-effort — tests should be robust to
        // partial shutdown.
        for (int i = _disposables.Count - 1; i >= 0; i--)
        {
            try { _disposables[i].Dispose(); } catch { }
        }
        foreach (var p in _paths)
        {
            try { File.Delete(p); File.Delete(p + ".wal"); File.Delete(p + ".recovery"); File.Delete(p + ".prepared.log"); File.Delete(p + ".coord.log"); } catch { }
        }
    }

    // --- Tests ---

    [Fact]
    public void ExecuteTransaction_OverHttp_AppliesOps()
    {
        var (db, _, transport, _) = BootServer();

        var ops = new List<ShardTxOp>
        {
            ShardTxOp.ForInsert("orders", DocumentForge.Document.BsonDocument.FromJson("""{"pnr":"H1","leg":1}""")),
            ShardTxOp.ForInsert("orders", DocumentForge.Document.BsonDocument.FromJson("""{"pnr":"H1","leg":2}""")),
        };

        transport.ExecuteTransaction(ops);

        Assert.Equal(2, db.Execute("SELECT * FROM orders WHERE pnr = 'H1'").Documents.Count);
    }

    [Fact]
    public void Prepare_Commit_OverHttp_AppliesOps()
    {
        var (db, _, transport, _) = BootServer();

        var ops = new List<ShardTxOp>
        {
            ShardTxOp.ForInsert("orders", DocumentForge.Document.BsonDocument.FromJson("""{"pnr":"PREPARED"}""")),
        };

        var result = transport.Prepare("tx-http-1", "self", ops, TimeSpan.FromSeconds(30));
        Assert.Equal(PrepareVote.Prepared, result.Vote);

        transport.CommitPrepared("tx-http-1");

        Assert.Single(db.Execute("SELECT * FROM orders WHERE pnr = 'PREPARED'").Documents);
    }

    [Fact]
    public void Prepare_Rollback_OverHttp_DiscardsOps()
    {
        var (db, _, transport, _) = BootServer();

        var ops = new List<ShardTxOp>
        {
            ShardTxOp.ForInsert("orders", DocumentForge.Document.BsonDocument.FromJson("""{"pnr":"ROLLED-BACK"}""")),
        };

        Assert.Equal(PrepareVote.Prepared, transport.Prepare("tx-http-rb", "self", ops, TimeSpan.FromSeconds(30)).Vote);
        transport.RollbackPrepared("tx-http-rb");

        Assert.Empty(db.Execute("SELECT * FROM orders").Documents);
    }

    [Fact]
    public void Prepare_Replace_OverHttp_RoundTripsDocId()
    {
        // Replace serialization carries a 16-byte DocumentId — confirms the
        // hex-string round-trip works.
        var (db, _, transport, _) = BootServer();

        var insertedId = db.Insert("orders", """{"pnr":"BASE","seat":"12A"}""");

        var ops = new List<ShardTxOp>
        {
            ShardTxOp.ForReplace("orders", insertedId,
                DocumentForge.Document.BsonDocument.FromJson("""{"pnr":"BASE","seat":"99Z"}""")),
        };

        Assert.Equal(PrepareVote.Prepared, transport.Prepare("tx-http-rep", "self", ops, TimeSpan.FromSeconds(30)).Vote);
        transport.CommitPrepared("tx-http-rep");

        var rows = db.Execute("SELECT * FROM orders WHERE pnr = 'BASE'").Documents;
        Assert.Single(rows);
        Assert.Equal("99Z", rows[0]["seat"].AsString);
    }

    [Fact]
    public void CoordinatorDecisionAndState_OverHttp_RoundTrips()
    {
        var (_, _, transport, _) = BootServer();

        // Unknown tx returns null (404 on the wire).
        Assert.Null(transport.GetCoordinatorTxState("ghost"));

        // Decide + done — and verify the state transitions.
        transport.RecordCoordinatorDecision("tx-c", commit: true);
        var afterDecision = transport.GetCoordinatorTxState("tx-c");
        Assert.NotNull(afterDecision);
        Assert.True(afterDecision!.Decided);
        Assert.False(afterDecision.Done);

        transport.RecordCoordinatorDone("tx-c");
        var afterDone = transport.GetCoordinatorTxState("tx-c");
        Assert.NotNull(afterDone);
        Assert.True(afterDone!.Decided);
        Assert.True(afterDone.Done);
    }

    [Fact]
    public void ScanInFlightPrepared_OverHttp_ReturnsPreparedRecord()
    {
        var (_, _, transport, _) = BootServer();

        // Empty initially.
        Assert.Empty(transport.ScanInFlightPrepared());

        var ops = new List<ShardTxOp>
        {
            ShardTxOp.ForInsert("orders", DocumentForge.Document.BsonDocument.FromJson("""{"pnr":"SCAN"}""")),
        };
        Assert.Equal(PrepareVote.Prepared, transport.Prepare("tx-scan", "coord-shard", ops, TimeSpan.FromSeconds(30)).Vote);

        var inFlight = transport.ScanInFlightPrepared();
        Assert.Single(inFlight);
        Assert.Equal("tx-scan", inFlight[0].TxId);
        Assert.Equal("coord-shard", inFlight[0].CoordinatorShardId);
        Assert.Single(inFlight[0].Ops);
        Assert.Equal(ShardTxOpKind.Insert, inFlight[0].Ops[0].Kind);

        // Resolve so the test's Dispose can clean up the prepared.log.
        transport.RollbackPrepared("tx-scan");
        Assert.Empty(transport.ScanInFlightPrepared());
    }

    [Fact]
    public void EndToEnd_MultiShardClusterCommit_OverHttp()
    {
        // Two HTTP-fronted shards; cluster.BeginTransaction drives 2PC over
        // the wire end-to-end. Each shard gets a unique name (the ring
        // would dedupe identical names).
        var first = BootServer("HTTP-A");
        var second = BootServer("HTTP-B");

        using var cluster = new DocumentForgeCluster()
            .AddShard(first.transport)
            .AddShard(second.transport)
            .ShardCollection("orders", "pnr");

        // Find two pnrs that route to distinct shards by computing the
        // ring directly — we can't probe via cluster.Insert because the
        // mini-server doesn't expose /collections (only the 2PC endpoints).
        var ring = new ConsistentHashRing(new[] { "HTTP-A", "HTTP-B" }, 150);
        string? pA = null, pB = null;
        for (int i = 0; pA is null || pB is null; i++)
        {
            if (i > 200) throw new InvalidOperationException("Couldn't find two pnrs on distinct shards");
            var pnr = $"PROBE{i:D4}";
            int idx = ring.PickShardIndex(pnr);
            if (idx == 0 && pA is null) pA = pnr;
            else if (idx == 1 && pB is null) pB = pnr;
        }

        using (var tx = cluster.BeginTransaction())
        {
            tx.Insert("orders", $$"""{"pnr":"{{pA}}","leg":1}""");
            tx.Insert("orders", $$"""{"pnr":"{{pB}}","leg":2}""");
            Assert.Equal(2, tx.ParticipantCount);
            tx.Commit();
        }

        // Both shards now have their slice.
        var totalA = first.db.Execute("SELECT * FROM orders").Documents.Count;
        var totalB = second.db.Execute("SELECT * FROM orders").Documents.Count;
        Assert.Equal(1, totalA);
        Assert.Equal(1, totalB);
    }

    // --- Phase E.2: operator endpoints + metrics ---

    [Fact]
    public void Stats_TrackPrepareCommitRollbackTransitions()
    {
        // Direct C# API (no HTTP) — quickest way to assert the counters
        // increment on each transition without wire overhead.
        var path = Path.Combine(Path.GetTempPath(), $"stats_{Guid.NewGuid():N}.dfdb");
        try
        {
            using var db = DocumentForgeDb.Create(path);
            var ops = new List<ShardTxOp>
            {
                ShardTxOp.ForInsert("orders", DocumentForge.Document.BsonDocument.FromJson("""{"pnr":"S1"}""")),
            };

            // Initial counters all zero.
            var s0 = db.GetPreparedTxStats();
            Assert.Equal(0, s0.PrepareTotal);
            Assert.Equal(0, s0.CommittedTotal);

            // Prepare + Commit cycle.
            db.PrepareTransaction("tx-s1", "self", ops, TimeSpan.FromSeconds(30));
            db.CommitPreparedTransaction("tx-s1");

            var s1 = db.GetPreparedTxStats();
            Assert.Equal(1, s1.PrepareTotal);
            Assert.Equal(1, s1.CommittedTotal);
            Assert.Equal(0, s1.RolledBackTotal);
            Assert.Equal(0, s1.InFlightPrepared);

            // Prepare + Rollback cycle.
            db.PrepareTransaction("tx-s2", "self", ops, TimeSpan.FromSeconds(30));
            db.RollbackPreparedTransaction("tx-s2");

            var s2 = db.GetPreparedTxStats();
            Assert.Equal(2, s2.PrepareTotal);
            Assert.Equal(1, s2.CommittedTotal);
            Assert.Equal(1, s2.RolledBackTotal);
            Assert.Equal(0, s2.InFlightPrepared);
        }
        finally
        {
            try { File.Delete(path); File.Delete(path + ".wal"); File.Delete(path + ".recovery"); File.Delete(path + ".prepared.log"); } catch { }
        }
    }

    [Fact]
    public void Stats_TrackTimedOutAbort()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stats_{Guid.NewGuid():N}.dfdb");
        try
        {
            using var db = DocumentForgeDb.Create(path);
            var ops = new List<ShardTxOp>
            {
                ShardTxOp.ForInsert("orders", DocumentForge.Document.BsonDocument.FromJson("""{"pnr":"T1"}""")),
            };

            db.PrepareTransaction("tx-timeout-stat", "self", ops, TimeSpan.FromMilliseconds(100));
            Thread.Sleep(500);  // let the timeout fire

            var stats = db.GetPreparedTxStats();
            Assert.Equal(1, stats.PrepareTotal);
            Assert.Equal(1, stats.TimedOutTotal);
            Assert.Equal(0, stats.CommittedTotal);
            Assert.Equal(0, stats.InFlightPrepared);
        }
        finally
        {
            try { File.Delete(path); File.Delete(path + ".wal"); File.Delete(path + ".recovery"); File.Delete(path + ".prepared.log"); } catch { }
        }
    }

    [Fact]
    public void OperatorAbort_OverHttp_FreesLock()
    {
        // Operator hits POST /tx/{id}/abort to force-resolve a stuck
        // PREPARED tx. After the abort, the participant must accept new
        // PREPAREs again.
        var (db, _, transport, _) = BootServer();

        var ops = new List<ShardTxOp>
        {
            ShardTxOp.ForInsert("orders", DocumentForge.Document.BsonDocument.FromJson("""{"pnr":"STUCK"}""")),
        };

        Assert.Equal(PrepareVote.Prepared, transport.Prepare("tx-stuck", "self", ops, TimeSpan.FromSeconds(30)).Vote);

        // Operator force-aborts.
        transport.OperatorAbort("tx-stuck");

        // Lock released — a new Prepare succeeds and commits.
        Assert.Equal(PrepareVote.Prepared, transport.Prepare("tx-after-abort", "self", ops, TimeSpan.FromSeconds(30)).Vote);
        transport.CommitPrepared("tx-after-abort");

        // The aborted tx never landed; only the post-abort one did.
        var rows = db.Execute("SELECT * FROM orders").Documents;
        Assert.Single(rows);
    }

    [Fact]
    public void OperatorAbort_RefusedWhenCoordinatorDecided()
    {
        // Safety guard: if THIS shard recorded COMMIT_DECISION for the tx
        // (it's the coordinator and decided), refuse the abort. Aborting
        // after a decision would diverge from what the cluster committed.
        var (_, _, transport, _) = BootServer();

        // Simulate "this shard is the coordinator and decided COMMIT" by
        // calling RecordCoordinatorDecision directly.
        transport.RecordCoordinatorDecision("tx-decided", commit: true);

        var ex = Assert.Throws<DocumentForgeException>(() => transport.OperatorAbort("tx-decided"));
        Assert.Contains("COMMIT_DECISION", ex.Message);
    }

    [Fact]
    public void Stats_OverHttp_RoundTrips()
    {
        var (_, _, transport, _) = BootServer();

        var initial = transport.GetStats();
        Assert.Equal(0, initial.PrepareTotal);

        var ops = new List<ShardTxOp>
        {
            ShardTxOp.ForInsert("orders", DocumentForge.Document.BsonDocument.FromJson("""{"pnr":"H-STAT"}""")),
        };

        Assert.Equal(PrepareVote.Prepared, transport.Prepare("tx-http-stat", "self", ops, TimeSpan.FromSeconds(30)).Vote);
        transport.CommitPrepared("tx-http-stat");

        var after = transport.GetStats();
        Assert.Equal(1, after.PrepareTotal);
        Assert.Equal(1, after.CommittedTotal);
        Assert.Equal(0, after.InFlightPrepared);
    }

    private sealed class HostStopper : IDisposable
    {
        private readonly IHost _host;
        public HostStopper(IHost host) { _host = host; }
        public void Dispose()
        {
            try { _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); } catch { }
            try { (_host as IDisposable)?.Dispose(); } catch { }
        }
    }
}
