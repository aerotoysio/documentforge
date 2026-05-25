using DocumentForge.Core;

namespace DocumentForge.Storage;

/// <summary>
/// Issue #86 — read-only page walker for forensic inspection and recovery
/// scaffolding. Opens a <c>.dfdb</c> file with <c>FileShare.Read</c> so
/// it works alongside an attached engine (for inspection) AND on a
/// completely orphaned file (for rebuild prep). Does NOT touch the
/// <c>.lock</c> sidecar — this is read-only metadata extraction and
/// shouldn't disturb the engine's locking state.
///
/// <para>
/// Doubles as the foundation for: <c>dfdb inspect</c>, <c>dfdb verify</c>,
/// <c>dfdb dump</c>, and the catalog rebuilder (which uses
/// <see cref="FindDataChains"/> to discover collections when the catalog
/// page is damaged).
/// </para>
///
/// <para>
/// Why bypass <see cref="DataFile"/> directly: <c>DataFile.Open</c> calls
/// <see cref="DataFile.ValidateHeader"/> which throws on certain header
/// damage. For forensics we want to surface that damage rather than
/// refuse — corrupted headers are exactly the case operators need
/// visibility into. We open the file ourselves and tolerate partial
/// reads.
/// </para>
/// </summary>
public sealed class DatabaseInspector : IDisposable
{
    private readonly FileStream _stream;
    private bool _disposed;

    public string FilePath { get; }
    public long FileSizeBytes { get; }
    public uint PageCount { get; }
    public bool HasValidHeader { get; }

    private DatabaseInspector(FileStream stream, string filePath)
    {
        _stream = stream;
        FilePath = filePath;
        FileSizeBytes = stream.Length;
        PageCount = (uint)(stream.Length / Constants.PageSize);

        // Best-effort header check. Doesn't throw on failure — the caller
        // can see HasValidHeader and decide what to do.
        try
        {
            var hdr = ReadPageRaw(PageId.Header);
            HasValidHeader = hdr.Length == Constants.PageSize
                && hdr[0] == 'D' && hdr[1] == 'F' && hdr[2] == 'D' && hdr[3] == 'B';
        }
        catch { HasValidHeader = false; }
    }

