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

    public void Load()
    {
        _definitions.Clear();
        if (!_catalogPage.IsValid) return;

        var pageId = _catalogPage;
        // Issue #57: cycle guard. A torn write that leaves a corrupt NextPageId
        // pointing back into the chain (or to page 0) would otherwise loop here
        // forever during Open and hang the host process before any caller-visible
        // exception is raised.
        var visited = new HashSet<uint>();
        while (pageId.IsValid)
        {
            if (!visited.Add(pageId.Value))
                throw new PageCorruptionException(pageId,
                    $"cycle detected in index-catalog page chain after {visited.Count} pages.");

            var pageData = _cache.GetPage(pageId);
            var page = new DataPage(pageData);

            for (int i = 0; i < page.Header.ItemCount; i++)
            {
                if (page.IsSlotDeleted(i)) continue;
                var slotData = page.GetSlot(i);
                if (slotData.IsEmpty) continue;

                var def = DeserializeDefinition(slotData);
                if (def is not null) _definitions.Add(def);
            }

            pageId = page.Header.NextPageId;
        }
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

            // Stamp the link on the just-finished page and persist it before
            // moving on; the chain has to be valid as soon as we walk away.
            currentPage.SetNextPage(nextPageId);
            _cache.PutPage(currentPageId, currentPage.RawData, isDirty: true);

            currentPageId = nextPageId;
            currentPage = ResetCatalogPage(currentPageId);

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
