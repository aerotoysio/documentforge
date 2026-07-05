using DocumentForge.Core;

namespace DocumentForge.Storage;

public interface IPageAllocator
{
    PageId AllocatePage(PageType pageType);
    void FreePage(PageId pageId);
}

/// <summary>
/// Allocates and reclaims pages. The free list is persisted (issue #94) as an
/// intrusive singly-linked chain: each freed page is marked
/// <see cref="PageType.FreeList"/> and stores the next free page's id in its
/// header's <see cref="PageHeader.NextPageId"/>; the chain head lives in the
/// file header (<see cref="IDataFile.GetFreeListHead"/>). So a page freed by a
/// delete/compaction is reused after a restart instead of the data file
/// growing forever.
///
/// <para>Crash-safety: the head pointer is only made durable at flush, so a
/// crash between flushes can leak reclaimable pages but can never
/// double-allocate — the loader walks the chain defensively and stops at the
/// first page that isn't a valid, in-range, uniquely-visited FreeList page
/// (e.g. one that was reallocated and rewritten with another type). A leaked
/// page is simply never reused; it is never handed out twice.</para>
/// </summary>
public sealed class PageAllocator : IPageAllocator
{
    private readonly IDataFile _dataFile;
    private readonly IPageCache _cache;

    // Working copy of the free list, top == the persisted chain head. Membership
    // set doubles as the O(1) double-free guard.
    private readonly Stack<PageId> _free = new();
    private readonly HashSet<uint> _freeSet = new();

    public PageAllocator(IDataFile dataFile, IPageCache cache)
    {
        _dataFile = dataFile;
        _cache = cache;
        LoadFreeList();
    }

    /// <summary>Current number of reclaimable pages tracked in memory.</summary>
    public int FreePageCount => _freeSet.Count;

    private void LoadFreeList()
    {
        // Walk head → next → … collecting valid FreeList pages. Push in reverse
        // so the stack top ends up as the chain head (matches Free/Allocate).
        var ordered = new List<PageId>();
        var visited = new HashSet<uint>();
        var cursor = _dataFile.GetFreeListHead();
        while (cursor.IsValid)
        {
            if (cursor.Value == 0 || cursor.Value >= _dataFile.PageCount) break; // out of range / header
            if (!visited.Add(cursor.Value)) break;                                // cycle
            if (!_dataFile.TryReadPage(cursor, out var buf)) break;               // torn free page
            var header = PageHeader.ReadFrom(buf);
            if (header.PageType != PageType.FreeList) break;                       // reallocated / stale tail
            ordered.Add(cursor);
            cursor = header.NextPageId;
        }

        for (int i = ordered.Count - 1; i >= 0; i--)
        {
            _free.Push(ordered[i]);
            _freeSet.Add(ordered[i].Value);
        }
        // If we truncated the chain, re-anchor the persisted head to what we
        // actually accepted so the on-disk head can't dangle past a bad tail.
        _dataFile.SetFreeListHead(_free.Count > 0 ? _free.Peek() : PageId.Invalid);
    }

    public PageId AllocatePage(PageType pageType)
    {
        PageId pageId;
        if (_free.Count > 0)
        {
            pageId = _free.Pop();
            _freeSet.Remove(pageId.Value);
            // New head = the next page down the chain (persisted lazily).
            _dataFile.SetFreeListHead(_free.Count > 0 ? _free.Peek() : PageId.Invalid);
        }
        else
        {
            pageId = _dataFile.AllocateNewPage();
        }

        // Initialize the page with the correct type. This overwrites the
        // FreeList marker, so even if the head-pointer update above is lost to
        // a crash, the reload's PageType check refuses to re-hand-out this page.
        var data = new byte[Constants.PageSize];
        var header = new PageHeader
        {
            PageId = pageId,
            PageType = pageType,
            ItemCount = 0,
            FreeSpaceStart = (ushort)Constants.PageHeaderSize,
            FreeSpaceEnd = (ushort)Constants.PageSize,
            NextPageId = PageId.Invalid,
            PrevPageId = PageId.Invalid,
        };
        header.WriteTo(data);
        _cache.PutPage(pageId, data, isDirty: true);

        return pageId;
    }

    public void FreePage(PageId pageId)
    {
        // Double-free guard: a page already on the free list must never be
        // pushed again — that would create a cycle and, worse, hand the same
        // page out twice on a later allocate.
        if (!pageId.IsValid || pageId.Value == 0) return;
        if (!_freeSet.Add(pageId.Value)) return; // already free — ignore

        var prevHead = _free.Count > 0 ? _free.Peek() : PageId.Invalid;

        // Rewrite the page as a FreeList node linking to the previous head.
        var data = new byte[Constants.PageSize];
        var header = new PageHeader
        {
            PageId = pageId,
            PageType = PageType.FreeList,
            ItemCount = 0,
            FreeSpaceStart = (ushort)Constants.PageHeaderSize,
            FreeSpaceEnd = (ushort)Constants.PageSize,
            NextPageId = prevHead,
            PrevPageId = PageId.Invalid,
        };
        header.WriteTo(data);
        // Stage the free node, then evict it: Evict writes it through to the
        // data file (and the WAL), so the chain node is persisted with the
        // rest of this flush window. Dropping it from cache also reclaims the
        // memory — a freed page has no reason to stay resident.
        _cache.PutPage(pageId, data, isDirty: true);
        _cache.Evict(pageId);

        _free.Push(pageId);
        _dataFile.SetFreeListHead(pageId);
    }
}
