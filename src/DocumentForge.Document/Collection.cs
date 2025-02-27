using DocumentForge.Core;
using DocumentForge.Storage;

namespace DocumentForge.Document;

/// <summary>
/// Document location: exactly where a document lives on disk.
/// This is the "direct address" - go straight to the right page and slot.
/// </summary>
public readonly struct DocumentLocation
{
    public PageId PageId { get; init; }
    public int SlotIndex { get; init; }
}

public sealed class Collection
{
    private readonly CollectionName _name;
    private readonly IPageCache _cache;
    private readonly IPageAllocator _allocator;
    private PageId _firstDataPage;
    private PageId _lastInsertPage = PageId.Invalid; // Skip straight to the tail - don't walk the chain
    private long _documentCount;

    // The direct-address map: O(1) lookup by document ID
    private readonly Dictionary<DocumentId, DocumentLocation> _locationMap = new();
    private bool _locationMapBuilt;

    public CollectionName Name => _name;
    public long DocumentCount => _documentCount;
    public PageId FirstDataPage => _firstDataPage;

    public Collection(CollectionName name, IPageCache cache, IPageAllocator allocator, PageId firstDataPage)
    {
        _name = name;
        _cache = cache;
        _allocator = allocator;
        _firstDataPage = firstDataPage;
    }

    /// <summary>
    /// Build the location map by scanning all documents once.
    /// Called lazily on first FindById or explicitly after opening a database.
    /// </summary>
    public void BuildLocationMap(bool force = false)
    {
        if (_locationMapBuilt && !force) return;

        _locationMap.Clear();
        _documentCount = 0;

        // Issue #57: track visited PageIds so a corrupt NextPageId — e.g. a
        // partial flush from a hard kill that leaves zeros at offset 11-14 —
        // can't steer this walker into an infinite cycle. Cycles return as
        // typed PageCorruptionException instead of hanging the host process.
        var visited = new HashSet<uint>();
        var currentPageId = _firstDataPage;
        while (currentPageId.IsValid)
        {
            if (!visited.Add(currentPageId.Value))
                throw new PageCorruptionException(currentPageId,
                    $"cycle detected in collection '{_name.Value}' page chain after {visited.Count} pages.");

            var pageData = _cache.GetPage(currentPageId);
            var page = new DataPage(pageData);

            for (int i = 0; i < page.Header.ItemCount; i++)
            {
                if (page.IsSlotDeleted(i)) continue;
                var slotData = page.GetSlot(i);
                if (slotData.IsEmpty) continue;

                try
                {
                    BsonDocument doc;
                    if (slotData.Length == OverflowStubSize && IsOverflowStub(slotData))
                    {
                        var fullBytes = ReadOverflowChain(slotData);
                        doc = BsonSerializer.Deserialize(fullBytes);
                    }
                    else
                    {
                        doc = BsonSerializer.Deserialize(slotData);
                    }
                    var id = doc.GetId();
                    if (!id.IsEmpty)
                    {
                        _locationMap[id] = new DocumentLocation { PageId = currentPageId, SlotIndex = i };
                        _documentCount++;
                    }
                }
                catch { /* skip corrupted */ }
            }

            currentPageId = page.Header.NextPageId;
        }

        _locationMapBuilt = true;
    }

    // Max doc size that fits on a single page (slot entry + data).
    // Reserve room for the slot, leaving 8156 bytes for data.
    private const int MaxSingleDocSize = Constants.PageSize - Constants.PageHeaderSize - Constants.SlotEntrySize;
    // Overflow stub: 12 bytes = [0xFFFFFFFF marker:4][totalLen:4][firstOverflowPage:4]
    private const int OverflowStubSize = 12;
    // Space available per overflow page for data payload
    private const int OverflowPayloadSize = Constants.PageSize - Constants.PageHeaderSize;

