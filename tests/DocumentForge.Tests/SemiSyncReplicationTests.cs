using DocumentForge.Core;
using DocumentForge.Engine;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// Issue #95 — semi-sync replication. With MinSyncReplicas &gt; 0 the leader
/// does not acknowledge a write until a follower has durably applied and ACKed
/// the seq, bounding RPO. With MinSyncReplicas = 0 it stays async (unchanged).
/// </summary>
public sealed class SemiSyncReplicationTests
{
    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static void Cleanup(params string[] paths)
    {
        foreach (var p in paths)
            foreach (var ext in new[] { "", ".wal", ".recovery", ".lock", ".followerseq", ".term",
                                        ".snapshot.incoming", ".snapshot.incoming.seq" })
                try { File.Delete(p + ext); } catch { }
    }

    [Fact]
    public async Task SemiSync_Insert_ReturnsOnlyAfterFollowerAcks()
    {
        var leaderPath = Path.Combine(Path.GetTempPath(), $"ss_leader_{Guid.NewGuid():N}.dfdb");
        var followerPath = Path.Combine(Path.GetTempPath(), $"ss_follower_{Guid.NewGuid():N}.dfdb");
        int port = FreePort();
        try
        {
            using var leader = DocumentForgeDb.Create(leaderPath);
            leader.StartLogicalReplicationServer(port);
            leader.MinSyncReplicas = 1;
            leader.SyncReplicationTimeout = TimeSpan.FromSeconds(10);

            using var follower = DocumentForgeDb.Create(followerPath);
            follower.StartLogicalReplicationFollower("localhost", port);
            for (int i = 0; i < 30 && leader.GetLogicalFollowerCount() == 0; i++)
                await Task.Delay(100);
            Assert.Equal(1, leader.GetLogicalFollowerCount());

            // This insert must not return until the follower has applied+ACKed.
            leader.Insert("orders", """{"pnr":"SYNC1"}""");

            // By the time Insert returned, the follower already has the row.
            Assert.Single(follower.Execute("SELECT * FROM orders WHERE pnr = 'SYNC1'").Documents);
        }
        finally { Cleanup(leaderPath, followerPath); }
    }

    [Fact]
    public void SemiSync_Insert_TimesOut_WhenNoFollowerCanAck()
    {
        var leaderPath = Path.Combine(Path.GetTempPath(), $"ss_noack_{Guid.NewGuid():N}.dfdb");
        int port = FreePort();
        try
        {
            using var leader = DocumentForgeDb.Create(leaderPath);
            leader.StartLogicalReplicationServer(port);
            leader.MinSyncReplicas = 1;                      // require one replica
            leader.SyncReplicationTimeout = TimeSpan.FromMilliseconds(400);

            // No follower connected → the ack quorum can never be met.
            var ex = Assert.Throws<ReplicationTimeoutException>(() => leader.Insert("orders", """{"pnr":"LONELY"}"""));
            Assert.Equal(1, ex.Required);
            Assert.Equal(0, ex.Achieved);

            // Critically, the write IS locally durable (semi-sync is about
            // confirming replication, not rolling back the local commit).
            Assert.Single(leader.Execute("SELECT * FROM orders WHERE pnr = 'LONELY'").Documents);
        }
        finally { Cleanup(leaderPath); }
    }

    [Fact]
    public void AsyncDefault_Insert_DoesNotWait()
    {
        // MinSyncReplicas defaults to 0 → the pre-#95 fire-and-forget behaviour;
        // an insert with no followers returns immediately, no throw.
        var leaderPath = Path.Combine(Path.GetTempPath(), $"ss_async_{Guid.NewGuid():N}.dfdb");
        int port = FreePort();
        try
        {
            using var leader = DocumentForgeDb.Create(leaderPath);
            leader.StartLogicalReplicationServer(port);
            Assert.Equal(0, leader.MinSyncReplicas);
            leader.Insert("orders", """{"pnr":"ASYNC"}"""); // no throw, no wait
            Assert.Single(leader.Execute("SELECT * FROM orders WHERE pnr = 'ASYNC'").Documents);
        }
        finally { Cleanup(leaderPath); }
    }

    [Fact]
    public async Task SemiSync_WithNoReplicationServer_IsNoOp()
    {
        // A standalone engine (no leader server) with MinSyncReplicas set never
        // blocks — there's nothing to replicate to.
        var path = Path.Combine(Path.GetTempPath(), $"ss_standalone_{Guid.NewGuid():N}.dfdb");
        try
        {
            using var db = DocumentForgeDb.Create(path);
            db.MinSyncReplicas = 2;
            await Task.Run(() => db.Insert("orders", """{"pnr":"STANDALONE"}"""))
                .WaitAsync(TimeSpan.FromSeconds(3)); // must not hang
            Assert.Single(db.Execute("SELECT * FROM orders").Documents);
        }
        finally { Cleanup(path); }
    }
}
