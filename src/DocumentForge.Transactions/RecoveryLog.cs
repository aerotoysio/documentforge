using DocumentForge.Core;

namespace DocumentForge.Transactions;

/// <summary>
/// The engine's physical write-ahead log (issues #89/#90). Every commit
/// appends the after-images of the pages it dirtied, then a commit marker,
/// then fsyncs — a write is not acknowledged until its marker is durable.
/// On open, <see cref="ReplayRecords"/> returns the page images of every
/// COMMITTED batch (marker-sealed); a trailing unsealed batch is a crash
/// mid-commit and is discarded, which is what makes multi-document
/// transactions crash-atomic (issue #91).
///
/// On-disk format, version 2 (files start with an 8-byte header):
///   [FileMagic:4 "WLG2"][Version:4 = 2]
///   then a sequence of records:
///     page record:   [Magic:4 "WLOG"][PageId:4][Checksum:4][PageData:8192]
///     commit marker: [Magic:4 "WCMT"][CommitSeq:8][Checksum:4 of CommitSeq]
///
/// Legacy format (no file header, first bytes are a "WLOG" record): produced
/// by pre-#89 engines and by PITR, which concatenates archived .walseg
/// segments (page records only) into a synthetic .recovery file. Legacy
/// files carry no markers, so replay applies every valid record — the
/// pre-#89 behaviour, preserved for upgrade and restore paths.
/// </summary>
public sealed class RecoveryLog : IDisposable
{
    private readonly string _path;
    private FileStream? _writeStream;
    private readonly object _lock = new();
    private bool _disposed;
    private ulong _commitSeq;

    private static readonly byte[] Magic = "WLOG"u8.ToArray();
    private static readonly byte[] CommitMagic = "WCMT"u8.ToArray();
    private static readonly byte[] FileMagic = "WLG2"u8.ToArray();
    private const uint FormatVersion = 2;
    private const int FileHeaderSize = 8;    // fileMagic(4) + version(4)
    private const int RecordOverhead = 12;   // magic(4) + pageId(4) + checksum(4)
    private const int RecordSize = RecordOverhead + 8192;
    private const int CommitRecordSize = 16; // magic(4) + seq(8) + checksum(4)

    public string Path => _path;
    public long Length { get { lock (_lock) return _writeStream?.Length ?? 0; } }

    public RecoveryLog(string path)
    {
        _path = path;
        _writeStream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        _writeStream.Seek(0, SeekOrigin.End);
        if (_writeStream.Length == 0) WriteFileHeader();
    }

    private void WriteFileHeader()
    {
        var header = new byte[FileHeaderSize];
        FileMagic.CopyTo(header, 0);
        BitConverter.TryWriteBytes(header.AsSpan(4), FormatVersion);
        _writeStream!.Write(header, 0, FileHeaderSize);
    }

    /// <summary>
    /// Append a page-write record to the log. Caller should Flush when durability is needed.
    /// </summary>
    public void LogPageWrite(PageId pageId, byte[] pageData)
    {
        if (pageData.Length != Constants.PageSize)
            throw new ArgumentException($"Page data must be {Constants.PageSize} bytes");

        lock (_lock)
        {
            if (_writeStream is null) return;
            var record = new byte[RecordSize];
            Magic.CopyTo(record, 0);
            BitConverter.TryWriteBytes(record.AsSpan(4), pageId.Value);
            uint checksum = Crc32(pageData);
            BitConverter.TryWriteBytes(record.AsSpan(8), checksum);
            pageData.CopyTo(record.AsSpan(12));
            _writeStream.Write(record, 0, RecordSize);
        }
    }

    /// <summary>
    /// Seal every page record appended since the previous marker as one
    /// committed batch and fsync. When this returns, the batch is durable:
    /// replay on the next Open will apply it. This is THE commit point —
    /// callers must not acknowledge a write to the client before it returns.
    /// </summary>
    public void Commit()
    {
        lock (_lock)
        {
            if (_writeStream is null) return;
            var record = new byte[CommitRecordSize];
            CommitMagic.CopyTo(record, 0);
            var seq = ++_commitSeq;
            BitConverter.TryWriteBytes(record.AsSpan(4), seq);
            BitConverter.TryWriteBytes(record.AsSpan(12), Crc32(record.AsSpan(4, 8)));
            _writeStream.Write(record, 0, CommitRecordSize);
            _writeStream.Flush(true);
        }
    }

    public void Flush()
    {
        lock (_lock) _writeStream?.Flush(true);
    }

    /// <summary>
    /// Truncate the log after data file has been safely flushed.
    /// </summary>
    public void Truncate()
    {
        lock (_lock)
        {
            if (_writeStream is null) return;
            _writeStream.SetLength(0);
            WriteFileHeader();
            _writeStream.Flush(true);
        }
    }