    public DocumentId Insert(BsonDocument doc)
    {
        doc.EnsureId();
        var id = doc.GetId();

        // Use pooled buffer to avoid GC pressure in the hot insert path
        var (pooledBuffer, length) = BsonSerializer.SerializeToPooled(doc);
        try
        {
            byte[] slotData;
            if (length > MaxSingleDocSize)
            {
                // Large doc: copy to a temp array only for overflow chain write
                var bytes = new byte[length];
                pooledBuffer.AsSpan(0, length).CopyTo(bytes);
                var firstOverflow = WriteOverflowChain(bytes);
                slotData = BuildOverflowStub(length, firstOverflow);
            }
            else
            {
                // Small doc: the slot data is just the first `length` bytes of pooledBuffer.
                // We need a byte[] because DataPage.Insert takes ReadOnlySpan but we reuse below.
                // Copy only the used portion (most docs are << page size).
                slotData = new byte[length];
                pooledBuffer.AsSpan(0, length).CopyTo(slotData);
            }
        // Hoisting the rest of the insert outside the try - but we still need pooledBuffer in scope
        // for the return at the end. Restructure:
        return InsertSlotData(id, slotData);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(pooledBuffer);
        }
    }

    private DocumentId InsertSlotData(DocumentId id, byte[] slotData)
    {

        if (!_firstDataPage.IsValid)
        {
            _firstDataPage = _allocator.AllocatePage(PageType.Data);
            _lastInsertPage = _firstDataPage;
        }

        // Start from the last page that had space - NOT from the beginning.
        // This is O(1) instead of O(pages) for append-heavy workloads.
        var currentPageId = _lastInsertPage.IsValid ? _lastInsertPage : _firstDataPage;

        // Safety: after allocating a fresh empty page, the doc MUST fit (we checked size above).
        // Track if we already tried a freshly allocated page.
        bool triedFreshPage = false;

        // Issue #57: bound the walk against a corrupt NextPageId loop. Without
        // this, a torn page header that cycles back into the chain prevents
        // triedFreshPage from ever flipping (we never reach an Invalid tail),
        // and Insert hangs forever instead of failing fast.
        var visited = new HashSet<uint>();

        while (true)
        {
            if (!visited.Add(currentPageId.Value))
                throw new PageCorruptionException(currentPageId,
                    $"cycle detected in collection '{_name.Value}' page chain during insert.");

            var pageData = _cache.GetPage(currentPageId);
            var page = new DataPage(pageData);

            int slotIndex = page.Insert(slotData);
            if (slotIndex >= 0)
            {
                _cache.PutPage(currentPageId, page.RawData, isDirty: true);
                _documentCount++;
                _lastInsertPage = currentPageId;
                _locationMap[id] = new DocumentLocation { PageId = currentPageId, SlotIndex = slotIndex };
                return id;
            }

            // Document didn't fit on a page we JUST allocated? That's a bug.
            if (triedFreshPage)
            {
                throw new DocumentForgeException(
                    $"Insert failed on freshly allocated page - slot size: {slotData.Length} bytes, page free: {page.Header.FreeSpace}");
            }

            // Current page is full - allocate a new one and link it
            if (!page.Header.NextPageId.IsValid)
            {
                var newPageId = _allocator.AllocatePage(PageType.Data);
                page.SetNextPage(newPageId);
                _cache.PutPage(currentPageId, page.RawData, isDirty: true);

                var newPageData = _cache.GetPage(newPageId);
                var newPage = new DataPage(newPageData);
                newPage.SetPrevPage(currentPageId);
                _cache.PutPage(newPageId, newPage.RawData, isDirty: true);

                currentPageId = newPageId;
                triedFreshPage = true; // next iteration must succeed
            }
            else
            {
                currentPageId = page.Header.NextPageId;
            }
        }
    }

    /// <summary>
    /// O(1) document lookup using the location map. Falls back to scan if map not built.
    /// </summary>
    public BsonDocument? FindById(DocumentId id)
    {
        // Fast path: direct addressing via location map
        if (_locationMap.TryGetValue(id, out var location))
        {
            return ReadDocumentAt(location);
        }

        // Slow path fallback: build map first, then try again
        if (!_locationMapBuilt)
        {
            BuildLocationMap();
            if (_locationMap.TryGetValue(id, out location))
                return ReadDocumentAt(location);
        }

        return null;
    }

