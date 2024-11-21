using DocumentForge.Core;

namespace DocumentForge.Storage;

public interface IDataFile : IDisposable
{
    byte[] ReadPage(PageId pageId);
    void WritePage(PageId pageId, byte[] data);
    uint PageCount { get; }
    PageId AllocateNewPage();
    void Flush();

    /// <summary>Read the index catalog root page pointer from the file header.
    /// Returns <see cref="PageId.Invalid"/> if never set.</summary>
    PageId GetIndexCatalogPage();

    /// <summary>Write the index catalog root page pointer to the file header
    /// and fsync. Decorators (e.g. the crash-injection harness) can intercept
    /// this for fault testing.</summary>
    void SetIndexCatalogPage(PageId pageId);
}

public sealed class DataFile : IDataFile
{
    private readonly FileStream _stream;
    private readonly object _lock = new();
    private uint _pageCount;

    public uint PageCount
    {
        get { lock (_lock) return _pageCount; }
    }

    private DataFile(FileStream stream, uint pageCount)
    {
        _stream = stream;
        _pageCount = pageCount;
    }

    public static DataFile Open(string filePath)
    {
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, Constants.PageSize, FileOptions.RandomAccess);
        var pageCount = (uint)(stream.Length / Constants.PageSize);
        return new DataFile(stream, pageCount);
    }

    public static DataFile Create(string filePath)
    {
        var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, Constants.PageSize, FileOptions.RandomAccess);

        // Write header page
        var headerPage = new byte[Constants.PageSize];
        // Magic bytes
        Constants.MagicBytes.CopyTo(headerPage.AsSpan(0));
        // Version
        BitConverter.TryWriteBytes(headerPage.AsSpan(4), Constants.FileFormatVersion);
        // Creation timestamp
        BitConverter.TryWriteBytes(headerPage.AsSpan(8), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        // Collection catalog root page (page 1)
        BitConverter.TryWriteBytes(headerPage.AsSpan(16), (uint)1);
        // Total page count (will be 2 after we write catalog page)
        BitConverter.TryWriteBytes(headerPage.AsSpan(20), (uint)2);
        stream.Write(headerPage, 0, Constants.PageSize);

        // Write empty collection catalog page
        var catalogPage = new byte[Constants.PageSize];
        var catalogHeader = PageHeader.CreateData(PageId.CollectionCatalog);
        catalogHeader.PageType = PageType.CollectionCatalog;
        catalogHeader.WriteTo(catalogPage);
        stream.Write(catalogPage, 0, Constants.PageSize);

        stream.Flush();

        return new DataFile(stream, 2);
    }

    /// <summary>
    /// Count of pages read where the stored checksum didn't match the computed one.
    /// Non-zero indicates disk or transport corruption - alert on this in production.
    /// </summary>
    public long ChecksumMismatches { get; private set; }

    public byte[] ReadPage(PageId pageId)
    {
        var buffer = new byte[Constants.PageSize];
        lock (_lock)
        {
            _stream.Seek(pageId.FileOffset, SeekOrigin.Begin);
            int totalRead = 0;
            while (totalRead < Constants.PageSize)
            {
                int read = _stream.Read(buffer, totalRead, Constants.PageSize - totalRead);
                if (read == 0) break;
                totalRead += read;
            }
        }

        // Verify checksum (skipped for legacy pages with Checksum=0)
        if (!PageChecksum.Verify(buffer))
        {
            lock (_lock) ChecksumMismatches++;
            Console.WriteLine($"[DocumentForge] WARNING: page {pageId} checksum mismatch - possible corruption");
        }

        return buffer;
    }

    public void WritePage(PageId pageId, byte[] data)
    {
        if (data.Length != Constants.PageSize)
            throw new ArgumentException($"Page data must be {Constants.PageSize} bytes.");

        // Stamp a fresh checksum before writing. Readers will verify on next load.
        PageChecksum.Stamp(data);

        lock (_lock)
        {
            _stream.Seek(pageId.FileOffset, SeekOrigin.Begin);
            _stream.Write(data, 0, Constants.PageSize);
        }
    }

    public PageId AllocateNewPage()
    {
        lock (_lock)
        {
            var newPageId = new PageId(_pageCount);
            // Extend file with an empty page
            _stream.Seek(newPageId.FileOffset, SeekOrigin.Begin);
            _stream.Write(new byte[Constants.PageSize], 0, Constants.PageSize);
            _pageCount++;

            // Update page count in header
            _stream.Seek(20, SeekOrigin.Begin);
            var countBytes = new byte[4];
            BitConverter.TryWriteBytes(countBytes, _pageCount);
            _stream.Write(countBytes, 0, 4);

            // Issue #24 part 3: fsync the file extension + header update
            // together. Pre-fix a crash between this method returning and
            // the next FlushAll could leave the file at the new size with
            // garbage bytes (or vice versa: header thinks N pages exist but
            // file is shorter). Page allocation is rare enough that one
            // fsync per call is unmeasurable in any realistic workload.
            _stream.Flush(true);

            return newPageId;
        }
    }

    public void Flush()
    {
        lock (_lock) _stream.Flush(true);
    }

    /// <summary>
    /// Read/write the index catalog root page pointer stored in the file header (bytes 24-27).
    /// Returns PageId.Invalid if never set.
    /// </summary>
    public PageId GetIndexCatalogPage()
    {
        lock (_lock)
        {
            _stream.Seek(24, SeekOrigin.Begin);
            var buf = new byte[4];
            int read = _stream.Read(buf, 0, 4);
            if (read != 4) return PageId.Invalid;
            var val = BitConverter.ToUInt32(buf);
            return val == 0 ? PageId.Invalid : new PageId(val);
        }
    }

    public void SetIndexCatalogPage(PageId pageId)
    {
        lock (_lock)
        {
            _stream.Seek(24, SeekOrigin.Begin);
            var buf = BitConverter.GetBytes(pageId.Value);
            _stream.Write(buf, 0, 4);
            // Flush(true) → FlushFileBuffers on Windows / fsync on POSIX. The
            // bare Flush() we used before only pushed the managed buffer; the
            // 4-byte header pointer could sit in the OS write cache through a
            // power loss and come back as Invalid on next open, leaving the
            // catalog page allocated and indexes orphaned. Match the discipline
            // used by Flush() on line 136 — every header-mutating write must
            // hit disk before we return.
            _stream.Flush(true);
        }
    }

    public void Dispose() => _stream.Dispose();
}
