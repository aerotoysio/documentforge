using DocumentForge.Core;

namespace DocumentForge.Storage;

public interface IDataFile : IDisposable
{
    /// <summary>Read a page, verifying its checksum. Throws
    /// <see cref="PageCorruptionException"/> on a mismatch (issue #92).</summary>
    byte[] ReadPage(PageId pageId);

    /// <summary>Corruption-tolerant read: false on checksum mismatch instead
    /// of throwing, for self-healing loaders. <paramref name="buffer"/> holds
    /// the raw bytes regardless.</summary>
    bool TryReadPage(PageId pageId, out byte[] buffer);

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
        try
        {
            ValidateHeader(filePath, stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
        var pageCount = (uint)(stream.Length / Constants.PageSize);
        return new DataFile(stream, pageCount);
    }

    /// <summary>
    /// Issue #57: refuse to Open a data file whose header is missing, truncated,
    /// has wrong magic bytes, or has an unsupported format version. Throws
    /// <see cref="DatabaseCorruptedException"/> with a precise message so the
    /// operator can decide between "restore from backup" and "wrong file."
    ///
    /// <para>
    /// We deliberately do NOT checksum-verify the header page here even though
    /// every other page is checksum-protected. The header page reuses bytes 24-27
    /// (the page-checksum slot in normal pages) for the index-catalog root pointer
    /// (<see cref="GetIndexCatalogPage"/>/<see cref="SetIndexCatalogPage"/>), so a
    /// CRC verify on those bytes against the rest of the page would be a
    /// guaranteed false positive on any database with at least one index. The
    /// magic+version check is the strongest practical integrity gate at this
    /// layer; deeper corruption (torn page-chain links, etc.) is caught by the
    /// per-page cycle/range guards in the catalog and collection loaders.
    /// </para>
    /// </summary>
    private static void ValidateHeader(string filePath, FileStream stream)
    {
        if (stream.Length < Constants.PageSize)
            throw new DatabaseCorruptedException(filePath,
                $"file is {stream.Length} bytes; smaller than a single {Constants.PageSize}-byte page. " +
                "The header is missing or the file was truncated.");

        var header = new byte[Constants.PageSize];
        stream.Seek(0, SeekOrigin.Begin);
        int totalRead = 0;
        while (totalRead < Constants.PageSize)
        {
            int read = stream.Read(header, totalRead, Constants.PageSize - totalRead);
            if (read == 0) break;
            totalRead += read;
        }
        if (totalRead < Constants.PageSize)
            throw new DatabaseCorruptedException(filePath,
                $"could not read the {Constants.PageSize}-byte header page (got {totalRead} bytes).");

        // Magic: bytes 0-3 must be ASCII "DFDB". A wrong magic almost always means
        // the file isn't ours — stale .dfdb path, copy of an unrelated binary, or
        // a totally garbled header from a torn write.
        for (int i = 0; i < Constants.MagicBytes.Length; i++)
        {
            if (header[i] != Constants.MagicBytes[i])
                throw new DatabaseCorruptedException(filePath,
                    $"header magic bytes are wrong (expected 'DFDB', got " +
                    $"0x{header[0]:X2}{header[1]:X2}{header[2]:X2}{header[3]:X2}). " +
                    "This is either not a DocumentForge data file or the header is destroyed.");
        }

        // Version: bytes 4-7. We only know how to read FileFormatVersion files;
        // newer formats deliberately fail-fast rather than silently misinterpret.
        var fileVersion = BitConverter.ToInt32(header, 4);
        if (fileVersion != Constants.FileFormatVersion)
            throw new DatabaseCorruptedException(filePath,
                $"file format version {fileVersion} is not supported by this build " +
                $"(expected {Constants.FileFormatVersion}).");

        // Header checks pass. Stream position is at PageSize; callers re-seek as needed.
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

    /// <summary>
    /// Read a page, verifying its checksum. On mismatch this THROWS
    /// <see cref="PageCorruptionException"/> rather than returning the corrupt
    /// buffer (issue #92): serving a torn b-tree/catalog page as if it were
    /// valid propagates garbage into query results and index walks. A torn
    /// write that happened during the last flush window is repaired earlier,
    /// on Open, by redo-replaying the WAL over the data file (issues #89/#90),
    /// so a mismatch that reaches here is media corruption outside any recovery
    /// window — fail loud so the operator restores from backup.
    ///
    /// <para>Callers that must tolerate corruption to make progress — the
    /// catalog / index self-heal loaders, and offline scanners — go through
    /// <see cref="TryReadPage"/> (or their own raw readers) instead.</para>
    /// </summary>
    public byte[] ReadPage(PageId pageId)
    {
        var buffer = ReadPageRawInternal(pageId);
        if (!PageChecksum.Verify(buffer))
        {
            lock (_lock) ChecksumMismatches++;
            throw new PageCorruptionException(pageId,
                "checksum mismatch — the page is torn or bit-rotted. It was not recoverable " +
                "from the write-ahead log (so the damage is outside the last flush window). " +
                "Restore from a backup or run catalog rebuild.");
        }
        return buffer;
    }

    /// <summary>
    /// Corruption-tolerant read: returns false on a checksum mismatch instead
    /// of throwing, so callers that self-heal (catalog/index loaders scanning
    /// a possibly-damaged file) can skip a bad page and carry on. Still counts
    /// the mismatch. <paramref name="buffer"/> holds the raw (possibly corrupt)
    /// bytes either way, for callers that want to inspect them.
    /// </summary>
    public bool TryReadPage(PageId pageId, out byte[] buffer)
    {
        buffer = ReadPageRawInternal(pageId);
        if (PageChecksum.Verify(buffer)) return true;
        lock (_lock) ChecksumMismatches++;
        return false;
    }

    private byte[] ReadPageRawInternal(PageId pageId)
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