    /// <summary>
    /// Read a document directly from its known physical location. O(1).
    /// Detects overflow stubs and follows the overflow chain.
    /// </summary>
    public BsonDocument? ReadDocumentAt(DocumentLocation location)
    {
        var pageData = _cache.GetPage(location.PageId);
        var page = new DataPage(pageData);

        if (page.IsSlotDeleted(location.SlotIndex))
            return null;

        var slotData = page.GetSlot(location.SlotIndex);
        if (slotData.IsEmpty) return null;

        try
        {
            // Is this an overflow stub? Check for 4-byte marker 0xFFFFFFFF
            if (slotData.Length == OverflowStubSize && IsOverflowStub(slotData))
            {
                var fullBytes = ReadOverflowChain(slotData);
                return BsonSerializer.Deserialize(fullBytes);
            }
            return BsonSerializer.Deserialize(slotData);
        }
        catch
        {
            return null;
        }
    }

    // --- Overflow support ---

    private static bool IsOverflowStub(ReadOnlySpan<byte> slotData)
    {
        return slotData.Length >= 4 &&
               slotData[0] == 0xFF && slotData[1] == 0xFF &&
               slotData[2] == 0xFF && slotData[3] == 0xFF;
    }

    private static byte[] BuildOverflowStub(int totalLen, PageId firstPage)
    {
        var stub = new byte[OverflowStubSize];
        stub[0] = 0xFF; stub[1] = 0xFF; stub[2] = 0xFF; stub[3] = 0xFF;
        BitConverter.TryWriteBytes(stub.AsSpan(4), totalLen);
        BitConverter.TryWriteBytes(stub.AsSpan(8), firstPage.Value);
        return stub;
    }

    /// <summary>
    /// Write a large byte array to a chain of overflow pages. Returns the first page ID.
    /// Each overflow page format: [PageHeader:32][chunk data]
    /// </summary>
    private PageId WriteOverflowChain(byte[] bytes)
    {
        int written = 0;
        PageId firstPage = PageId.Invalid;
        PageId prevPage = PageId.Invalid;

        while (written < bytes.Length)
        {
            int chunkSize = Math.Min(OverflowPayloadSize, bytes.Length - written);
            var pageId = _allocator.AllocatePage(PageType.Overflow);
            if (!firstPage.IsValid) firstPage = pageId;

            var pageData = _cache.GetPage(pageId);
            // Copy chunk data after the header
            bytes.AsSpan(written, chunkSize).CopyTo(pageData.AsSpan(Constants.PageHeaderSize));

            // Update header with chunk size in ItemCount (we use it to track chunk length)
            var header = PageHeader.ReadFrom(pageData);
            header.PageType = PageType.Overflow;
            header.ItemCount = (ushort)chunkSize;
            header.NextPageId = PageId.Invalid;
            header.PrevPageId = prevPage;
            header.WriteTo(pageData);

            _cache.PutPage(pageId, pageData, isDirty: true);

            // Link previous page's NextPageId to this one
            if (prevPage.IsValid)
            {
                var prevData = _cache.GetPage(prevPage);
                var prevHeader = PageHeader.ReadFrom(prevData);
                prevHeader.NextPageId = pageId;
                prevHeader.WriteTo(prevData);
                _cache.PutPage(prevPage, prevData, isDirty: true);
            }

            prevPage = pageId;
            written += chunkSize;
        }

        return firstPage;
    }

    /// <summary>
    /// Read back a document from its overflow stub, following the chain.
    /// </summary>
    private byte[] ReadOverflowChain(ReadOnlySpan<byte> stub)
    {
        int totalLen = BitConverter.ToInt32(stub[4..]);
        var firstPage = new PageId(BitConverter.ToUInt32(stub[8..]));

        var result = new byte[totalLen];
        int written = 0;
        var currentPage = firstPage;

        // Issue #57: cycle guard. A corrupt overflow header with ItemCount=0
        // would not advance `written`; combined with a NextPageId that loops,
        // this walk hangs without bound. Throw on revisit instead.
        var visited = new HashSet<uint>();

        while (currentPage.IsValid && written < totalLen)
        {
            if (!visited.Add(currentPage.Value))
                throw new PageCorruptionException(currentPage,
                    $"cycle detected in overflow chain in collection '{_name.Value}'.");

            var pageData = _cache.GetPage(currentPage);
            var header = PageHeader.ReadFrom(pageData);
            int chunkSize = Math.Min(header.ItemCount, totalLen - written);
            pageData.AsSpan(Constants.PageHeaderSize, chunkSize).CopyTo(result.AsSpan(written));
            written += chunkSize;
            currentPage = header.NextPageId;
        }

        return result;
    }

