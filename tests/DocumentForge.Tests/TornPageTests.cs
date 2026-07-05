using DocumentForge.Core;
using DocumentForge.Engine;
using DocumentForge.Storage;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// Issue #92 — torn / bit-rotted pages are no longer served as if valid.
/// <see cref="DataFile.ReadPage"/> throws <see cref="PageCorruptionException"/>
/// on a checksum mismatch instead of returning the corrupt buffer, while the
/// WAL redo (issues #89/#90) repairs a page torn during the last flush window
/// on Open. Corruption-tolerant loaders use <see cref="DataFile.TryReadPage"/>.
/// </summary>
public sealed class TornPageTests
{
    private static string FreshPath(string label) =>
        Path.Combine(Path.GetTempPath(), $"torn_{label}_{Guid.NewGuid():N}.dfdb");

    private static void Cleanup(string path)
    {
        foreach (var ext in new[] { "", ".wal", ".recovery", ".lock", ".followerseq" })
            try { File.Delete(path + ext); } catch { }
    }

    /// <summary>Overwrite the tail of a page's on-disk bytes without fixing the
    /// checksum — a torn write / bit-rot stand-in.</summary>
    private static void CorruptPageTail(string path, PageId pageId)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Write);
        fs.Seek(pageId.FileOffset + Constants.PageSize - 32, SeekOrigin.Begin);
        fs.Write(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xDE, 0xAD, 0xBE, 0xEF }, 0, 8);
    }

    [Fact]
    public void ReadPage_ChecksumMismatch_Throws()
    {
        var path = FreshPath("read_throws");
        try
        {
            PageId dataPage;
            using (var db = DocumentForgeDb.Create(path))
            {
                for (int i = 0; i < 50; i++)
                    db.Insert("orders", $$"""{"i":{{i}},"pad":"{{new string('x', 100)}}"}""");
                dataPage = db.GetCollection("orders")!.FirstDataPage;
                db.Flush();
            }

            CorruptPageTail(path, dataPage);

            using var file = DataFile.Open(path);
            var ex = Assert.Throws<PageCorruptionException>(() => file.ReadPage(dataPage));
            Assert.Equal(dataPage.Value, ex.PageId.Value);
            Assert.Equal(1, file.ChecksumMismatches);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void TryReadPage_ChecksumMismatch_ReturnsFalse_NotThrow()
    {
        var path = FreshPath("tryread");
        try
        {
            PageId dataPage;
            using (var db = DocumentForgeDb.Create(path))
            {
                for (int i = 0; i < 50; i++)
                    db.Insert("orders", $$"""{"i":{{i}},"pad":"{{new string('x', 100)}}"}""");
                dataPage = db.GetCollection("orders")!.FirstDataPage;
                db.Flush();
            }

            CorruptPageTail(path, dataPage);

            using var file = DataFile.Open(path);
            Assert.False(file.TryReadPage(dataPage, out var buf));
            Assert.Equal(Constants.PageSize, buf.Length); // raw bytes still returned

            // A clean page still reads true.
            Assert.True(file.TryReadPage(PageId.CollectionCatalog, out _));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void TornPageDuringFlushWindow_IsRepairedFromWal_OnOpen()
    {
        // The recoverable case (#92 remediation via #89/#90 redo): a page is
        // committed to the WAL, then torn on its way to the data file. Because
        // the WAL still holds the clean image at reopen, replay overwrites the
        // torn page before anything reads it — the DB opens clean.
        var path = FreshPath("wal_repair");
        try
        {
            var inner = DataFile.Create(path);
            var faulty = new FaultyDataFile(inner);
            var db = DocumentForgeDb.OpenWithDataFile(path, faulty);
            for (int i = 0; i < 40; i++)
                db.Insert("orders", $$"""{"i":{{i}},"pad":"{{new string('y', 100)}}"}""");

            // Data file writes all fail from here, so the pages the commits
            // logged to the WAL never reach the data file cleanly. Dispose's
            // FlushAll throws; the WAL is left intact for replay.
            faulty.FailAllWrites = true;
            try { db.Dispose(); } catch (IOException) { }

            // Reopen through the real path: WAL replay repairs the pages, and
            // every acknowledged insert reads back without a corruption throw.
            using var reopened = DocumentForgeDb.Open(path);
            var result = reopened.Execute("SELECT * FROM orders");
            Assert.True(result.Success);
            Assert.Equal(40, result.Documents.Count);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void UnrecoverableTornPage_SurfacesAsCorruption_NotGarbage()
    {
        // The unrecoverable case: a page torn OUTSIDE any flush window (bit-rot
        // long after checkpoint). The WAL was already truncated, so there is
        // nothing to redo — reading it must fail loud rather than feed garbage
        // into a query.
        var path = FreshPath("unrecoverable");
        try
        {
            PageId dataPage;
            using (var db = DocumentForgeDb.Create(path))
            {
                for (int i = 0; i < 50; i++)
                    db.Insert("orders", $$"""{"i":{{i}},"pad":"{{new string('z', 100)}}"}""");
                dataPage = db.GetCollection("orders")!.FirstDataPage;
                db.Flush(); // checkpoint: WAL truncated, page durable in data file
            }

            CorruptPageTail(path, dataPage); // rot after checkpoint

            // Open eagerly builds location maps, which reads the data pages, so
            // the corruption surfaces at Open — the DB refuses to come up on a
            // torn data page rather than serving a partial/garbled scan later.
            Assert.Throws<PageCorruptionException>(() => DocumentForgeDb.Open(path));
        }
        finally { Cleanup(path); }
    }
}
