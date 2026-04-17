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
        var catalogData = _cache.GetPage(PageId.CollectionCatalog);
        var page = new DataPage(catalogData);

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

    private void SaveCatalog()
    {
        // Rewrite the entire catalog page
        var page = DataPage.CreateNew(PageId.CollectionCatalog);
        var header = page.Header;
        header.PageType = PageType.CollectionCatalog;
        header.WriteTo(page.RawData);

        foreach (var (_, info) in _collections)
        {
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(info.Name.Value);
            var entrySize = 2 + nameBytes.Length + 4 + 8;
            var entry = new byte[entrySize];
            int offset = 0;

            BitConverter.TryWriteBytes(entry.AsSpan(offset), (short)nameBytes.Length);
            offset += 2;
            nameBytes.CopyTo(entry.AsSpan(offset));
            offset += nameBytes.Length;
            BitConverter.TryWriteBytes(entry.AsSpan(offset), info.FirstDataPage.Value);
            offset += 4;
            BitConverter.TryWriteBytes(entry.AsSpan(offset), info.DocumentCount);

            page.Insert(entry);
        }

        _cache.PutPage(PageId.CollectionCatalog, page.RawData, isDirty: true);
    }
}
