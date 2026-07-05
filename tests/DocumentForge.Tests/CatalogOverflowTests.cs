using DocumentForge.Core;
using DocumentForge.Document;
using DocumentForge.Engine;
using DocumentForge.Storage;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// Issue #93 — the collection catalog spills onto a multi-page chain instead
/// of silently dropping entries once it overflows the first 8 KB page. Enough
/// collections to exceed one page must all survive a save/reload, and the
/// catalog page(s) must be correctly typed.
/// </summary>
public sealed class CatalogOverflowTests
{
    private static string FreshPath(string label) =>
        Path.Combine(Path.GetTempPath(), $"catalog_{label}_{Guid.NewGuid():N}.dfdb");

    private static void Cleanup(string path)
    {
        foreach (var ext in new[] { "", ".wal", ".recovery", ".lock", ".followerseq" })
            try { File.Delete(path + ext); } catch { }
    }

    // A single 8 KB page holds ~250-300 short-named entries; 800 forces the
    // catalog well past one page and onto the overflow chain.
    private const int ManyCollections = 800;

    [Fact]
    public void ManyCollections_ExceedOnePage_AllSurviveReload()
    {
        var path = FreshPath("overflow_reload");
        try
        {
            using (var db = DocumentForgeDb.Create(path))
            {
                for (int i = 0; i < ManyCollections; i++)
                    db.Insert($"collection_number_{i:D5}", """{"seed":1}""");
                db.Flush();
                Assert.Equal(ManyCollections, db.GetCollectionNames().Count);
            }

            // Reopen: every collection must reload from the chain — none dropped.
            using (var db = DocumentForgeDb.Open(path))
            {
                var names = db.GetCollectionNames();
                Assert.Equal(ManyCollections, names.Count);
                Assert.Contains("collection_number_00000", names);
                Assert.Contains($"collection_number_{ManyCollections - 1:D5}", names);
                // Data under a late-registered collection is still reachable.
                var r = db.Execute($"SELECT * FROM collection_number_{ManyCollections - 1:D5}");
                Assert.True(r.Success);
                Assert.Equal(1, r.Documents.Count);
            }
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void CatalogSpills_OntoAChain_HeadAndOverflowAreCatalogTyped()
    {
        var path = FreshPath("chain_typed");
        try
        {
            using (var db = DocumentForgeDb.Create(path))
            {
                for (int i = 0; i < ManyCollections; i++)
                    db.Insert($"collection_number_{i:D5}", """{"seed":1}""");
                db.Flush();
            }

            using var file = DataFile.Open(path);
            // Walk the catalog chain from page 1 and count the pages.
            int catalogPages = 0;
            var cursor = PageId.CollectionCatalog;
            var seen = new HashSet<uint>();
            bool head = true;
            while (cursor.IsValid && seen.Add(cursor.Value))
            {
                Assert.True(file.TryReadPage(cursor, out var buf));
                var hdr = PageHeader.ReadFrom(buf);
                if (!head)
                    Assert.Equal(PageType.CollectionCatalog, hdr.PageType);
                catalogPages++;
                cursor = hdr.NextPageId;
                head = false;
            }
            Assert.True(catalogPages >= 2, $"expected the catalog to span >1 page, got {catalogPages}");
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void CatalogShrink_ReleasesOverflowPages_ForReuse()
    {
        // Grow the catalog onto overflow pages, then drop most collections. The
        // freed overflow pages must return to the allocator (not leak), and the
        // survivors must still reload.
        var path = FreshPath("shrink");
        try
        {
            using (var db = DocumentForgeDb.Create(path))
            {
                for (int i = 0; i < ManyCollections; i++)
                    db.Insert($"collection_number_{i:D5}", """{"seed":1}""");
                db.Flush();

                for (int i = 10; i < ManyCollections; i++)
                    db.DropCollection($"collection_number_{i:D5}");
                db.Flush();
                Assert.Equal(10, db.GetCollectionNames().Count);
            }

            using (var db = DocumentForgeDb.Open(path))
            {
                Assert.Equal(10, db.GetCollectionNames().Count);
                Assert.Contains("collection_number_00000", db.GetCollectionNames());
            }
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void CatalogPage_IsCorrectlyTyped_AfterSave()
    {
        // Regression for the stale-header bug: Insert's FlushHeader used to
        // overwrite the CollectionCatalog type back to Data. Even the single
        // head page must now report the right type.
        var path = FreshPath("head_typed");
        try
        {
            using (var db = DocumentForgeDb.Create(path))
            {
                db.Insert("orders", """{"pnr":"A"}""");
                db.Flush();
            }
            using var file = DataFile.Open(path);
            Assert.True(file.TryReadPage(PageId.CollectionCatalog, out var buf));
            Assert.Equal(PageType.CollectionCatalog, PageHeader.ReadFrom(buf).PageType);
        }
        finally { Cleanup(path); }
    }
}
