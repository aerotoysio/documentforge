using DocumentForge.Core;
using DocumentForge.Storage;

namespace DocumentForge.Index;

/// <summary>
/// Persists index definitions (metadata) to a dedicated catalog page.
/// Format per entry:
///   [NameLen:2][NameBytes][CollLen:2][CollBytes][PathLen:2][PathBytes]
///   [IsUnique:1][RootPage:4]
/// </summary>
public sealed class IndexCatalog
{
    private readonly IPageCache _cache;
    private readonly IPageAllocator _allocator;
    private PageId _catalogPage;
    private readonly List<IndexDefinition> _definitions = new();

    public IReadOnlyList<IndexDefinition> Definitions => _definitions;
    public PageId CatalogPage => _catalogPage;

    public IndexCatalog(IPageCache cache, IPageAllocator allocator, PageId catalogPage)
    {
        _cache = cache;
        _allocator = allocator;
        _catalogPage = catalogPage;
    }

    /// <summary>
    /// Attempt to load the catalog from disk. Returns true on success;
    /// returns false (with <paramref name="corruptionReason"/> populated)
    /// when the page chain is structurally corrupt (Issue #64 — cycle on
    /// disk that survives a crash). On failure <see cref="Definitions"/>
    /// is left empty so the engine can decide whether to surface the
    /// error or fall back to a self-heal path.
    /// </summary>
    public bool TryLoad(out string? corruptionReason)
    {
        corruptionReason = null;
        _definitions.Clear();
        if (!_catalogPage.IsValid) return true;

        // Walk into a staging list. Only commit to _definitions after a
        // clean traversal — a half-loaded catalog with the rest discarded
        // would be worse than no catalog at all.
        var staged = new List<IndexDefinition>();
        var visited = new HashSet<uint>();
        var pageId = _catalogPage;

        while (pageId.IsValid)
        {
            if (!visited.Add(pageId.Value))
            {
                corruptionReason =
                    $"cycle detected in index-catalog page chain at page {pageId.Value} " +
                    $"after {visited.Count} pages.";
                return false;
            }

            var pageData = _cache.GetPage(pageId);
            var page = new DataPage(pageData);

            for (int i = 0; i < page.Header.ItemCount; i++)
            {
                if (page.IsSlotDeleted(i)) continue;
                var slotData = page.GetSlot(i);
                if (slotData.IsEmpty) continue;

                var def = DeserializeDefinition(slotData);
                if (def is not null) staged.Add(def);
            }

            pageId = page.Header.NextPageId;
        }

        _definitions.AddRange(staged);
        return true;
    }

    /// <summary>
    /// Convenience overload that throws <see cref="PageCorruptionException"/>
    /// when the chain is corrupt. Kept for callers that have no recovery
    /// path (and to make the behaviour change surgical for callers that do).
    /// </summary>
    public void Load()
    {
        if (TryLoad(out var reason))
            return;
        throw new PageCorruptionException(_catalogPage, reason ?? "index catalog corrupt.");
    }

    /// <summary>
    /// Issue #64: wipe the catalog head pointer and the in-memory definition
    /// list. Used by the self-heal path on Open when <see cref="TryLoad"/>
    /// reports corruption. The previous catalog pages become orphaned (we
    /// can't safely walk them — that's how we got here) but the rest of the
    /// data file is untouched and a subsequent <see cref="Save"/> will
    /// allocate a fresh head page. Operators recreate indexes via
    /// <c>CreateIndex</c>; document data is preserved.
    /// </summary>
    public void Reset()
    {
        _definitions.Clear();
        _catalogPage = PageId.Invalid;
    }