    /// <summary>
    /// Read the page images that recovery replay should apply.
    ///
    /// Version-2 files (WLG2 header): only batches sealed by a valid commit
    /// marker are returned, in log order; a trailing unsealed batch (crash
    /// mid-commit) is discarded. Legacy files (no header): every valid page
    /// record is returned — those files were written at flush time by
    /// pre-#89 engines, or synthesized by PITR from archived segments, and
    /// carry no markers.
    /// </summary>
    public static IEnumerable<(PageId PageId, byte[] PageData)> ReplayRecords(string path)
    {
        if (!File.Exists(path)) return Enumerable.Empty<(PageId, byte[])>();
        var fileInfo = new FileInfo(path);
        if (fileInfo.Length == 0) return Enumerable.Empty<(PageId, byte[])>();

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        // Sniff the file header without consuming a legacy record's bytes.
        var head = new byte[4];
        if (stream.Read(head, 0, 4) != 4) return Enumerable.Empty<(PageId, byte[])>();
        bool versioned = head.AsSpan().SequenceEqual(FileMagic);
        if (versioned)
        {
            var ver = new byte[4];
            if (stream.Read(ver, 0, 4) != 4) return Enumerable.Empty<(PageId, byte[])>();
            // Future versions replay conservatively: nothing we don't understand.
            if (BitConverter.ToUInt32(ver) != FormatVersion) return Enumerable.Empty<(PageId, byte[])>();
        }
        else
        {
            stream.Seek(0, SeekOrigin.Begin);
        }

        var committed = new List<(PageId, byte[])>();
        var pending = new List<(PageId, byte[])>();

        while (true)
        {
            var magic = new byte[4];
            if (!ReadExactly(stream, magic, 4)) break;

            if (magic.AsSpan().SequenceEqual(Magic))
            {
                var body = new byte[RecordSize - 4];
                if (!ReadExactly(stream, body, body.Length)) break; // truncated tail
                var pageId = new PageId(BitConverter.ToUInt32(body, 0));
                var expectedChecksum = BitConverter.ToUInt32(body, 4);
                var pageData = new byte[Constants.PageSize];
                Array.Copy(body, 8, pageData, 0, Constants.PageSize);
                if (Crc32(pageData) != expectedChecksum) break; // corrupt — stop; nothing past it is trustworthy
                pending.Add((pageId, pageData));
            }
            else if (magic.AsSpan().SequenceEqual(CommitMagic))
            {
                var body = new byte[CommitRecordSize - 4];
                if (!ReadExactly(stream, body, body.Length)) break;
                if (Crc32(body.AsSpan(0, 8)) != BitConverter.ToUInt32(body, 8)) break;
                committed.AddRange(pending);
                pending.Clear();
            }
            else
            {
                break; // unknown/corrupt record
            }
        }

        // Legacy files have no markers: everything valid is replayable.
        // Versioned files: the pending tail was never committed — drop it.
        if (!versioned) committed.AddRange(pending);
        return committed;
    }

    /// <summary>
    /// Legacy reader: every valid page record, ignoring commit semantics.
    /// Kept for tools that inspect raw logs; recovery uses ReplayRecords.
    /// </summary>
    public static IEnumerable<(PageId PageId, byte[] PageData)> ReadAllRecords(string path)
    {
        if (!File.Exists(path)) yield break;

        var fileInfo = new FileInfo(path);
        if (fileInfo.Length == 0) yield break;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        // Skip the v2 file header if present.
        var head = new byte[FileHeaderSize];
        if (stream.Read(head, 0, FileHeaderSize) == FileHeaderSize &&
            head.AsSpan(0, 4).SequenceEqual(FileMagic))
        {
            // positioned after header
        }
        else
        {
            stream.Seek(0, SeekOrigin.Begin);
        }

        while (true)
        {
            var magic = new byte[4];
            if (!ReadExactly(stream, magic, 4)) yield break;

            if (magic.AsSpan().SequenceEqual(CommitMagic))
            {
                // Skip marker body.
                var skip = new byte[CommitRecordSize - 4];
                if (!ReadExactly(stream, skip, skip.Length)) yield break;
                continue;
            }
            if (!magic.AsSpan().SequenceEqual(Magic)) yield break; // corrupt record, stop

            var body = new byte[RecordSize - 4];
            if (!ReadExactly(stream, body, body.Length)) yield break; // truncated record

            var pageId = new PageId(BitConverter.ToUInt32(body, 0));
            var expectedChecksum = BitConverter.ToUInt32(body, 4);
            var pageData = new byte[Constants.PageSize];
            Array.Copy(body, 8, pageData, 0, Constants.PageSize);

            if (Crc32(pageData) != expectedChecksum) continue; // skip corrupt record

            yield return (pageId, pageData);
        }
    }

    private static bool ReadExactly(Stream stream, byte[] buffer, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int n = stream.Read(buffer, totalRead, count - totalRead);
            if (n == 0) return false;
            totalRead += n;
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (_lock)
        {
            _writeStream?.Dispose();
            _writeStream = null;
        }
        _disposed = true;
    }

    // Standard CRC-32 (IEEE 802.3 polynomial)
    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        const uint poly = 0xEDB88320;
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int j = 0; j < 8; j++)
                c = (c & 1) != 0 ? (c >> 1) ^ poly : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