    /// <summary>
    /// Free all pages in an overflow chain starting at firstPage.
    /// </summary>
    private void FreeOverflowChain(PageId firstPage)
    {
        var current = firstPage;
        // Issue #57: cycle guard — a corrupt cycle would otherwise re-free the
        // same page repeatedly, leading to allocator-state divergence on disk.
        var visited = new HashSet<uint>();
        while (current.IsValid)
        {
            if (!visited.Add(current.Value))
                throw new PageCorruptionException(current,
                    $"cycle detected in overflow chain during free in collection '{_name.Value}'.");

            var pageData = _cache.GetPage(current);
            var header = PageHeader.ReadFrom(pageData);
            var next = header.NextPageId;
            _allocator.FreePage(current);
            current = next;
        }
    }

    public bool Delete(DocumentId id)
    {
        // Fast path: use location map
        if (_locationMap.TryGetValue(id, out var location))
        {
            var pageData = _cache.GetPage(location.PageId);
            var page = new DataPage(pageData);

            // If this doc uses overflow pages, free them
            var slotData = page.GetSlot(location.SlotIndex);
            if (slotData.Length == OverflowStubSize && IsOverflowStub(slotData))
            {
                var firstOverflowPage = new PageId(BitConverter.ToUInt32(slotData[8..]));
                FreeOverflowChain(firstOverflowPage);
            }

            page.DeleteSlot(location.SlotIndex);
            _cache.PutPage(location.PageId, page.RawData, isDirty: true);
            _locationMap.Remove(id);
            _documentCount--;
            return true;
        }

        // Slow path: scan
        var currentPageId = _firstDataPage;
        // Issue #57: cycle guard for the scan path — same hang risk as BuildLocationMap.
        var visited = new HashSet<uint>();
        while (currentPageId.IsValid)
        {
            if (!visited.Add(currentPageId.Value))
                throw new PageCorruptionException(currentPageId,
                    $"cycle detected in collection '{_name.Value}' page chain during delete scan.");

            var pageData = _cache.GetPage(currentPageId);
            var page = new DataPage(pageData);

            for (int i = 0; i < page.Header.ItemCount; i++)
            {
                if (page.IsSlotDeleted(i)) continue;

                var slotData = page.GetSlot(i);
                var doc = BsonSerializer.Deserialize(slotData);
                if (doc.GetId() == id)
                {
                    page.DeleteSlot(i);
                    _cache.PutPage(currentPageId, page.RawData, isDirty: true);
                    _locationMap.Remove(id);
                    _documentCount--;
                    return true;
                }
            }

            currentPageId = page.Header.NextPageId;
        }
        return false;
    }

    public bool Update(DocumentId id, BsonDocument newDoc)
    {
        if (Delete(id))
        {
            newDoc.SetId(id);
            Insert(newDoc);
            return true;
        }
        return false;
    }

    public IEnumerable<BsonDocument> FindAll()
    {
        foreach (var (doc, _, _) in IterateDocuments())
        {
            yield return doc;
        }
    }

    public IEnumerable<(BsonDocument Doc, PageId PageId, int SlotIndex)> IterateDocuments()
    {
        var currentPageId = _firstDataPage;
        // Issue #57: cycle guard for the iterator path.
        var visited = new HashSet<uint>();
        while (currentPageId.IsValid)
        {
            if (!visited.Add(currentPageId.Value))
                throw new PageCorruptionException(currentPageId,
                    $"cycle detected in collection '{_name.Value}' page chain during iteration.");

            var pageData = _cache.GetPage(currentPageId);
            var page = new DataPage(pageData);

            for (int i = 0; i < page.Header.ItemCount; i++)
            {
                if (page.IsSlotDeleted(i)) continue;

                var slotData = page.GetSlot(i);
                if (slotData.IsEmpty) continue;

                BsonDocument doc;
                try
                {
                    // Handle overflow stubs
                    if (slotData.Length == OverflowStubSize && IsOverflowStub(slotData))
                    {
                        var fullBytes = ReadOverflowChain(slotData);
                        doc = BsonSerializer.Deserialize(fullBytes);
                    }
                    else
                    {
                        doc = BsonSerializer.Deserialize(slotData);
                    }
                }
                catch
                {
                    continue;
                }
                yield return (doc, currentPageId, i);
            }

            currentPageId = page.Header.NextPageId;
        }
    }
}
