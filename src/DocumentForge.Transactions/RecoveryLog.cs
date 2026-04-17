using DocumentForge.Core;

namespace DocumentForge.Transactions;

/// <summary>
/// Simple physical write-ahead log. Logs (PageId, PageData) records before flushing
/// dirty pages to the data file. On crash recovery, replay the log to restore durability.
///
/// Format per record:
///   [Magic:4 "WLOG"][PageId:4][Checksum:4][PageData:8192]
///
/// Only records with a valid magic + checksum are replayed.
/// </summary>
public sealed class RecoveryLog : IDisposable
{
    private readonly string _path;
    private FileStream? _writeStream;
    private readonly object _lock = new();
    private bool _disposed;

    private static readonly byte[] Magic = "WLOG"u8.ToArray();
    private const int RecordOverhead = 12; // magic(4) + pageId(4) + checksum(4)
    private const int RecordSize = RecordOverhead + 8192;

    public string Path => _path;
    public long Length { get { lock (_lock) return _writeStream?.Length ?? 0; } }

    public RecoveryLog(string path)
    {
        _path = path;
        _writeStream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        _writeStream.Seek(0, SeekOrigin.End);
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
            _writeStream.Flush(true);
        }
    }

    /// <summary>
    /// Read all valid records from the log for recovery replay.
    /// Returns (PageId, PageData) pairs. Invalid records are skipped.
    /// </summary>
    public static IEnumerable<(PageId PageId, byte[] PageData)> ReadAllRecords(string path)
    {
        if (!File.Exists(path)) yield break;

        var fileInfo = new FileInfo(path);
        if (fileInfo.Length == 0) yield break;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        while (stream.Position + RecordSize <= stream.Length)
        {
            var record = new byte[RecordSize];
            int totalRead = 0;
            while (totalRead < RecordSize)
            {
                int n = stream.Read(record, totalRead, RecordSize - totalRead);
                if (n == 0) break;
                totalRead += n;
            }
            if (totalRead != RecordSize) yield break; // truncated record

            // Verify magic
            if (record[0] != Magic[0] || record[1] != Magic[1] ||
                record[2] != Magic[2] || record[3] != Magic[3])
                yield break; // corrupt record, stop

            var pageId = new PageId(BitConverter.ToUInt32(record, 4));
            var expectedChecksum = BitConverter.ToUInt32(record, 8);
            var pageData = new byte[Constants.PageSize];
            Array.Copy(record, RecordOverhead, pageData, 0, Constants.PageSize);

            var actualChecksum = Crc32(pageData);
            if (actualChecksum != expectedChecksum) continue; // skip corrupt record

            yield return (pageId, pageData);
        }
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
