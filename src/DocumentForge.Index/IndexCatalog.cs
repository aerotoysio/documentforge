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
        while (pageId.IsValid)
        {
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

        // Ensure we have a catalog page
        if (!_catalogPage.IsValid)
        {
            _catalogPage = _allocator.AllocatePage(PageType.CollectionCatalog);
        }

        // Reset the catalog page: clear header and rewrite entries
        var pageData = _cache.GetPage(_catalogPage);
        var header = PageHeader.CreateData(_catalogPage);
        header.PageType = PageType.CollectionCatalog;
        Array.Clear(pageData);
        header.WriteTo(pageData);
        var page = new DataPage(pageData);

        foreach (var def in _definitions)
        {
            var bytes = SerializeDefinition(def);
            int slot = page.Insert(bytes);
            if (slot < 0)
                throw new DocumentForgeException("Index catalog overflow - too many indexes for one page (TODO: multi-page).");
        }

        _cache.PutPage(_catalogPage, page.RawData, isDirty: true);
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
