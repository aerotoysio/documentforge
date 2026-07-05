using DocumentForge.Core;
using DocumentForge.Engine;
using DocumentForge.Storage;
using DocumentForge.Transactions;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// Issues #89/#90/#91 — the recovery log as a real WAL. Commit paths append
/// their dirty pages plus a fsync'd commit marker BEFORE the write lock is
/// released; Open replays only marker-sealed batches. These tests cover the
/// commit-time durability contract (a write acknowledged with no Flush ever
/// called survives a "crash"), transaction atomicity at the log level
/// (unsealed tails are discarded), and format compatibility (legacy logs
/// without markers — pre-#89 engines and PITR-synthesized files — replay
/// in full).
/// </summary>
public sealed class WalDurabilityTests
{
    private static string FreshPath(string label) =>
        Path.Combine(Path.GetTempPath(), $"waldur_{label}_{Guid.NewGuid():N}.dfdb");

    private static void Cleanup(string path)
    {
        foreach (var ext in new[] { "", ".wal", ".recovery", ".lock", ".followerseq" })
            try { File.Delete(path + ext); } catch { }
    }

    private static byte[] FakePage(byte fill)
    {
        var page = new byte[Constants.PageSize];
        Array.Fill(page, fill);
        return page;
    }

    // ---- engine-level: fsync-on-commit + replay-on-open ----