    /// <summary>Open a <c>.dfdb</c> file for read-only inspection. Compatible
    /// with concurrent readers; non-disruptive to a running engine that
    /// has the file open. Does NOT acquire the engine lock.</summary>
    public static DatabaseInspector OpenReadOnly(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Database file not found.", filePath);
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, Constants.PageSize, FileOptions.RandomAccess);
        try { return new DatabaseInspector(stream, filePath); }
        catch { stream.Dispose(); throw; }
    }

    /// <summary>Read a single page's raw bytes. No checksum verification —
    /// the caller decides what to do with broken pages.</summary>
    public byte[] ReadPageRaw(PageId pageId)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DatabaseInspector));
        var buf = new byte[Constants.PageSize];
        long offset = (long)pageId.Value * Constants.PageSize;
        if (offset + Constants.PageSize > _stream.Length)
            throw new EndOfStreamException(
                $"Page {pageId.Value} is past end of file (offset {offset}, file size {_stream.Length}).");
        lock (_stream)
        {
            _stream.Seek(offset, SeekOrigin.Begin);
            int read = 0;
            while (read < buf.Length)
            {
                int n = _stream.Read(buf, read, buf.Length - read);
                if (n <= 0) throw new EndOfStreamException(
                    $"Short read on page {pageId.Value}: got {read} of {Constants.PageSize} bytes.");
                read += n;
            }
        }
        return buf;
    }

    /// <summary>Walk every page in the file and produce a structural report.
    /// Heavy operation (reads every page once) — intended for diagnostics,
    /// not a hot path. Per-page checksum verification is included.</summary>
    public InspectionReport Inspect()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DatabaseInspector));
        var byType = new Dictionary<PageType, int>();
        var checksumFailures = new List<uint>();
        int unreadable = 0;

        // Pages 0..PageCount-1. Page 0 is the file header; page 1 is the
        // collection catalog; everything else is data / btree / overflow /
        // freelist.
        for (uint i = 0; i < PageCount; i++)
        {
            byte[] raw;
            try { raw = ReadPageRaw(new PageId(i)); }
            catch { unreadable++; continue; }

            // Header page (0) has a different layout — skip the page-header
            // typed parse and don't checksum-verify it (file header has its
            // own format).
            if (i == 0)
            {
                byType[PageType.Header] = byType.GetValueOrDefault(PageType.Header) + 1;
                continue;
            }

            // For non-header pages, parse the standard page header and
            // verify the page-level CRC32.
            var ph = PageHeader.ReadFrom(raw);
            byType[ph.PageType] = byType.GetValueOrDefault(ph.PageType) + 1;
            if (!PageChecksum.Verify(raw)) checksumFailures.Add(i);
        }

        // Discover data-page chains. A chain head is a data page whose
        // PrevPageId is Invalid; the chain extends forward via NextPageId.
        var chains = FindDataChains();

        // Try to parse the catalog page so we can correlate chain heads
        // with collection names when the catalog IS intact.
        var (catalogEntries, catalogEmpty) = TryReadCatalogEntries();

        // Cross-link: for each chain head, see if any catalog entry points
        // at it. Unlinked chain heads are what the rebuilder synthesises
        // recovered_<id> names for.
        var entriesByFirstPage = catalogEntries.ToDictionary(e => e.FirstPageId.Value);
        foreach (var c in chains)
        {
            if (entriesByFirstPage.TryGetValue(c.FirstPageId.Value, out var entry))
                c.AssociatedCollection = entry.CollectionName;
        }

        return new InspectionReport
        {
            FilePath = FilePath,
            FileSizeBytes = FileSizeBytes,
            PageCount = PageCount,
            HasValidHeader = HasValidHeader,
            PageCountByType = byType,
            ChecksumFailures = checksumFailures,
            UnreadablePages = unreadable,
            Chains = chains,
            CatalogEntries = catalogEntries,
            CatalogPageEmpty = catalogEmpty,
        };
    }

    /// <summary>Scan every page and return the heads of all data-page
    /// chains. A chain head is a <see cref="PageType.Data"/> page whose
    /// <c>PrevPageId</c> is <see cref="PageId.Invalid"/>. This is how the
    /// rebuilder finds collections when the catalog page is corrupt — the
    /// catalog stores name→firstPageId but the data pages themselves
    /// (linked-list) survive independently.</summary>
    public List<DataChain> FindDataChains()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DatabaseInspector));
        var heads = new List<(PageId Id, PageHeader Header)>();
        for (uint i = 2; i < PageCount; i++)
        {
            byte[] raw;
            try { raw = ReadPageRaw(new PageId(i)); }
            catch { continue; }
            if (!PageChecksum.Verify(raw)) continue;  // skip damaged pages
            var ph = PageHeader.ReadFrom(raw);
            if (ph.PageType != PageType.Data) continue;
            if (ph.PrevPageId.Value != Constants.InvalidPageId) continue;
            heads.Add((new PageId(i), ph));
        }

        // Walk each head's chain to count documents and total page span.
        // Documents-per-page is approximated by ItemCount; that's
        // slot-count-including-deleted-slots, so the real number is
        // ItemCount minus slots whose length is zero. For the rebuilder's
        // purposes (auto-naming + sanity check) ItemCount is sufficient.
        var result = new List<DataChain>(heads.Count);
        foreach (var (headId, headPh) in heads)
        {
            var visited = new HashSet<uint> { headId.Value };
            long docCount = headPh.ItemCount;
            int pageCount = 1;
            var cursor = headPh.NextPageId;
            // Cycle defense: bounded by total page count + visited set.
            while (cursor.Value != Constants.InvalidPageId
                   && cursor.Value < PageCount
                   && visited.Add(cursor.Value))
            {
                byte[] raw;
                try { raw = ReadPageRaw(cursor); }
                catch { break; }
                if (!PageChecksum.Verify(raw)) break;
                var ph = PageHeader.ReadFrom(raw);
                if (ph.PageType != PageType.Data) break;
                docCount += ph.ItemCount;
                pageCount++;
                cursor = ph.NextPageId;
            }
            result.Add(new DataChain
            {
                FirstPageId = headId,
                PageCount = pageCount,
                DocumentCount = docCount,
                AssociatedCollection = null,  // filled in by Inspect() if catalog has it
            });
        }
        return result;
    }

    /// <summary>Parse the catalog page (page 1) entries if intact. The
    /// rebuilder uses this to verify "catalog is genuinely empty" before
    /// it agrees to write a new one — never clobbers a working catalog.</summary>
    public (List<CatalogEntry> Entries, bool CatalogEmpty) TryReadCatalogEntries()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DatabaseInspector));
        if (PageCount < 2) return (new(), true);

        byte[] raw;
        try { raw = ReadPageRaw(PageId.CollectionCatalog); }
        catch { return (new(), true); }

        // If the page header reports zero items, the catalog is empty.
        var ph = PageHeader.ReadFrom(raw);
        if (ph.ItemCount == 0) return (new(), true);

        // Use the same slotted-page layout the engine writes: each slot
        // record holds [NameLen:2][Name:N][FirstPageId:4][DocCount:8].
        var entries = new List<CatalogEntry>(ph.ItemCount);
        for (int i = 0; i < ph.ItemCount; i++)
        {
            int slotOffset = Constants.PageHeaderSize + (i * Constants.SlotEntrySize);
            if (slotOffset + Constants.SlotEntrySize > raw.Length) break;
            ushort dataOffset = BitConverter.ToUInt16(raw, slotOffset);
            ushort dataLength = BitConverter.ToUInt16(raw, slotOffset + 2);
            if (dataLength == 0) continue;  // deleted slot
            if (dataOffset + dataLength > raw.Length) continue;  // corrupt slot

            int p = dataOffset;
            ushort nameLen = BitConverter.ToUInt16(raw, p); p += 2;
            if (p + nameLen + 12 > dataOffset + dataLength) continue;
            string name = System.Text.Encoding.UTF8.GetString(raw, p, nameLen); p += nameLen;
            uint firstPage = BitConverter.ToUInt32(raw, p); p += 4;
            long docCount = BitConverter.ToInt64(raw, p);

            entries.Add(new CatalogEntry
            {
                CollectionName = name,
                FirstPageId = new PageId(firstPage),
                DocumentCount = docCount,
            });
        }
        return (entries, entries.Count == 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stream.Dispose(); } catch { }
    }
}

