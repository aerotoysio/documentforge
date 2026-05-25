using DocumentForge.Core;

namespace DocumentForge.Storage;

/// <summary>
/// Issue #86 — rebuilds the collection catalog (page 1) of a damaged
/// <c>.dfdb</c> file by scanning data-page chains and synthesising
/// <c>recovered_&lt;FirstPageId&gt;</c> entries. The operator renames
/// them afterwards once they inspect a few documents to identify each
/// collection.
///
/// <para>
/// Safety invariants (the rebuilder REFUSES to run otherwise — never
/// silently clobbers a working catalog):
/// <list type="bullet">
///   <item>Catalog page (page 1) must currently be empty or only-deleted-slots.</item>
///   <item>File must have at least one discoverable data chain.</item>
///   <item>File must not be locked by another process (we open with FileShare.None — fails fast otherwise).</item>
/// </list>
/// </para>
///
/// <para>
/// Safety scaffolding the rebuilder DOES (every Execute):
/// <list type="bullet">
///   <item>Writes the existing page-1 bytes to <c>{path}.page1.backup</c> BEFORE touching the data file. If rebuild goes wrong, the backup is a literal byte-for-byte restore source.</item>
///   <item>Stamps the new page's CRC32 so it verifies on subsequent Opens.</item>
///   <item>Calls <c>FileStream.Flush(true)</c> to fsync before reporting success.</item>
/// </list>
/// </para>
/// </summary>
public sealed class CatalogRebuilder : IDisposable
{
    private readonly FileStream _stream;
    private readonly string _filePath;
    private readonly uint _pageCount;
    private bool _disposed;

    private CatalogRebuilder(FileStream stream, string filePath)
    {
        _stream = stream;
        _filePath = filePath;
        _pageCount = (uint)(stream.Length / Constants.PageSize);
    }

