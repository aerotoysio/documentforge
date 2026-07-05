using DocumentForge.Core;
using DocumentForge.Document;
using DocumentForge.Engine;
using DocumentForge.Engine.Cluster;
using DocumentForge.Query;
using DocumentForge.Transactions;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// Issue #98 — the 2PC commit decision must be quorum-durable, not pinned to a
/// single coordinator shard's disk. These tests prove the specific audit
/// failure is fixed: a committed cross-shard transaction survives the loss of
/// the original (lowest-index) coordinator shard's decision log.
/// </summary>
public sealed class ClusterCoordinatorQuorumTests
{
    private static readonly List<ShardTxOp> OneInsert = new()
    {
        ShardTxOp.ForInsert("orders", BsonDocument.FromJson("""{"pnr":"QRM","leg":1}""")),
    };

    private static void Cleanup(params string[] paths)
    {
        foreach (var p in paths)
            foreach (var ext in new[] { "", ".wal", ".recovery", ".lock", ".prepared.log", ".coord.log", ".term" })
                try { File.Delete(p + ext); } catch { }
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    [InlineData(5, 3)]
    public void Majority_IsStrictMajority(int n, int expected)
        => Assert.Equal(expected, DocumentForgeCluster.Majority(n));

    [Fact]
    public void Recover_DecisionOnQuorumButNotNamedCoordinator_Commits()
    {
        // THE #98 fix. Three shards. A participant (A) is PREPARED and names A
        // as its coordinator — but A's coord.log has NO decision (its disk was
        // lost). The decision was made quorum-durable on B and C. Recovery must
        // still COMMIT, because a majority read finds the decision on B/C.
        // Under the old single-coordinator design this rolled back a committed
        // booking — the exact bug the audit reported.
        var pA = Path.Combine(Path.GetTempPath(), $"q_{Guid.NewGuid():N}.dfdb");
        var pB = Path.Combine(Path.GetTempPath(), $"q_{Guid.NewGuid():N}.dfdb");
        var pC = Path.Combine(Path.GetTempPath(), $"q_{Guid.NewGuid():N}.dfdb");
        try
        {
            const string txId = "tx-quorum-commit";
            using (var dbA = DocumentForgeDb.Create(pA))
            using (var dbB = DocumentForgeDb.Create(pB))
            using (var dbC = DocumentForgeDb.Create(pC))
            {
                Assert.Equal(PrepareVote.Prepared, dbA.PrepareTransaction(txId, "A", OneInsert).Vote);
                // Decision is quorum-durable on B and C (2 of 3) — but NOT on the
                // named coordinator A, whose disk we treat as lost.
                dbB.RecordCoordinatorDecision(txId, commit: true);
                dbC.RecordCoordinatorDecision(txId, commit: true);
            }

            using var dbA2 = DocumentForgeDb.Open(pA);
            using var dbB2 = DocumentForgeDb.Open(pB);
            using var dbC2 = DocumentForgeDb.Open(pC);
            using var cluster = new DocumentForgeCluster()
                .AddShard(new InProcessShardTransport("A", dbA2))
                .AddShard(new InProcessShardTransport("B", dbB2))
                .AddShard(new InProcessShardTransport("C", dbC2))
                .ShardCollection("orders", "pnr");

            var summary = cluster.Recover();
            Assert.Equal(1, summary.Committed);
            Assert.Equal(0, summary.Aborted);
            Assert.Equal(0, summary.Skipped);
            Assert.Single(dbA2.Execute("SELECT * FROM orders WHERE pnr = 'QRM'").Documents);
        }
        finally { Cleanup(pA, pB, pC); }
    }

    [Fact]
    public void Recover_NoDecisionAnywhere_FullView_Aborts()
    {
        // No shard holds a decision and the whole cluster (incl. the named
        // coordinator) is reachable → a majority read confirms no decision was
        // ever made → safe ROLLBACK, and the staged insert is NOT applied.
        var pA = Path.Combine(Path.GetTempPath(), $"q_{Guid.NewGuid():N}.dfdb");
        var pB = Path.Combine(Path.GetTempPath(), $"q_{Guid.NewGuid():N}.dfdb");
        var pC = Path.Combine(Path.GetTempPath(), $"q_{Guid.NewGuid():N}.dfdb");
        try
        {
            const string txId = "tx-quorum-abort";
            using (var dbA = DocumentForgeDb.Create(pA))
            using (var _ = DocumentForgeDb.Create(pB))
            using (var __ = DocumentForgeDb.Create(pC))
            {
                Assert.Equal(PrepareVote.Prepared, dbA.PrepareTransaction(txId, "A", OneInsert).Vote);
                // No RecordCoordinatorDecision anywhere.
            }

            using var dbA2 = DocumentForgeDb.Open(pA);
            using var dbB2 = DocumentForgeDb.Open(pB);
            using var dbC2 = DocumentForgeDb.Open(pC);
            using var cluster = new DocumentForgeCluster()
                .AddShard(new InProcessShardTransport("A", dbA2))
                .AddShard(new InProcessShardTransport("B", dbB2))
                .AddShard(new InProcessShardTransport("C", dbC2))
                .ShardCollection("orders", "pnr");

            var summary = cluster.Recover();
            Assert.Equal(0, summary.Committed);
            Assert.Equal(1, summary.Aborted);
            Assert.Empty(dbA2.Execute("SELECT * FROM orders WHERE pnr = 'QRM'").Documents);
        }
        finally { Cleanup(pA, pB, pC); }
    }

    [Fact]
    public void RecordCommitDecisionQuorum_ReportsDurabilityByMajority()
    {
        // Direct check of the write-side quorum: with 2 of 3 shards failing to
        // record the decision, only 1 acks → NOT durable; with at most 1
        // failing, a majority acks → durable.
        var pA = Path.Combine(Path.GetTempPath(), $"q_{Guid.NewGuid():N}.dfdb");
        var pB = Path.Combine(Path.GetTempPath(), $"q_{Guid.NewGuid():N}.dfdb");
        var pC = Path.Combine(Path.GetTempPath(), $"q_{Guid.NewGuid():N}.dfdb");
        try
        {
            using var dbA = DocumentForgeDb.Create(pA);
            using var dbB = DocumentForgeDb.Create(pB);
            using var dbC = DocumentForgeDb.Create(pC);
            var fA = new FaultyDecisionTransport("A", dbA);
            var fB = new FaultyDecisionTransport("B", dbB);
            var fC = new FaultyDecisionTransport("C", dbC);
            using var cluster = new DocumentForgeCluster()
                .AddShard(fA).AddShard(fB).AddShard(fC)
                .ShardCollection("orders", "pnr");

            // Two shards can't persist the decision → minority ack.
            fB.FailDecision = true;
            fC.FailDecision = true;
            var r1 = cluster.RecordCommitDecisionQuorum("tx1");
            Assert.Equal(1, r1.Acks);
            Assert.False(r1.Durable);

            // Only one shard down → majority ack.
            fC.FailDecision = false;
            var r2 = cluster.RecordCommitDecisionQuorum("tx2");
            Assert.Equal(2, r2.Acks);
            Assert.True(r2.Durable);
        }
        finally { Cleanup(pA, pB, pC); }
    }

    [Fact]
    public void CrossShardCommit_WhenDecisionQuorumUnreachable_AbortsWithoutPartialCommit()
    {
        // End-to-end write-path safety: a cross-shard tx PREPAREs fine, but the
        // decision can't be made durable on a majority (2 of 3 shards fail to
        // record it). The commit must throw and leave NO participant applied —
        // never a partial commit when the decision isn't durable.
        var paths = Enumerable.Range(0, 3)
            .Select(_ => Path.Combine(Path.GetTempPath(), $"q_{Guid.NewGuid():N}.dfdb")).ToArray();
        var dbs = paths.Select(p => DocumentForgeDb.Create(p)).ToArray();
        var faults = new[]
        {
            new FaultyDecisionTransport("A", dbs[0]),
            new FaultyDecisionTransport("B", dbs[1]),
            new FaultyDecisionTransport("C", dbs[2]),
        };
        var cluster = new DocumentForgeCluster();
        foreach (var f in faults) cluster.AddShard(f);
        cluster.ShardCollection("orders", "pnr");
        try
        {
            // Find two pnrs that route to distinct shards.
            string? pnrA = null, pnrB = null; int aShard = -1, bShard = -1;
            for (int i = 0; (pnrA is null || pnrB is null) && i < 300; i++)
            {
                var pnr = $"PROBE{i:D4}";
                cluster.Insert("orders", $$"""{"pnr":"{{pnr}}"}""");
                int found = -1;
                for (int s = 0; s < dbs.Length; s++)
                    if (dbs[s].Execute($"SELECT * FROM orders WHERE pnr = '{pnr}'").Documents.Count == 1) found = s;
                if (pnrA is null) { pnrA = pnr; aShard = found; }
                else if (found != aShard) { pnrB = pnr; bShard = found; }
            }
            Assert.NotNull(pnrB);
            cluster.Execute("DELETE FROM orders");

            // Arm a majority of shards to fail the decision write.
            faults[0].FailDecision = true;
            faults[1].FailDecision = true;
            faults[2].FailDecision = true;

            using (var tx = cluster.BeginTransaction())
            {
                tx.Insert("orders", $$"""{"pnr":"{{pnrA}}"}""");
                tx.Insert("orders", $$"""{"pnr":"{{pnrB}}"}""");
                var ex = Assert.Throws<TransactionException>(() => tx.Commit());
                Assert.Contains("majority", ex.Message);
            }

            // Critically: the decision was never durable, so NOTHING committed.
            Assert.Empty(cluster.Execute($"SELECT * FROM orders WHERE pnr = '{pnrA}'").Documents);
            Assert.Empty(cluster.Execute($"SELECT * FROM orders WHERE pnr = '{pnrB}'").Documents);
        }
        finally
        {
            cluster.Dispose();
            foreach (var d in dbs) try { d.Dispose(); } catch { }
            Cleanup(paths);
        }
    }

    /// <summary>Wraps an in-process shard but can be armed to throw on the
    /// coordinator-decision write, simulating a shard whose disk can't persist
    /// the decision (the failure the quorum is meant to tolerate).</summary>
    private sealed class FaultyDecisionTransport : IShardTransport
    {
        private readonly InProcessShardTransport _inner;
        public bool FailDecision { get; set; }
        public FaultyDecisionTransport(string name, DocumentForgeDb db) => _inner = new InProcessShardTransport(name, db);
        public string ShardName => _inner.ShardName;
        public QueryResult Execute(string sql) => _inner.Execute(sql);
        public DocumentId Insert(string c, BsonDocument d) => _inner.Insert(c, d);
        public DatabaseStatistics GetStatistics() => _inner.GetStatistics();
        public bool DeleteById(string c, DocumentId id) => _inner.DeleteById(c, id);
        public void ExecuteTransaction(IReadOnlyList<ShardTxOp> ops) => _inner.ExecuteTransaction(ops);
        public PrepareResult Prepare(string txId, string coord, IReadOnlyList<ShardTxOp> ops, TimeSpan timeout) => _inner.Prepare(txId, coord, ops, timeout);
        public void CommitPrepared(string txId) => _inner.CommitPrepared(txId);
        public void RollbackPrepared(string txId) => _inner.RollbackPrepared(txId);
        public void RecordCoordinatorDecision(string txId, bool commit)
        {
            if (FailDecision) throw new IOException($"[{ShardName}] simulated decision-log write failure");
            _inner.RecordCoordinatorDecision(txId, commit);
        }
        public void RecordCoordinatorDone(string txId) => _inner.RecordCoordinatorDone(txId);
        public IReadOnlyList<PreparedTxRecord> ScanInFlightPrepared() => _inner.ScanInFlightPrepared();
        public CoordinatorTxState? GetCoordinatorTxState(string txId) => _inner.GetCoordinatorTxState(txId);
        public void Dispose() => _inner.Dispose();
    }
}
