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

/// <summary>
/// CRC32-based page checksums. Computed over the entire page with the Checksum field
/// zeroed out during computation, then stamped into the Checksum field before write.
/// On read, the checksum is recomputed and compared - mismatch means bit-rot or torn write.
///
/// Backward compat: a stored Checksum of 0 means "this page predates checksums" and
/// verification is skipped. New writes always produce a non-zero checksum (if the
/// computed CRC is genuinely 0, we store 1 instead to keep the "0 = unchecksummed" rule).
/// </summary>
public static class PageChecksum
{
    private const int ChecksumOffset = 24;
    private const int ChecksumSize = 4;

    /// <summary>Compute a checksum for a page buffer. The Checksum field (bytes 24-27) is excluded.</summary>
    public static uint Compute(ReadOnlySpan<byte> pageData)
    {
        // CRC32 over [0..24) + [28..PageSize)
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < ChecksumOffset; i++)
            crc = Table[(crc ^ pageData[i]) & 0xFF] ^ (crc >> 8);
        for (int i = ChecksumOffset + ChecksumSize; i < pageData.Length; i++)
            crc = Table[(crc ^ pageData[i]) & 0xFF] ^ (crc >> 8);
        var result = crc ^ 0xFFFFFFFFu;
        return result == 0 ? 1u : result; // never return 0 - that means "unchecksummed"
    }

    /// <summary>Stamp a computed checksum into the page's Checksum field.</summary>
    public static void Stamp(Span<byte> pageData)
    {
        var cksum = Compute(pageData);
        BitConverter.TryWriteBytes(pageData.Slice(ChecksumOffset, ChecksumSize), cksum);
    }

    /// <summary>
    /// Verify a page's checksum. Returns true if valid OR if no checksum was stored
    /// (backward compat). Returns false only when a stored checksum mismatches.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> pageData)
    {
        var stored = BitConverter.ToUInt32(pageData.Slice(ChecksumOffset, ChecksumSize));
        if (stored == 0) return true; // pre-checksum page
        var computed = Compute(pageData);
        return stored == computed;
    }

    private static readonly uint[] Table = BuildTable();
    private static uint[] BuildTable()
    {
        const uint poly = 0xEDB88320;
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int j = 0; j < 8; j++) c = (c & 1) != 0 ? (c >> 1) ^ poly : c >> 1;
            t[i] = c;
        }
        return t;
    }
}
