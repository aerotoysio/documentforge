using DocumentForge.Core;

namespace DocumentForge.Storage;

public interface IPageAllocator
{
    PageId AllocatePage(PageType pageType);
    void FreePage(PageId pageId);
}

public sealed class PageAllocator : IPageAllocator
{
    private readonly IDataFile _dataFile;
    private readonly IPageCache _cache;
    private readonly Queue<PageId> _freePages = new();

    public PageAllocator(IDataFile dataFile, IPageCache cache)
    {
        _dataFile = dataFile;
        _cache = cache;
    }

    public PageId AllocatePage(PageType pageType)
    {
        PageId pageId;
        if (_freePages.Count > 0)
        {
            pageId = _freePages.Dequeue();
        }
        else
        {
            pageId = _dataFile.AllocateNewPage();
        }

        // Initialize the page with the correct type
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
        _freePages.Enqueue(pageId);
        _cache.Evict(pageId);
    }
}
