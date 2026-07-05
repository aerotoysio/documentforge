using DocumentForge.Core;

namespace DocumentForge.Storage;

/// <summary>
/// Slotted page: slot array grows down from header, data grows up from end of page.
/// Each slot is 4 bytes: 2-byte offset + 2-byte length.
/// </summary>
public sealed class DataPage
{
    private readonly byte[] _data;
    private PageHeader _header;

    public PageHeader Header => _header;
    public byte[] RawData => _data;

    public DataPage(byte[] data)
    {
        _data = data;
        _header = PageHeader.ReadFrom(data);
    }

    public static DataPage CreateNew(PageId pageId)
    {
        var data = new byte[Constants.PageSize];
        var header = PageHeader.CreateData(pageId);
        header.WriteTo(data);
        return new DataPage(data);
    }

    /// <summary>
    /// Insert data into the page. Returns the slot index, or -1 if not enough space.
    /// </summary>
    public int Insert(ReadOnlySpan<byte> itemData)
    {
        int slotSize = Constants.SlotEntrySize;
        int required = itemData.Length + slotSize;
        if (Header.FreeSpace < required)
            return -1;

        // Data grows from the end of the page upward
        ushort dataOffset = (ushort)(Header.FreeSpaceEnd - itemData.Length);
        itemData.CopyTo(_data.AsSpan(dataOffset));

        // Slot grows from after the header downward
        ushort slotOffset = Header.FreeSpaceStart;
        BitConverter.TryWriteBytes(_data.AsSpan(slotOffset), dataOffset);
        BitConverter.TryWriteBytes(_data.AsSpan(slotOffset + 2), (ushort)itemData.Length);

        _header.FreeSpaceStart = (ushort)(slotOffset + slotSize);
        _header.FreeSpaceEnd = dataOffset;
        int slotIndex = _header.ItemCount;
        _header.ItemCount++;

        FlushHeader();
        return slotIndex;
    }

    /// <summary>
    /// Read the data at the given slot index.
    /// </summary>
    public ReadOnlySpan<byte> GetSlot(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= Header.ItemCount)
            throw new ArgumentOutOfRangeException(nameof(slotIndex));

        int slotOffset = Constants.PageHeaderSize + (slotIndex * Constants.SlotEntrySize);
        ushort dataOffset = BitConverter.ToUInt16(_data, slotOffset);
        ushort dataLength = BitConverter.ToUInt16(_data, slotOffset + 2);

        if (dataLength == 0) // deleted slot
            return ReadOnlySpan<byte>.Empty;

        return _data.AsSpan(dataOffset, dataLength);
    }

    /// <summary>
    /// Mark a slot as deleted (set length to 0). Does not reclaim space.
    /// </summary>
    public void DeleteSlot(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= Header.ItemCount)
            throw new ArgumentOutOfRangeException(nameof(slotIndex));

        int slotOffset = Constants.PageHeaderSize + (slotIndex * Constants.SlotEntrySize);
        // Set length to 0 to mark as deleted
        BitConverter.TryWriteBytes(_data.AsSpan(slotOffset + 2), (ushort)0);
    }

    /// <summary>
    /// Check if a slot is deleted.
    /// </summary>
    public bool IsSlotDeleted(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= Header.ItemCount) return true;
        int slotOffset = Constants.PageHeaderSize + (slotIndex * Constants.SlotEntrySize);
        return BitConverter.ToUInt16(_data, slotOffset + 2) == 0;
    }

    /// <summary>
    /// How many bytes are wasted by deleted slots (tombstones).
    /// </summary>
    public int DeadSpace
    {
        get
        {
            int dead = 0;
            for (int i = 0; i < _header.ItemCount; i++)
            {
                int slotOffset = Constants.PageHeaderSize + (i * Constants.SlotEntrySize);
                ushort len = BitConverter.ToUInt16(_data, slotOffset + 2);
                if (len == 0)
                    dead += Constants.SlotEntrySize; // the slot entry itself is wasted too
                // Note: the actual data bytes are also wasted but trapped between live data
            }
            return dead;
        }
    }

    /// <summary>
    /// Compact the page: remove deleted slots, reclaim all dead space.
    /// Rewrites live data contiguously. Returns new slot index mapping.
    /// </summary>
    public Dictionary<int, int> Compact()
    {
        var liveItems = new List<(int OldSlot, byte[] Data)>();

        // Collect all live slot data
        for (int i = 0; i < _header.ItemCount; i++)
        {
            if (IsSlotDeleted(i)) continue;
            var data = GetSlot(i);
            if (data.IsEmpty) continue;
            liveItems.Add((i, data.ToArray()));
        }

        // Remember links
        var nextPage = _header.NextPageId;
        var prevPage = _header.PrevPageId;
        var pageId = _header.PageId;
        var pageType = _header.PageType;

        // Clear the page
        Array.Clear(_data);

        // Reinitialize header
        _header = PageHeader.CreateData(pageId);
        _header.PageType = pageType;
        _header.NextPageId = nextPage;
        _header.PrevPageId = prevPage;
        FlushHeader();

        // Re-insert all live items
        var oldToNew = new Dictionary<int, int>();
        foreach (var (oldSlot, itemData) in liveItems)
        {
            int newSlot = Insert(itemData);
            if (newSlot >= 0)
                oldToNew[oldSlot] = newSlot;
        }

        return oldToNew;
    }

    /// <summary>
    /// True if this page has no live data at all.
    /// </summary>
    public bool IsEmpty
    {
        get
        {
            for (int i = 0; i < _header.ItemCount; i++)
            {
                if (!IsSlotDeleted(i)) return false;
            }
            return true;
        }
    }

    public void SetNextPage(PageId nextPageId)
    {
        _header.NextPageId = nextPageId;
        FlushHeader();
    }

    /// <summary>
    /// Set the page type and persist it. Must be used instead of mutating a
    /// copy of <see cref="Header"/> then WriteTo-ing it: any later Insert /
    /// SetNextPage calls FlushHeader, which rewrites the in-struct header and
    /// would otherwise clobber a type set only in the raw bytes. (This was the
    /// latent bug that left catalog pages mistyped as Data — issue #93.)
    /// </summary>
    public void SetPageType(PageType pageType)
    {
        _header.PageType = pageType;
        FlushHeader();
    }

    public void SetPrevPage(PageId prevPageId)
    {
        _header.PrevPageId = prevPageId;
        FlushHeader();
    }

    private void FlushHeader()
    {
        _header.WriteTo(_data);
    }
}