    public void Save(IEnumerable<IndexDefinition> definitions)
    {
        _definitions.Clear();
        _definitions.AddRange(definitions);

        // Walk the existing chain so we can reuse its pages instead of leaking
        // them. The catalog grows and shrinks over time (CreateIndex / DropIndex)
        // and the previous Save left exactly the chain we need to overwrite.
        // Pages we don't end up needing get freed at the end.
        var existing = new List<PageId>();
        // Issue #57: cycle guard — same risk profile as Load.
        var existingVisited = new HashSet<uint>();
        if (_catalogPage.IsValid)
        {
            var p = _catalogPage;
            while (p.IsValid)
            {
                if (!existingVisited.Add(p.Value))
                    throw new PageCorruptionException(p,
                        $"cycle detected in index-catalog page chain during save after {existing.Count} pages.");
                existing.Add(p);
                var data = _cache.GetPage(p);
                p = new DataPage(data).Header.NextPageId;
            }
        }

        // Ensure the head page exists. If this is the first Save ever (catalog
        // page Invalid), allocate it now; otherwise reuse the head from the
        // existing chain.
        if (!_catalogPage.IsValid)
        {
            _catalogPage = _allocator.AllocatePage(PageType.CollectionCatalog);
            existing.Add(_catalogPage);
        }

        int chainIndex = 0;
        var currentPageId = existing[chainIndex];
        var currentPage = ResetCatalogPage(currentPageId);

        foreach (var def in _definitions)
        {
            var bytes = SerializeDefinition(def);
            int slot = currentPage.Insert(bytes);
            if (slot >= 0) continue;

            // Page full. Either reuse the next existing chain page or allocate
            // a fresh one, link from the current page, and switch to it.
            chainIndex++;
            PageId nextPageId;
            if (chainIndex < existing.Count)
            {
                nextPageId = existing[chainIndex];
            }
            else
            {
                nextPageId = _allocator.AllocatePage(PageType.CollectionCatalog);
                existing.Add(nextPageId);
            }

            // Issue #64: persist the new tail page in a known-good (empty)
            // state BEFORE stamping the link onto the previous page. If a
            // crash lands between the two writes, the old tail still says
            // NextPageId=Invalid and the freshly-cleared B is durable;
            // either way Load sees a coherent chain. The reverse order
            // (link A first, persist B's reset later) is what produced the
            // disk-cycle reports in #64: a freed page returning from the
            // allocator with stale content could carry a leftover
            // NextPageId looping back into the chain.
            var nextPage = ResetCatalogPage(nextPageId);
            _cache.PutPage(nextPageId, nextPage.RawData, isDirty: true);

            // Now safe to link forwards.
            currentPage.SetNextPage(nextPageId);
            _cache.PutPage(currentPageId, currentPage.RawData, isDirty: true);

            currentPageId = nextPageId;
            currentPage = nextPage;

            int retry = currentPage.Insert(bytes);
            if (retry < 0)
                // Single definition larger than a whole page. Names + paths
                // would have to be enormous to hit this. Keep the throw so we
                // notice if it ever becomes real.
                throw new DocumentForgeException(
                    $"Index catalog: definition '{def.Name}' is too large to fit in a single page.");
        }

        // Persist the final page and clear NextPageId on it so the chain ends
        // cleanly even if a previous Save had a longer tail.
        currentPage.SetNextPage(PageId.Invalid);
        _cache.PutPage(currentPageId, currentPage.RawData, isDirty: true);

        // Free any leftover pages from the prior chain that we no longer need.
        // (catalog shrunk — e.g. a DropIndex reduced the def count.)
        for (int i = chainIndex + 1; i < existing.Count; i++)
            _allocator.FreePage(existing[i]);
    }

    private DataPage ResetCatalogPage(PageId pageId)
    {
        var pageData = _cache.GetPage(pageId);
        Array.Clear(pageData);
        var header = PageHeader.CreateData(pageId);
        header.PageType = PageType.CollectionCatalog;
        header.WriteTo(pageData);
        return new DataPage(pageData);
    }

    private static byte[] SerializeDefinition(IndexDefinition def)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        var nameBytes = System.Text.Encoding.UTF8.GetBytes(def.Name);
        var collBytes = System.Text.Encoding.UTF8.GetBytes(def.CollectionName.Value);
        var pathBytes = System.Text.Encoding.UTF8.GetBytes(def.JsonPath);

        w.Write((short)nameBytes.Length);
        w.Write(nameBytes);
        w.Write((short)collBytes.Length);
        w.Write(collBytes);
        w.Write((short)pathBytes.Length);
        w.Write(pathBytes);
        w.Write(def.IsUnique);
        w.Write(def.RootPage.Value);

        return ms.ToArray();
    }

    private static IndexDefinition? DeserializeDefinition(ReadOnlySpan<byte> data)
    {
        try
        {
            int offset = 0;
            var nameLen = BitConverter.ToInt16(data[offset..]); offset += 2;
            var name = System.Text.Encoding.UTF8.GetString(data.Slice(offset, nameLen)); offset += nameLen;
            var collLen = BitConverter.ToInt16(data[offset..]); offset += 2;
            var coll = System.Text.Encoding.UTF8.GetString(data.Slice(offset, collLen)); offset += collLen;
            var pathLen = BitConverter.ToInt16(data[offset..]); offset += 2;
            var path = System.Text.Encoding.UTF8.GetString(data.Slice(offset, pathLen)); offset += pathLen;
            var isUnique = data[offset] != 0; offset += 1;
            var rootPage = new PageId(BitConverter.ToUInt32(data[offset..]));

            return new IndexDefinition
            {
                Name = name,
                CollectionName = new CollectionName(coll),
                JsonPath = path,
                IsUnique = isUnique,
                RootPage = rootPage
            };
        }
        catch { return null; }
    }
}