    /// <summary>Open the file for the rebuild operation. Uses
    /// <see cref="FileShare.Read"/> rather than <c>None</c> so the
    /// rebuilder can use its own stream for both inspection and write
    /// without conflicting with itself, but other writers are still
    /// blocked (FileShare.Read forbids concurrent <see cref="FileAccess.Write"/>).
    /// Throws <see cref="IOException"/> if another writer holds the
    /// file open — that's the right outcome (the operator must Detach
    /// the DB from the live engine before rebuilding).</summary>
    public static CatalogRebuilder Open(string filePath)
    {
        var stream = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite,
            FileShare.Read, Constants.PageSize, FileOptions.RandomAccess);
        return new CatalogRebuilder(stream, filePath);
    }

    /// <summary>Inspect without writing. Returns what the rebuild WOULD do
    /// if invoked. Studio's confirmation dialog calls this first so the
    /// operator can see the discovered chains before committing.</summary>
    public RebuildPlan Plan()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(CatalogRebuilder));
        var (chains, catalogEntries, catalogEmpty) = ScanFile();
        return BuildPlan(chains, catalogEntries, catalogEmpty);
    }

    /// <summary>Read a page from our own stream. Avoids holding a second
    /// file handle the way a <see cref="DatabaseInspector"/> would.</summary>
    private byte[] ReadPageRaw(PageId pageId)
    {
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

    /// <summary>Single-pass scan reused by both Plan() and Execute(). Walks
    /// every page once, finds data chain heads, and tries to read existing
    /// catalog entries — mirrors <see cref="DatabaseInspector"/>'s logic
    /// but on our own stream so we don't double-open the file.</summary>
    private (List<DataChain> Chains, List<CatalogEntry> CatalogEntries, bool CatalogEmpty) ScanFile()
    {
        // Discover data-chain heads.
        var heads = new List<(PageId Id, PageHeader Header)>();
        for (uint i = 2; i < _pageCount; i++)
        {
            byte[] raw;
            try { raw = ReadPageRaw(new PageId(i)); }
            catch { continue; }
            if (!PageChecksum.Verify(raw)) continue;
            var ph = PageHeader.ReadFrom(raw);
            if (ph.PageType != PageType.Data) continue;
            if (ph.PrevPageId.Value != Constants.InvalidPageId) continue;
            heads.Add((new PageId(i), ph));
        }

        // Walk each head's chain.
        var chains = new List<DataChain>(heads.Count);
        foreach (var (headId, headPh) in heads)
        {
            var visited = new HashSet<uint> { headId.Value };
            long docCount = headPh.ItemCount;
            int pageCount = 1;
            var cursor = headPh.NextPageId;
            while (cursor.Value != Constants.InvalidPageId
                   && cursor.Value < _pageCount
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
            chains.Add(new DataChain
            {
                FirstPageId = headId,
                PageCount = pageCount,
                DocumentCount = docCount,
                AssociatedCollection = null,
            });
        }

        // Read existing catalog entries (if any) so the safety guard can
        // refuse on a healthy catalog.
        var catalogEntries = new List<CatalogEntry>();
        bool catalogEmpty = true;
        if (_pageCount >= 2)
        {
            byte[] raw;
            try { raw = ReadPageRaw(PageId.CollectionCatalog); }
            catch { return (chains, catalogEntries, catalogEmpty); }
            var ph = PageHeader.ReadFrom(raw);
            if (ph.ItemCount > 0)
            {
                for (int i = 0; i < ph.ItemCount; i++)
                {
                    int slotOffset = Constants.PageHeaderSize + (i * Constants.SlotEntrySize);
                    if (slotOffset + Constants.SlotEntrySize > raw.Length) break;
                    ushort dataOffset = BitConverter.ToUInt16(raw, slotOffset);
                    ushort dataLength = BitConverter.ToUInt16(raw, slotOffset + 2);
                    if (dataLength == 0) continue;
                    if (dataOffset + dataLength > raw.Length) continue;

                    int p = dataOffset;
                    ushort nameLen = BitConverter.ToUInt16(raw, p); p += 2;
                    if (p + nameLen + 12 > dataOffset + dataLength) continue;
                    string name = System.Text.Encoding.UTF8.GetString(raw, p, nameLen); p += nameLen;
                    uint firstPage = BitConverter.ToUInt32(raw, p); p += 4;
                    long dc = BitConverter.ToInt64(raw, p);
                    catalogEntries.Add(new CatalogEntry
                    {
                        CollectionName = name,
                        FirstPageId = new PageId(firstPage),
                        DocumentCount = dc,
                    });
                }
                catalogEmpty = catalogEntries.Count == 0;
            }
        }
        return (chains, catalogEntries, catalogEmpty);
    }

    /// <summary>Back up page 1 and stamp the rebuilt catalog. Returns the
    /// same shape as <see cref="Plan"/> for parity, plus where the backup
    /// landed. Throws <see cref="CatalogRebuildException"/> if any safety
    /// invariant is violated.</summary>
    public RebuildResult Execute()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(CatalogRebuilder));
        var (chains, catalogEntries, catalogEmpty) = ScanFile();
        var plan = BuildPlan(chains, catalogEntries, catalogEmpty);

        // Refuse on any safety violation. The exception's Reason field
        // tells the caller WHY so the UI can surface a useful message
        // (don't just say "rebuild failed").
        if (!plan.SafeToRebuild)
            throw new CatalogRebuildException(plan.RefusalReason!);

        // Backup the existing page-1 bytes. Even if it's empty, we want
        // the EXACT pre-rebuild state on disk in case anything goes wrong.
        var backupPath = _filePath + ".page1.backup";
        byte[] currentPage1;
        lock (_stream)
        {
            _stream.Seek(Constants.PageSize, SeekOrigin.Begin);
            currentPage1 = new byte[Constants.PageSize];
            int read = 0;
            while (read < currentPage1.Length)
            {
                int n = _stream.Read(currentPage1, read, currentPage1.Length - read);
                if (n <= 0) throw new InvalidOperationException("Short read on page 1 backup.");
                read += n;
            }
        }
        File.WriteAllBytes(backupPath, currentPage1);

        // Synthesise the new catalog page. Same on-disk format the engine
        // writes: PageType=CollectionCatalog, slotted layout, each slot
        // is [NameLen:2][Name:N][FirstPageId:4][DocCount:8]. The recovered
        // names are guaranteed unique because they include the page id.
        var newPage = new byte[Constants.PageSize];
        var header = new PageHeader
        {
            PageId = PageId.CollectionCatalog,
            PageType = PageType.CollectionCatalog,
            ItemCount = 0,
            FreeSpaceStart = (ushort)Constants.PageHeaderSize,
            FreeSpaceEnd = (ushort)Constants.PageSize,
            NextPageId = PageId.Invalid,
            PrevPageId = PageId.Invalid,
            TransactionId = 0,
            Flags = 0,
            Checksum = 0,
        };
        header.WriteTo(newPage);
        var dp = new DataPage(newPage);

        var recovered = new List<RecoveredCollection>();
        foreach (var chain in plan.Chains)
        {
            var name = chain.SuggestedName;
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
            var entrySize = 2 + nameBytes.Length + 4 + 8;
            var entry = new byte[entrySize];
            int offset = 0;
            BitConverter.TryWriteBytes(entry.AsSpan(offset), (short)nameBytes.Length); offset += 2;
            nameBytes.CopyTo(entry.AsSpan(offset)); offset += nameBytes.Length;
            BitConverter.TryWriteBytes(entry.AsSpan(offset), chain.FirstPageId.Value); offset += 4;
            BitConverter.TryWriteBytes(entry.AsSpan(offset), chain.DocumentCount);

            int slot = dp.Insert(entry);
            if (slot < 0)
                throw new CatalogRebuildException(
                    $"Too many collections to fit in a single 8 KB catalog page. " +
                    $"Discovered {plan.Chains.Count} chains; stopped after writing {recovered.Count}.");
            recovered.Add(new RecoveredCollection
            {
                Name = name,
                FirstPageId = chain.FirstPageId,
                DocumentCount = chain.DocumentCount,
            });
        }

        // Stamp the CRC and write atomically (single write, then fsync).
        PageChecksum.Stamp(dp.RawData);
        lock (_stream)
        {
            _stream.Seek(Constants.PageSize, SeekOrigin.Begin);
            _stream.Write(dp.RawData, 0, dp.RawData.Length);
            _stream.Flush(flushToDisk: true);
        }

        return new RebuildResult
        {
            FilePath = _filePath,
            BackupPath = backupPath,
            Recovered = recovered,
            BackedUpPage1Bytes = currentPage1.Length,
        };
    }

    private static RebuildPlan BuildPlan(List<DataChain> chains, List<CatalogEntry> catalogEntries, bool catalogEmpty)
    {
        // Catalog must be effectively empty. If it has live entries
        // already, the rebuild would destroy them.
        if (!catalogEmpty && catalogEntries.Count > 0)
        {
            return new RebuildPlan
            {
                SafeToRebuild = false,
                RefusalReason = $"Catalog page is not empty — it already lists {catalogEntries.Count} collection(s). " +
                                "Rebuild would destroy them. Use Detach + manual recovery if you really want this.",
                Chains = new(),
            };
        }
        if (chains.Count == 0)
        {
            return new RebuildPlan
            {
                SafeToRebuild = false,
                RefusalReason = "No data-page chains discovered — there's nothing to recover. The data file " +
                                "is either empty or its data pages are damaged beyond what page-walking can find.",
                Chains = new(),
            };
        }
        var planChains = chains
            .OrderBy(c => c.FirstPageId.Value)
            .Select(c => new RebuildPlanChain
            {
                FirstPageId = c.FirstPageId,
                DocumentCount = c.DocumentCount,
                PageCount = c.PageCount,
                SuggestedName = $"recovered_p{c.FirstPageId.Value}",
            })
            .ToList();
        return new RebuildPlan
        {
            SafeToRebuild = true,
            RefusalReason = null,
            Chains = planChains,
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stream.Dispose(); } catch { }
    }
}

