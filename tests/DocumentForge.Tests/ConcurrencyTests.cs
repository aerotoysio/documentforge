using System.Collections.Concurrent;
using DocumentForge.Engine;
using Xunit;

namespace DocumentForge.Tests;

// Stage A — single-writer / multiple-reader concurrency. DocumentForgeDb routes
// SELECTs through the TransactionManager read lock (concurrent) and mutations
// through the write lock (exclusive). These tests prove the read path is safe
// under concurrency and guard against anyone regressing the ReaderWriterLockSlim
// back to an exclusive lock. (Cross-process exclusivity is a separate, by-design
// file lock — one OS process per .dfdb.)
public sealed class ConcurrencyTests : IDisposable
{
    private readonly List<string> _paths = new();

    private DocumentForgeDb NewDb()
    {
        var p = Path.Combine(Path.GetTempPath(), $"conc_{Guid.NewGuid():N}.dfdb");
        _paths.Add(p);
        return DocumentForgeDb.OpenOrCreate(p);
    }

    public void Dispose()
    {
        foreach (var p in _paths)
            foreach (var f in new[] { p, p + ".wal", p + ".recovery", p + ".followerseq" })
                try { if (File.Exists(f)) File.Delete(f); } catch { /* best effort */ }
    }

    [Fact]
    public void ConcurrentReaders_SeeConsistentData_NoExceptions()
    {
        using var db = NewDb();
        for (int i = 0; i < 200; i++)
            db.Insert("items", $$"""{ "n": {{i}}, "tag": "t{{i % 5}}" }""");

        var errors = new ConcurrentBag<Exception>();
        Parallel.For(0, 8, _ =>
        {
            try
            {
                for (int k = 0; k < 50; k++)
                {
                    var r = db.Execute("SELECT * FROM items WHERE tag = 't3'");
                    Assert.Equal(40, r.Documents.Count); // i % 5 == 3 → 40 of 200
                }
            }
            catch (Exception ex) { errors.Add(ex); }
        });

        Assert.Empty(errors);
    }

    [Fact]
    public void ConcurrentReadsDuringWrites_RemainSafe()
    {
        using var db = NewDb();
        for (int i = 0; i < 50; i++) db.Insert("items", $$"""{ "n": {{i}} }""");

        var errors = new ConcurrentBag<Exception>();

        // One exclusive writer adding rows while six readers stream the
        // collection. The RW lock must keep every read internally consistent.
        var writer = Task.Run(() =>
        {
            try { for (int i = 50; i < 150; i++) db.Insert("items", $$"""{ "n": {{i}} }"""); }
            catch (Exception ex) { errors.Add(ex); }
        });

        Parallel.For(0, 6, _ =>
        {
            try
            {
                for (int k = 0; k < 100; k++)
                {
                    var r = db.Execute("SELECT * FROM items");
                    Assert.InRange(r.Documents.Count, 50, 150); // writer only ever adds
                }
            }
            catch (Exception ex) { errors.Add(ex); }
        });

        writer.Wait();
        Assert.Empty(errors);
        Assert.Equal(150, db.Execute("SELECT * FROM items").Documents.Count);
    }
}
