using DocumentForge.Core;
using DocumentForge.Storage;

namespace DocumentForge.Index;

/// <summary>
/// Manages the page chain that persists an index's entries to disk.
/// Entries are appended as they happen; deletions are tombstones.
/// On load, the caller scans all entries to rebuild the in-memory index structure.
/// </summary>
public sealed class IndexStorage
{
    private readonly IPageCache _cache;
    private readonly IPageAllocator _allocator;
    private PageId _firstPage;
    private PageId _lastPage = PageId.Invalid;

    public PageId FirstPage => _firstPage;

    public IndexStorage(IPageCache cache, IPageAllocator allocator, PageId firstPage)
    {
        _cache = cache;
        _allocator = allocator;
        _firstPage = firstPage;
    }

    /// <summary>
    /// Allocate a brand-new storage for a new index. Returns the root page ID.
    /// </summary>
    public static IndexStorage CreateNew(IPageCache cache, IPageAllocator allocator)
    {
        var firstPage = allocator.AllocatePage(PageType.BTreeLeaf);
        return new IndexStorage(cache, allocator, firstPage);
    }

    /// <summary>
    /// Append an entry to the tail page. Allocates a new page if needed.
    /// </summary>
    public void AppendEntry(IndexKey key, DocumentId docId, bool isDeleted)
    {
        var bytes = IndexEntrySerializer.Serialize(key, docId, isDeleted);

        var pageId = _lastPage.IsValid ? _lastPage : _firstPage;
        bool triedFresh = false;

        while (true)
        {
            var pageData = _cache.GetPage(pageId);
            var page = new DataPage(pageData);

            int slot = page.Insert(bytes);
            if (slot >= 0)
            {
                _cache.PutPage(pageId, page.RawData, isDirty: true);
                _lastPage = pageId;
                return;
            }

            if (triedFresh)
                throw new DocumentForgeException("Index entry too large for a page.");

            // Page full - allocate new one and link
            if (!page.Header.NextPageId.IsValid)
            {
                var newPageId = _allocator.AllocatePage(PageType.BTreeLeaf);
                page.SetNextPage(newPageId);
                _cache.PutPage(pageId, page.RawData, isDirty: true);

                var newData = _cache.GetPage(newPageId);
                var newPage = new DataPage(newData);
                newPage.SetPrevPage(pageId);
                _cache.PutPage(newPageId, newPage.RawData, isDirty: true);

                pageId = newPageId;
                triedFresh = true;
            }
            else
            {
                pageId = page.Header.NextPageId;
            }
        }
    }

    /// <summary>
    /// Read all entries back (for index rebuild on open). Applies tombstones.
    /// Returns the FINAL state per (key, docId): for each pair the latest write
    /// in append order wins, so a tombstone that follows an insert correctly
    /// cancels it.
    /// </summary>
    public IEnumerable<(IndexKey Key, DocumentId DocId)> ReadAllEntries()
    {
        // Walk every persisted entry in append order and track the most recent
        // state for each (key, docId) pair. value=true means the pair is currently
        // inserted; false means tombstoned. Pre-fix the replay only filtered an
        // insert if a tombstone had ALREADY been seen earlier on the page, so the
        // common case (insert then later delete) left the insert in the rebuilt
        // index — which surfaced as phantom Duplicate-key errors after restart
        // when callers re-inserted the same business key. (issue #11)
        var state = new Dictionary<(IndexKey Key, DocumentId DocId), bool>(IndexEntryComparer.Instance);

        var pageId = _firstPage;
        while (pageId.IsValid)
        {
            var pageData = _cache.GetPage(pageId);
            var page = new DataPage(pageData);

            for (int i = 0; i < page.Header.ItemCount; i++)
            {
                if (page.IsSlotDeleted(i)) continue;
                var slotData = page.GetSlot(i);
                if (slotData.IsEmpty) continue;

                var (key, docId, isDeleted) = IndexEntrySerializer.Deserialize(slotData);
                state[(key, docId)] = !isDeleted;
            }

            // Track tail page
            if (!page.Header.NextPageId.IsValid)
                _lastPage = pageId;

            pageId = page.Header.NextPageId;
        }

        foreach (var kvp in state)
        {
            if (kvp.Value)
                yield return (kvp.Key.Key, kvp.Key.DocId);
        }
    }
}

internal sealed class IndexEntryComparer : IEqualityComparer<(IndexKey Key, DocumentId DocId)>
{
    public static readonly IndexEntryComparer Instance = new();
    public bool Equals((IndexKey Key, DocumentId DocId) x, (IndexKey Key, DocumentId DocId) y)
        => x.Key.Equals(y.Key) && x.DocId == y.DocId;
    public int GetHashCode((IndexKey Key, DocumentId DocId) obj)
        => HashCode.Combine(obj.Key.GetHashCode(), obj.DocId.GetHashCode());
}