    [Fact]
    public void CommittedInserts_SurviveCrash_WithoutAnyFlush()
    {
        // The #89 scenario: writes acknowledged, no Flush/Checkpoint ever
        // runs, then the data file becomes unwritable (power loss stand-in).
        // Every acknowledged insert must come back via WAL replay.
        var path = FreshPath("commit_no_flush");
        try
        {
            var inner = DataFile.Create(path);
            var faulty = new FaultyDataFile(inner);
            var db = DocumentForgeDb.OpenWithDataFile(path, faulty);
            for (int i = 0; i < 5; i++)
                db.Insert("orders", $$"""{"pnr": "PNR-{{i}}", "amount": {{i * 100}}}""");

            // Kill the data file: every page write from here on fails, so
            // Dispose's FlushAll cannot persist anything to the data file.
            faulty.FailAllWrites = true;
            try { db.Dispose(); } catch (IOException) { /* deferred flush error — expected */ }

            using var reopened = DocumentForgeDb.Open(path);
            var result = reopened.Execute("SELECT * FROM orders");
            Assert.True(result.Success);
            Assert.Equal(5, result.Documents.Count);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void CommittedTransaction_RecoversAtomically_AcrossCollections()
    {
        // The #91 scenario: one multi-doc transaction spanning collections is
        // sealed as a single WAL batch. After a crash-before-flush, replay
        // must restore ALL of it.
        var path = FreshPath("tx_atomic");
        try
        {
            var inner = DataFile.Create(path);
            var faulty = new FaultyDataFile(inner);
            var db = DocumentForgeDb.OpenWithDataFile(path, faulty);

            using (var tx = db.BeginTransaction())
            {
                for (int i = 0; i < 5; i++)
                {
                    tx.Insert("bookings", DocumentForge.Document.BsonDocument.FromJson($$"""{"pnr": "T-{{i}}"}"""));
                    tx.Insert("inventory", DocumentForge.Document.BsonDocument.FromJson($$"""{"seat": "S-{{i}}", "held": true}"""));
                }
                tx.Commit();
            }

            faulty.FailAllWrites = true;
            try { db.Dispose(); } catch (IOException) { }

            using var reopened = DocumentForgeDb.Open(path);
            Assert.Equal(5, reopened.Execute("SELECT * FROM bookings").Documents.Count);
            Assert.Equal(5, reopened.Execute("SELECT * FROM inventory").Documents.Count);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void DurableCommitsOff_CommitPathWritesNothingToLog()
    {
        // Escape hatch for bulk loads: with DurableCommits=false the commit
        // path performs no log append and no fsync (the pre-#89 contract,
        // now opt-in). Durability then comes only from Flush/Dispose — a
        // hard process kill before those loses the write, by design.
        var path = FreshPath("lazy_mode");
        try
        {
            using (var db = DocumentForgeDb.Create(path, new DatabaseOptions { DurableCommits = false }))
            {
                for (int i = 0; i < 3; i++)
                    db.Insert("orders", $$"""{"pnr": "LAZY-{{i}}"}""");

                Assert.True(new FileInfo(path + ".recovery").Length <= 8,
                    "lazy mode must not append commit batches to the recovery log");
            }

            // Clean Dispose still flushes normally.
            using var reopened = DocumentForgeDb.Open(path);
            Assert.Equal(3, reopened.Execute("SELECT * FROM orders").Documents.Count);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void FlushTruncatesLog_AndKeepsDataReadable()
    {
        // The normal steady-state cycle: commits accumulate sealed batches,
        // Flush checkpoints them into the data file and truncates the log
        // back to just its header.
        var path = FreshPath("flush_cycle");
        try
        {
            using (var db = DocumentForgeDb.Create(path))
            {
                for (int i = 0; i < 3; i++)
                    db.Insert("orders", $$"""{"pnr": "F-{{i}}"}""");

                var logPath = path + ".recovery";
                Assert.True(new FileInfo(logPath).Length > 8,
                    "commits must land in the recovery log before any flush");

                db.Flush();
                Assert.True(new FileInfo(logPath).Length <= 8,
                    "flush must truncate the log to its file header");
            }

            using var reopened = DocumentForgeDb.Open(path);
            Assert.Equal(3, reopened.Execute("SELECT * FROM orders").Documents.Count);
        }
        finally { Cleanup(path); }
    }

    // ---- log-level: commit-marker replay semantics ----

    [Fact]
    public void Replay_AppliesOnlyMarkerSealedBatches()
    {
        var logPath = FreshPath("sealed_only") + ".recovery";
        try
        {
            using (var log = new RecoveryLog(logPath))
            {
                log.LogPageWrite(new PageId(1), FakePage(0xA1));
                log.LogPageWrite(new PageId(2), FakePage(0xA2));
                log.Commit();                                    // seals pages 1+2
                log.LogPageWrite(new PageId(3), FakePage(0xA3)); // crash before marker
            }

            var replayed = RecoveryLog.ReplayRecords(logPath).ToList();
            Assert.Equal(2, replayed.Count);
            Assert.Equal(1u, replayed[0].PageId.Value);
            Assert.Equal(2u, replayed[1].PageId.Value);
        }
        finally { try { File.Delete(logPath); } catch { } }
    }

    [Fact]
    public void Replay_EmptyOrHeaderOnlyLog_YieldsNothing()
    {
        var logPath = FreshPath("header_only") + ".recovery";
        try
        {
            using (var _ = new RecoveryLog(logPath)) { }
            Assert.Empty(RecoveryLog.ReplayRecords(logPath));
        }
        finally { try { File.Delete(logPath); } catch { } }
    }

    [Fact]
    public void Replay_MultipleBatches_AppliedInOrder()
    {
        // Same page committed twice: both records replay in log order so the
        // last write wins when applied sequentially to the data file.
        var logPath = FreshPath("in_order") + ".recovery";
        try
        {
            using (var log = new RecoveryLog(logPath))
            {
                log.LogPageWrite(new PageId(7), FakePage(0x01));
                log.Commit();
                log.LogPageWrite(new PageId(7), FakePage(0x02));
                log.Commit();
            }

            var replayed = RecoveryLog.ReplayRecords(logPath).ToList();
            Assert.Equal(2, replayed.Count);
            Assert.Equal(0x01, replayed[0].PageData[100]);
            Assert.Equal(0x02, replayed[1].PageData[100]);
        }
        finally { try { File.Delete(logPath); } catch { } }
    }

    [Fact]
    public void Replay_LegacyLogWithoutHeader_AppliesAllRecords()
    {
        // Pre-#89 logs and PITR-synthesized .recovery files are bare "WLOG"
        // record streams with no file header and no markers. They must keep
        // replaying in full (the old behaviour) or upgrades and restores
        // would silently drop data.
        var logPath = FreshPath("legacy") + ".recovery";
        try
        {
            using (var stream = new FileStream(logPath, FileMode.Create, FileAccess.Write))
            {
                foreach (var (id, fill) in new[] { (10u, (byte)0xB1), (11u, (byte)0xB2) })
                {
                    var page = FakePage(fill);
                    var record = new byte[12 + Constants.PageSize];
                    "WLOG"u8.ToArray().CopyTo(record, 0);
                    BitConverter.TryWriteBytes(record.AsSpan(4), id);
                    BitConverter.TryWriteBytes(record.AsSpan(8), RecoveryLog.Crc32(page));
                    page.CopyTo(record.AsSpan(12));
                    stream.Write(record);
                }
            }

            var replayed = RecoveryLog.ReplayRecords(logPath).ToList();
            Assert.Equal(2, replayed.Count);
            Assert.Equal(10u, replayed[0].PageId.Value);
            Assert.Equal(11u, replayed[1].PageId.Value);
        }
        finally { try { File.Delete(logPath); } catch { } }
    }

    [Fact]
    public void Replay_CorruptRecord_StopsAtCorruption_KeepsPriorBatches()
    {
        // Bit-rot mid-log: everything sealed before the corruption replays;
        // nothing after it does (order past the tear is untrustworthy).
        var logPath = FreshPath("corrupt_tail") + ".recovery";
        try
        {
            using (var log = new RecoveryLog(logPath))
            {
                log.LogPageWrite(new PageId(1), FakePage(0xC1));
                log.Commit();
            }
            // Append garbage where the next record would start.
            using (var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write))
                stream.Write(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01 });

            var replayed = RecoveryLog.ReplayRecords(logPath).ToList();
            Assert.Single(replayed);
            Assert.Equal(1u, replayed[0].PageId.Value);
        }
        finally { try { File.Delete(logPath); } catch { } }
    }
}
