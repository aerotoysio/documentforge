using DocumentForge.Core;
using DocumentForge.Engine;
using DocumentForge.Storage;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// Crash-injection regression tests (issue #28). Each test:
///
/// <list type="number">
///   <item>Builds a real DB through <see cref="FaultyDataFile"/>.</item>
///   <item>Drives a workload that triggers the failure mode under test.</item>
///   <item>Disposes (lock released, recovery log written/preserved).</item>
///   <item>Re-opens through the standard <see cref="DocumentForgeDb.Open"/>
///         path — the engine's actual recovery semantics — and asserts the
///         observed state matches what the contract claims.</item>
/// </list>
///
/// <para>
/// Each test owns its own data file path and cleans up in a finally block.
/// The artifacts (.dfdb, .wal, .recovery, .lock, .followerseq) need explicit
/// removal so failures don't pollute the next run.
/// </para>
///
/// <para>
/// What's NOT tested here yet (and shouldn't be — they need their own fixes
/// first): genuine eviction-under-fault recovery (#24 parts 2-3 — Evict
/// doesn't fsync today), disk-full handling (#25 — engine doesn't fail-fast
/// yet), and tx partial-rollback (#23 — needs the shadow-buffer model).
/// Those tests will land in their respective PRs alongside the fix; the
/// harness here is the foundation that makes them feasible.
/// </para>
/// </summary>
public sealed class CrashInjectionTests
{
    private static string FreshPath(string label) =>
        Path.Combine(Path.GetTempPath(), $"crash_{label}_{Guid.NewGuid():N}.dfdb");

    private static void Cleanup(string path)
    {
        foreach (var ext in new[] { "", ".wal", ".recovery", ".lock", ".followerseq" })
            try { File.Delete(path + ext); } catch { }
    }

    /// <summary>DataFile has no built-in OpenOrCreate; the engine wraps it in
    /// DocumentForgeDb.OpenOrCreate. Tests bypass that wrapper so we synthesise
    /// the same logic here.</summary>
    private static DataFile OpenOrCreateDataFile(string path) =>
        File.Exists(path) ? DataFile.Open(path) : DataFile.Create(path);

