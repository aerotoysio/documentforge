using System.Net;
using System.Net.Sockets;
using DocumentForge.Api;
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
