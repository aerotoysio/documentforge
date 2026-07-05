using DocumentForge.Core;
using DocumentForge.Storage;

namespace DocumentForge.Document;

/// <summary>
/// Manages the catalog of collections. Stores collection metadata
/// (name -> first data page mapping) in the catalog page.
/// </summary>
public sealed class CollectionCatalog
{
    private readonly IPageCache _cache;
    private readonly IPageAllocator _allocator;
    private readonly Dictionary<string, CollectionInfo> _collections = new();

    public IReadOnlyDictionary<string, CollectionInfo> Collections => _collections;

    public sealed class CollectionInfo
    {
        public CollectionName Name { get; init; }
        public PageId FirstDataPage { get; set; }
        public long DocumentCount { get; set; }
    }

    public CollectionCatalog(IPageCache cache, IPageAllocator allocator)
    {
        _cache = cache;
        _allocator = allocator;
    }

    public void Load()
    {
        // Walk the catalog page chain (issue #93 — the catalog spills past the
        // first 8 KB page via each page's NextPageId). Page 1 is the fixed
        // head; overflow pages follow. We read the head unconditionally (older
        // files mistyped it as Data — see SaveCatalog), but require every
        // FOLLOWING page to be a genuine CollectionCatalog page so a stale
        // NextPageId left dangling by a crash can't be read as catalog entries.
        var visited = new HashSet<uint>();
        var cursor = PageId.CollectionCatalog;
        bool isHead = true;
        while (cursor.IsValid)
        {
            if (!visited.Add(cursor.Value)) break; // cycle guard

            var page = new DataPage(_cache.GetPage(cursor));
            if (!isHead && page.Header.PageType != PageType.CollectionCatalog)
                break; // dangling/reallocated overflow page — stop

            for (int i = 0; i < page.Header.ItemCount; i++)
            {
                if (page.IsSlotDeleted(i)) continue;
                var slotData = page.GetSlot(i);
                if (slotData.IsEmpty) continue;

                // Format: [NameLen:2][Name:N][FirstPageId:4][DocCount:8]
                int offset = 0;
                var nameLen = BitConverter.ToInt16(slotData[offset..]);
                offset += 2;
                var name = System.Text.Encoding.UTF8.GetString(slotData.Slice(offset, nameLen));
                offset += nameLen;
                var firstPage = new PageId(BitConverter.ToUInt32(slotData[offset..]));
                offset += 4;
                var docCount = BitConverter.ToInt64(slotData[offset..]);

                _collections[name] = new CollectionInfo
                {
                    Name = new CollectionName(name),
                    FirstDataPage = firstPage,
                    DocumentCount = docCount
                };
            }

            cursor = page.Header.NextPageId;
            isHead = false;
        }
    }

    // Cache Collection instances so the location map and _lastInsertPage persist across calls
    private readonly Dictionary<string, Collection> _instanceCache = new();

    public Collection GetOrCreateCollection(string name)
    {
        var collName = new CollectionName(name);

        // Return cached instance if we already have one
        if (_instanceCache.TryGetValue(collName.Value, out var cached))
            return cached;

        if (_collections.TryGetValue(collName.Value, out var info))
        {
            var coll = new Collection(collName, _cache, _allocator, info.FirstDataPage);
            _instanceCache[collName.Value] = coll;
            return coll;
        }

        // Create new collection - allocate first data page
        var firstPage = _allocator.AllocatePage(PageType.Data);
        info = new CollectionInfo
        {
            Name = collName,
            FirstDataPage = firstPage,
            DocumentCount = 0
        };
        _collections[collName.Value] = info;
        SaveCatalog();

        var newColl = new Collection(collName, _cache, _allocator, firstPage);
        _instanceCache[collName.Value] = newColl;
        return newColl;
    }

    public Collection? GetCollection(string name)
    {
        var collName = new CollectionName(name);

        // Return cached instance if we already have one
        if (_instanceCache.TryGetValue(collName.Value, out var cached))
            return cached;

        if (!_collections.TryGetValue(collName.Value, out var info))
            return null;

        var coll = new Collection(collName, _cache, _allocator, info.FirstDataPage);
        _instanceCache[collName.Value] = coll;
        return coll;
    }

    public IReadOnlyList<string> GetCollectionNames()
    {
        return _collections.Keys.ToList();
    }

