using DocumentForge.Core;

namespace DocumentForge.Storage;

/// <summary>
/// 32-byte header at the start of every page.
/// </summary>
public struct PageHeader
{
    public PageId PageId;          // 4 bytes (offset 0)
    public PageType PageType;      // 1 byte  (offset 4)
    public ushort ItemCount;       // 2 bytes (offset 5)
    public ushort FreeSpaceStart;  // 2 bytes (offset 7) - end of slot array
    public ushort FreeSpaceEnd;    // 2 bytes (offset 9) - start of data region (grows from end)
    public PageId NextPageId;      // 4 bytes (offset 11)
    public PageId PrevPageId;      // 4 bytes (offset 15)
    public uint TransactionId;     // 4 bytes (offset 19)
    public byte Flags;             // 1 byte  (offset 23)
    public uint Checksum;          // 4 bytes (offset 24)
    // 4 bytes reserved            // (offset 28-31)

    public int FreeSpace => FreeSpaceEnd - FreeSpaceStart;

    public void WriteTo(Span<byte> buffer)
    {
        BitConverter.TryWriteBytes(buffer[0..], PageId.Value);
        buffer[4] = (byte)PageType;
        BitConverter.TryWriteBytes(buffer[5..], ItemCount);
        BitConverter.TryWriteBytes(buffer[7..], FreeSpaceStart);
        BitConverter.TryWriteBytes(buffer[9..], FreeSpaceEnd);
        BitConverter.TryWriteBytes(buffer[11..], NextPageId.Value);
        BitConverter.TryWriteBytes(buffer[15..], PrevPageId.Value);
        BitConverter.TryWriteBytes(buffer[19..], TransactionId);
        buffer[23] = Flags;
        BitConverter.TryWriteBytes(buffer[24..], Checksum);
        // bytes 28-31 reserved (zeros)
        buffer[28] = 0; buffer[29] = 0; buffer[30] = 0; buffer[31] = 0;
    }

    public static PageHeader ReadFrom(ReadOnlySpan<byte> buffer)
    {
        return new PageHeader
        {
            PageId = new PageId(BitConverter.ToUInt32(buffer[0..])),
            PageType = (PageType)buffer[4],
            ItemCount = BitConverter.ToUInt16(buffer[5..]),
            FreeSpaceStart = BitConverter.ToUInt16(buffer[7..]),
            FreeSpaceEnd = BitConverter.ToUInt16(buffer[9..]),
            NextPageId = new PageId(BitConverter.ToUInt32(buffer[11..])),
            PrevPageId = new PageId(BitConverter.ToUInt32(buffer[15..])),
            TransactionId = BitConverter.ToUInt32(buffer[19..]),
            Flags = buffer[23],
            Checksum = BitConverter.ToUInt32(buffer[24..])
        };
    }

    public static PageHeader CreateData(PageId pageId)
    {
        return new PageHeader
        {
            PageId = pageId,
            PageType = PageType.Data,
            ItemCount = 0,
            FreeSpaceStart = (ushort)Constants.PageHeaderSize,
            FreeSpaceEnd = (ushort)Constants.PageSize,
            NextPageId = PageId.Invalid,
            PrevPageId = PageId.Invalid,
        };
    }
}
