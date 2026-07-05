using DocumentForge.Core;
using DocumentForge.Engine;
using DocumentForge.Storage;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// Issue #94 — the free-page list is persisted (intrusive FreeList chain, head
/// in the file header) and reloaded on Open, so a churny node reuses reclaimed
/// pages instead of growing its data file forever. The loader is defensive:
/// a reallocated / torn / cyclic tail truncates the chain (leak, never
/// double-allocate), and double-frees are ignored.
///
/// Pages are reclaimed when overflow chains (large documents) are deleted, so
/// these tests churn multi-page documents to exercise real free/reuse.
/// </summary>
public sealed class FreeListPersistenceTests
{
    // Comfortably larger than one page → each doc spans several overflow pages.
    private static readonly string BigPad = new string('x', 20_000);

    private static string BigDoc(int i) => $$"""{"i":{{i}},"pad":"{{BigPad}}"}""";

    private static string FreshPath(string label) =>
        Path.Combine(Path.GetTempPath(), $"freelist_{label}_{Guid.NewGuid():N}.dfdb");

    private static void Cleanup(string path)
    {
        foreach (var ext in new[] { "", ".wal", ".recovery", ".lock", ".followerseq" })
            try { File.Delete(path + ext); } catch { }
    }

    private static long PageCount(string path) =>
        new FileInfo(path).Length / Constants.PageSize;

    [Fact]
    public void FreedPages_AreReused_WithinASession_NoFileGrowth()
    {
        var path = FreshPath("reuse_session");
        try
        {
            using var db = DocumentForgeDb.Create(path);
            var coll = db.GetCollection("docs") ?? db.GetOrCreateCollection("docs");
            var ids = new List<DocumentId>();
            for (int i = 0; i < 40; i++)
                ids.Add(db.Insert("docs", BigDoc(i)));
            db.Flush();
            var peak = PageCount(path);

            foreach (var id in ids) Assert.True(coll.Delete(id));
            db.Flush();

            // Re-insert the same volume: allocations should draw from the free
            // list, so the file must not grow materially past its prior peak.
            for (int i = 0; i < 40; i++)
                db.Insert("docs", BigDoc(i));
            db.Flush();

            Assert.True(PageCount(path) <= peak + 2,
                $"file grew from {peak} to {PageCount(path)} pages despite a full free list");
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void FreeList_SurvivesRestart_AndIsReused()
    {
        var path = FreshPath("reuse_restart");
        try
        {
            long peak;
            using (var db = DocumentForgeDb.Create(path))
            {
                var coll = db.GetOrCreateCollection("docs");
                var ids = new List<DocumentId>();
                for (int i = 0; i < 40; i++)
                    ids.Add(db.Insert("docs", BigDoc(i)));
                db.Flush();
                peak = PageCount(path);
                foreach (var id in ids) coll.Delete(id);
                db.Flush(); // free list persisted to the header here
            }

            // Reopen: the persisted free list must load, so re-inserting reuses
            // the freed pages rather than extending the file.
            using (var db = DocumentForgeDb.Open(path))
            {
                for (int i = 0; i < 40; i++)
                    db.Insert("docs", BigDoc(i));
                db.Flush();
                Assert.True(PageCount(path) <= peak + 2,
                    $"after restart the free list wasn't reused: {peak} → {PageCount(path)} pages");
            }
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void FreeListHead_PersistedInHeader_PointsAtAFreePage()
    {
        var path = FreshPath("head_persist");
        try
        {
            using (var db = DocumentForgeDb.Create(path))
            {
                var coll = db.GetOrCreateCollection("docs");
                var ids = new List<DocumentId>();
                for (int i = 0; i < 20; i++)
                    ids.Add(db.Insert("docs", BigDoc(i)));
                foreach (var id in ids) coll.Delete(id);
                db.Flush();
            }

            using var file = DataFile.Open(path);
            var head = file.GetFreeListHead();
            Assert.True(head.IsValid, "free-list head should be set after freeing pages");
            Assert.True(file.TryReadPage(head, out var buf));
            Assert.Equal(PageType.FreeList, PageHeader.ReadFrom(buf).PageType);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void DoubleFree_IsIgnored_NoCycleNoDoubleAllocate()
    {
        var path = FreshPath("double_free");
        try
        {
            using var file = DataFile.Create(path);
            var cache = new PageCache(file);
            var alloc = new PageAllocator(file, cache);

            var p = alloc.AllocatePage(PageType.Data);
            alloc.FreePage(p);
            alloc.FreePage(p); // double free — must be a no-op
            Assert.Equal(1, alloc.FreePageCount);

            var a = alloc.AllocatePage(PageType.Data);
            var b = alloc.AllocatePage(PageType.Data);
            Assert.Equal(p.Value, a.Value);          // reused the single free page
            Assert.NotEqual(a.Value, b.Value);       // second alloc did NOT re-hand-out p
            Assert.Equal(0, alloc.FreePageCount);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void ReallocatedTail_TruncatesChain_OnLoad_NeverDoubleAllocates()
    {
        // Simulate the crash window: head points at a page that has since been
        // rewritten as a Data page (head-pointer update was lost). The loader
        // must stop at it and treat the list as empty rather than reclaim a
        // live page.
        var path = FreshPath("stale_tail");
        try
        {
            PageId freed;
            using (var file = DataFile.Create(path))
            {
                var cache = new PageCache(file);
                var alloc = new PageAllocator(file, cache);
                var p = alloc.AllocatePage(PageType.Data);
                alloc.FreePage(p);
                freed = p;
                cache.FlushAll();
                // Overwrite the freed page in place as a Data page WITHOUT
                // updating the head — exactly the stale-head crash window.
                var data = new byte[Constants.PageSize];
                new PageHeader { PageId = freed, PageType = PageType.Data,
                    FreeSpaceStart = (ushort)Constants.PageHeaderSize,
                    FreeSpaceEnd = (ushort)Constants.PageSize,
                    NextPageId = PageId.Invalid, PrevPageId = PageId.Invalid }.WriteTo(data);
                file.WritePage(freed, data);
                file.Flush();
            }

            using (var file = DataFile.Open(path))
            {
                var cache = new PageCache(file);
                var alloc = new PageAllocator(file, cache);
                Assert.Equal(0, alloc.FreePageCount); // live page not reclaimed
            }
        }
        finally { Cleanup(path); }
    }
}