/// <summary>What the rebuild WOULD do. Returned by Plan() so the UI can
/// show a confirmation dialog before any bytes are written. Same shape
/// as the rebuild result minus the post-write fields.</summary>
public sealed record RebuildPlan
{
    public bool SafeToRebuild { get; init; }
    public string? RefusalReason { get; init; }
    public List<RebuildPlanChain> Chains { get; init; } = new();
}

public sealed record RebuildPlanChain
{
    public PageId FirstPageId { get; init; }
    public long DocumentCount { get; init; }
    public int PageCount { get; init; }
    public string SuggestedName { get; init; } = "";
}

/// <summary>What actually got rebuilt. Returned by Execute(). The backup
/// path tells the operator where to find the pre-rebuild page-1 bytes
/// in case they need to revert by overwriting page 1 with that file.</summary>
public sealed record RebuildResult
{
    public string FilePath { get; init; } = "";
    public string BackupPath { get; init; } = "";
    public int BackedUpPage1Bytes { get; init; }
    public List<RecoveredCollection> Recovered { get; init; } = new();
}

public sealed record RecoveredCollection
{
    public string Name { get; init; } = "";
    public PageId FirstPageId { get; init; }
    public long DocumentCount { get; init; }
}

public sealed class CatalogRebuildException : Exception
{
    public CatalogRebuildException(string message) : base(message) { }
}