    [Fact]
    public void RecoveryLog_ReplayedOnReopen_AfterFsyncFault()
    {
        // The whole point of the recovery log: if the fsync at the end of
        // FlushAll fails (full disk, networked filesystem hiccup), the data
        // file's writes may or may not be on disk — but the recovery log
        // captured the pages BEFORE the data-file writes started, and
        // wasn't truncated because Phase 4 (`OnAfterFlushComplete`) didn't
        // run. Next Open replays from the log and the state is whole.
        //
        // Pre-recovery-log this scenario silently lost data; the test
        // pins the contract that's been promised since the WAL landed.
        var path = FreshPath("recoverylog");
        try
        {
            using (var underlying = OpenOrCreateDataFile(path))
            {
                var faulty = new FaultyDataFile(underlying) { ThrowOnNextFlush = true };
                using var db = DocumentForgeDb.OpenWithDataFile(path, faulty);

                for (int i = 0; i < 20; i++)
                    db.Insert("orders", $$"""{"pnr":"PNR{{i:D3}}"}""");

                // The injected fsync failure surfaces as IOException out of
                // FlushAll. Caller must observe and react.
                Assert.Throws<IOException>(() => db.Flush());
            }

            // Reopen via the standard path. ReplayRecoveryLog runs as part
            // of Open. Every committed insert from before the fault must
            // be observable.
            using var reopened = DocumentForgeDb.Open(path);
            var rows = reopened.Execute("SELECT * FROM orders").Documents;
            Assert.Equal(20, rows.Count);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void IndexCatalogPagePointer_FaultDoesNotProduceStaleStateOnReopen()
    {
        // PR #29's Flush(true) fix in SetIndexCatalogPage. Without it, a
        // crash between the 4-byte header write and the next FlushAll would
        // leave the data file with the catalog page allocated but the
        // header pointer never persisted — index orphaned on disk.
        //
        // Here we inject a failure ON the SetIndexCatalogPage call itself.
        // The CreateIndex op throws, no inconsistent claim is made about an
        // index existing, and the next Open sees a coherent state.
        var path = FreshPath("idxcatfsync");
        try
        {
            using (var underlying = OpenOrCreateDataFile(path))
            {
                var faulty = new FaultyDataFile(underlying)
                {
                    ThrowOnNextSetIndexCatalogPage = true,
                };
                using var db = DocumentForgeDb.OpenWithDataFile(path, faulty);
                db.Insert("users", """{"email":"a@b.com"}""");

                // CreateIndex internally calls SetIndexCatalogPage; the
                // injected throw propagates as IOException.
                Assert.Throws<IOException>(() =>
                    db.CreateIndex("users", "email", "idx_users_email", unique: true));
            }

            // The doc itself is fine — it was written before the index op.
            using var reopened = DocumentForgeDb.Open(path);
            var rows = reopened.Execute("SELECT * FROM users").Documents;
            Assert.Single(rows);

            // Indexes shouldn't be claimed when their catalog write failed.
            // The catalog page may exist on disk but the header pointer
            // wasn't persisted (it failed) so GetIndexCatalogPage returns
            // Invalid and LoadFromCatalog finds nothing.
            Assert.Empty(reopened.GetIndexes("users"));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Reopen_AfterFault_DoesNotDeadlockOnLock()
    {
        // The lock file (PR #31) is released LAST in Dispose so a crash
        // mid-shutdown leaves it visible for diagnostics. Here we crash
        // BEFORE Dispose (an exception during Insert / Flush). The lock
        // file is left behind; next Open's stale-lock detector should
        // reclaim it (same host, dead pid) and proceed.
        var path = FreshPath("lockreclaim");
        try
        {
            DocumentForgeDb? leaked = null;
            try
            {
                using var underlying = OpenOrCreateDataFile(path);
                var faulty = new FaultyDataFile(underlying) { ThrowOnNextFlush = true };
                leaked = DocumentForgeDb.OpenWithDataFile(path, faulty);
                leaked.Insert("orders", """{"pnr":"X"}""");

                Assert.Throws<IOException>(() => leaked.Dispose());
                leaked = null;
            }
            finally
            {
                try { leaked?.Dispose(); } catch { }
            }

            // Reopen must succeed despite the leftover lock from the
            // crashed prior session.
            using var reopened = DocumentForgeDb.Open(path);
            // Smoke: a real op proves the engine is functional.
            _ = reopened.Execute("SELECT * FROM orders");
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void TransactionalCommit_FaultMidApply_DoesNotBlockNextOpen()
    {
        // Ground-truth check on the Phase 1 partial-commit gap (issue #23).
        // A multi-doc tx where ApplyTransactionLocked hits an I/O failure
        // mid-batch may leave partial state today — but the next Open must
        // still succeed and produce a coherent (even if partial) state. The
        // shadow-buffer fix in #23 will tighten this; for now we verify the
        // engine doesn't get wedged.
        var path = FreshPath("txfault");
        try
        {
            using (var underlying = OpenOrCreateDataFile(path))
            {
                var faulty = new FaultyDataFile(underlying) { ThrowOnNextFlush = true };
                using var db = DocumentForgeDb.OpenWithDataFile(path, faulty);

                using var tx = db.BeginTransaction();
                for (int i = 0; i < 10; i++)
                    tx.Insert("orders", $$"""{"pnr":"PNR{{i}}"}""");

                // Commit may or may not throw depending on whether Apply
                // hits Flush before returning. We catch on purpose; the test
                // is about post-fault recovery, not propagation.
                try { tx.Commit(); } catch (IOException) { /* expected */ }

                // The Dispose at scope end will also try to flush and may
                // throw; absorb so the test cleanup runs.
                try { db.Dispose(); } catch (IOException) { /* expected */ }
            }

            // Reopen. The state may be empty, partial, or full — what we
            // ENFORCE is that the engine comes up cleanly and SELECTs respond.
            using var reopened = DocumentForgeDb.Open(path);
            var r = reopened.Execute("SELECT * FROM orders");
            Assert.True(r.Success, $"Reopen failed to query after tx fault: {r.Message}");
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void FaultyDataFile_RecordsInjectedFaultForAssertions()
    {
        // Smoke test on the harness itself — confirms the test infrastructure
        // does what the README claims. Without this, a future change that
        // accidentally swallows the throw inside FaultyDataFile would let
        // every other test silently lie.
        var path = FreshPath("harness");
        try
        {
            using var underlying = OpenOrCreateDataFile(path);
            var faulty = new FaultyDataFile(underlying) { ThrowOnNextFlush = true };
            using var db = DocumentForgeDb.OpenWithDataFile(path, faulty);

            db.Insert("orders", """{"pnr":"X"}""");
            try { db.Flush(); } catch (IOException) { /* expected */ }

            Assert.NotNull(faulty.LastInjectedFault);
            Assert.Contains("flush", faulty.LastInjectedFault!.Message,
                StringComparison.OrdinalIgnoreCase);
        }
        finally { Cleanup(path); }
    }
}