/// <summary>Structural snapshot of a <c>.dfdb</c> file. Returned by
/// <see cref="DatabaseInspector.Inspect"/>; consumed by the health endpoint
/// for deep diagnostics and by the catalog rebuilder for its dry-run preview.</summary>
public sealed record InspectionReport
{
    public string FilePath { get; init; } = "";
    public long FileSizeBytes { get; init; }
    public uint PageCount { get; init; }
    public bool HasValidHeader { get; init; }
    public Dictionary<PageType, int> PageCountByType { get; init; } = new();
    public List<uint> ChecksumFailures { get; init; } = new();
    public int UnreadablePages { get; init; }
    public List<DataChain> Chains { get; init; } = new();
    public List<CatalogEntry> CatalogEntries { get; init; } = new();
    public bool CatalogPageEmpty { get; init; }
}

/// <summary>One linked list of <see cref="PageType.Data"/> pages discovered
/// during inspection. If the catalog page is intact, <see cref="AssociatedCollection"/>
/// is the name from the catalog entry; otherwise null and the rebuilder
/// will synthesise a <c>recovered_&lt;FirstPageId&gt;</c> name.</summary>
public sealed class DataChain
{
    public PageId FirstPageId { get; init; }
    public int PageCount { get; init; }
    public long DocumentCount { get; init; }
    public string? AssociatedCollection { get; set; }
}

/// <summary>A parsed entry from the on-disk catalog page (page 1). Used
/// by the inspector to correlate chain heads with their collection names
/// when the catalog IS intact; not the same shape as the engine's
/// in-memory catalog object.</summary>
public sealed record CatalogEntry
{
    public string CollectionName { get; init; } = "";
    public PageId FirstPageId { get; init; }
    public long DocumentCount { get; init; }
}
