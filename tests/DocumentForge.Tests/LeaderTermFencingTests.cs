using DocumentForge.Engine;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// Issue #96 — leader-term fencing epoch. Promotions bump a persistent
/// monotonic term; a node that observes a higher term steps down to read-only
/// (fences itself) so two leaders can't both accept writes; a node rejects a
/// stale lower-term leader. This is the epoch/fencing foundation (quorum-based
/// promotion and a fully term-aware router are tracked as follow-up consensus
/// work).
/// </summary>
public sealed class LeaderTermFencingTests
{
    private static string FreshPath(string label) =>
        Path.Combine(Path.GetTempPath(), $"term_{label}_{Guid.NewGuid():N}.dfdb");

    private static void Cleanup(string path)
    {
        foreach (var ext in new[] { "", ".wal", ".recovery", ".lock", ".followerseq", ".term", ".term.tmp" })
            try { File.Delete(path + ext); } catch { }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public void Promotion_BumpsTerm_Monotonically()
    {
        var path = FreshPath("bump");
        try
        {
            using var db = DocumentForgeDb.Create(path);
            Assert.Equal(0ul, db.LeaderTerm);
            db.PromoteToLeader(FreePort());
            Assert.Equal(1ul, db.LeaderTerm);
            db.StepDownToFollower();
            db.PromoteToLeader(FreePort());
            Assert.Equal(2ul, db.LeaderTerm);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Term_PersistsAcrossRestart_NeverRegresses()
    {
        var path = FreshPath("persist");
        try
        {
            using (var db = DocumentForgeDb.Create(path))
            {
                db.PromoteToLeader(FreePort());
                db.PromoteToLeader(FreePort()); // (idempotent re-promote path bumps again)
                Assert.True(db.LeaderTerm >= 1);
            }
            using (var reopened = DocumentForgeDb.Open(path))
            {
                Assert.True(reopened.LeaderTerm >= 1, "term must survive restart (Raft-style stable state)");
            }
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void ObserveHigherTerm_OnALeader_FencesToReadOnly()
    {
        var path = FreshPath("fence");
        try
        {
            using var db = DocumentForgeDb.Create(path);
            db.PromoteToLeader(FreePort());        // term 1, leader, accepts writes
            Assert.False(db.IsReadOnly);
            db.Insert("orders", """{"pnr":"A"}"""); // ok as leader

            // A newer leader (term 5) is observed → we must fence.
            var obs = db.ObserveTerm(5);
            Assert.Equal(DocumentForgeDb.TermObservation.AdoptedAndFenced, obs);
            Assert.True(db.IsReadOnly);
            Assert.Equal(5ul, db.LeaderTerm);

            // Fenced node refuses writes — no split-brain double-accept.
            Assert.ThrowsAny<Exception>(() => db.Insert("orders", """{"pnr":"B"}"""));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void ObserveLowerTerm_IsRejectedAsStale()
    {
        var path = FreshPath("stale");
        try
        {
            using var db = DocumentForgeDb.Create(path);
            db.ObserveTerm(7); // adopt term 7
            Assert.Equal(DocumentForgeDb.TermObservation.StaleRejected, db.ObserveTerm(3));
            Assert.Equal(DocumentForgeDb.TermObservation.Current, db.ObserveTerm(7));
            Assert.Equal(7ul, db.LeaderTerm);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void ObserveHigherTerm_OnAFollower_AdoptsWithoutFencing()
    {
        var path = FreshPath("adopt");
        try
        {
            using var db = DocumentForgeDb.Create(path); // not a leader
            Assert.Equal(DocumentForgeDb.TermObservation.Adopted, db.ObserveTerm(4));
            Assert.Equal(4ul, db.LeaderTerm);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task StaleLeader_IsFenced_WhenAFollowerWithHigherTermConnects()
    {
        // End-to-end: an old leader (term 1) is contacted by a follower that
        // has adopted a newer term (2). The handshake advertises the newer
        // term, and the old leader steps down to read-only — it can no longer
        // accept writes, so it can't diverge from the real leader.
        var oldLeaderPath = FreshPath("oldleader");
        var followerPath = FreshPath("newfollower");
        int oldLeaderPort = FreePort();
        try
        {
            using var oldLeader = DocumentForgeDb.Create(oldLeaderPath);
            oldLeader.PromoteToLeader(oldLeaderPort); // term 1
            Assert.False(oldLeader.IsReadOnly);
            await Task.Delay(200);

            using var follower = DocumentForgeDb.Create(followerPath);
            follower.ObserveTerm(2); // this node has already followed a newer (term 2) leader
            follower.StartLogicalReplicationFollower("localhost", oldLeaderPort);

            // The old leader should observe the follower's higher term and fence.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && !oldLeader.IsReadOnly)
                await Task.Delay(100);

            Assert.True(oldLeader.IsReadOnly, "the stale leader must fence itself on seeing a higher term");
            Assert.Equal(2ul, oldLeader.LeaderTerm);
            Assert.ThrowsAny<Exception>(() => oldLeader.Insert("orders", """{"pnr":"SPLIT"}"""));
        }
        finally { Cleanup(oldLeaderPath); Cleanup(followerPath); }
    }
}