    public bool DropCollection(string name)
    {
        var collName = new CollectionName(name);
        if (!_collections.Remove(collName.Value))
            return false;
        _instanceCache.Remove(collName.Value);
        SaveCatalog();
        return true;
    }

    public void UpdateCollectionInfo(string name, PageId firstDataPage, long documentCount)
    {
        var collName = new CollectionName(name);
        if (_collections.TryGetValue(collName.Value, out var info))
        {
            info.FirstDataPage = firstDataPage;
            info.DocumentCount = documentCount;
            SaveCatalog();
        }
    }

    private static byte[] SerializeEntry(CollectionInfo info)
    {
        // Format: [NameLen:2][Name:N][FirstPageId:4][DocCount:8]
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(info.Name.Value);
        var entry = new byte[2 + nameBytes.Length + 4 + 8];
        int offset = 0;
        BitConverter.TryWriteBytes(entry.AsSpan(offset), (short)nameBytes.Length);
        offset += 2;
        nameBytes.CopyTo(entry.AsSpan(offset));
        offset += nameBytes.Length;
        BitConverter.TryWriteBytes(entry.AsSpan(offset), info.FirstDataPage.Value);
        offset += 4;
        BitConverter.TryWriteBytes(entry.AsSpan(offset), info.DocumentCount);
        return entry;
    }

    /// <summary>The catalog page chain as it exists on disk right now: page 1
    /// (the fixed head) followed by each overflow page's NextPageId. Cycle- and
    /// type-guarded so a corrupt chain can't spin or drag in stray pages.</summary>
    private List<PageId> GatherChainPageIds()
    {
        var pages = new List<PageId> { PageId.CollectionCatalog };
        var visited = new HashSet<uint> { PageId.CollectionCatalog.Value };
        var next = new DataPage(_cache.GetPage(PageId.CollectionCatalog)).Header.NextPageId;
        while (next.IsValid && visited.Add(next.Value))
        {
            var page = new DataPage(_cache.GetPage(next));
            if (page.Header.PageType != PageType.CollectionCatalog) break;
            pages.Add(next);
            next = page.Header.NextPageId;
        }
        return pages;
    }

    /// <summary>
    /// Persist the whole catalog across a page chain (issue #93). Entries that
    /// overflow the current page spill onto the next: pre-fix, <c>Insert</c>'s
    /// -1 "page full" return was ignored, so past ~8 KB of catalog entries
    /// collections were silently dropped and their data pages orphaned. Existing
    /// chain pages are reused in place; surplus pages are freed when the catalog
    /// shrinks. Durability: the pages go through the cache and are sealed by the
    /// enclosing write op's WAL commit (issues #89/#92 cover fsync-on-mutation
    /// and torn-write repair), so a single torn catalog page is recoverable.
    /// </summary>
    private void SaveCatalog()
    {
        var entries = _collections.Values.Select(SerializeEntry).ToList();
        var existing = GatherChainPageIds();

        DataPage NewCatalogPage(int pageIndex)
        {
            var pid = pageIndex < existing.Count
                ? existing[pageIndex]
                : _allocator.AllocatePage(PageType.CollectionCatalog);
            var p = DataPage.CreateNew(pid);
            p.SetPageType(PageType.CollectionCatalog); // persists through later Insert/SetNextPage
            return p;
        }

        var pages = new List<DataPage> { NewCatalogPage(0) };
        int ei = 0;
        while (ei < entries.Count)
        {
            var page = pages[^1];
            if (page.Insert(entries[ei]) < 0)
            {
                if (page.Header.ItemCount == 0)
                    throw new InvalidOperationException(
                        $"Collection catalog entry is {entries[ei].Length} bytes — too large for an " +
                        $"empty {Constants.PageSize}-byte page. Collection name is unreasonably long.");
                pages.Add(NewCatalogPage(pages.Count)); // spill to a fresh page
                continue; // retry the same entry on the new page
            }
            ei++;
        }

        // Link the chain (last page terminates it) and persist every page.
        for (int i = 0; i < pages.Count; i++)
        {
            pages[i].SetNextPage(i + 1 < pages.Count ? pages[i + 1].Header.PageId : PageId.Invalid);
            _cache.PutPage(pages[i].Header.PageId, pages[i].RawData, isDirty: true);
        }

        // Free any pages the catalog no longer needs (it shrank).
        for (int i = pages.Count; i < existing.Count; i++)
            _allocator.FreePage(existing[i]);
    }
}
