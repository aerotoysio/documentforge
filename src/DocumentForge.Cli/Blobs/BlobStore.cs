using System.Security.Cryptography;

namespace DocumentForge.Cli.Blobs;

/// <summary>
/// Issue #109 / #71 — out-of-line, content-addressed blob store. Blob bytes
/// live in a sibling append-only file (<c>&lt;db&gt;.dfdb.blobs</c>), NOT in the
/// primary data file, the page cache, or the WAL, so the transactional hot path
/// (index scans, page reads, the write lock, the replication op stream) never
/// carries blob payload. Documents reference a blob by a small descriptor; the
/// engine treats that as an opaque JSON field and never sees the bytes.
///
/// <para>Content-addressed by SHA-256: a re-uploaded identical blob costs
/// nothing (dedup) and the id doubles as an integrity check. Uploads stream
/// through a temp file so a hundred-MB video never sits in memory.</para>
///
/// <para>On-disk layout:
/// <list type="bullet">
///   <item>data file — raw blob bytes concatenated; a blob occupies
///   <c>[offset, offset+length)</c>.</item>
///   <item>index file — append-only log of fixed 49-byte records
///   <c>[sha256:32][offset:8][length:8][live:1]</c>; replayed on open, last
///   record per id wins. Rebuilt from nothing if missing.</item>
/// </list></para>
/// </summary>
public sealed class BlobStore : IDisposable
{
    private const int IdBytes = 32;               // SHA-256
    private const int IndexRecordSize = IdBytes + 8 + 8 + 1;

    private readonly string _dataPath;
    private readonly string _indexPath;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _index = new(StringComparer.OrdinalIgnoreCase);
    private FileStream? _dataAppend;
    private FileStream? _indexAppend;
    private bool _disposed;

    private readonly record struct Entry(long Offset, long Length, bool Live);

    public string DataPath => _dataPath;

    public BlobStore(string basePath)
    {
        _dataPath = basePath;
        _indexPath = basePath + ".idx";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(basePath))!);
        LoadIndex();
    }

    private void LoadIndex()
    {
        if (!File.Exists(_indexPath)) return;
        using var fs = new FileStream(_indexPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var rec = new byte[IndexRecordSize];
        while (true)
        {
            int read = 0;
            while (read < IndexRecordSize)
            {
                int n = fs.Read(rec, read, IndexRecordSize - read);
                if (n == 0) break;
                read += n;
            }
            if (read != IndexRecordSize) break; // torn tail — stop
            var id = Convert.ToHexString(rec.AsSpan(0, IdBytes)).ToLowerInvariant();
            var offset = BitConverter.ToInt64(rec, IdBytes);
            var length = BitConverter.ToInt64(rec, IdBytes + 8);
            var live = rec[IdBytes + 16] != 0;
            _index[id] = new Entry(offset, length, live); // last record wins
        }
    }

    private FileStream DataAppend() => _dataAppend ??=
        new FileStream(_dataPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
    private FileStream IndexAppend() => _indexAppend ??=
        new FileStream(_indexPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);

    /// <summary>Number of live (non-collected) blobs.</summary>
    public int Count { get { lock (_lock) return _index.Values.Count(e => e.Live); } }

    /// <summary>Total bytes of live blob payload.</summary>
    public long LiveBytes { get { lock (_lock) return _index.Values.Where(e => e.Live).Sum(e => e.Length); } }

    /// <summary>True if a live blob with this id exists.</summary>
    public bool Contains(string sha256Hex)
    {
        lock (_lock) return _index.TryGetValue(sha256Hex, out var e) && e.Live;
    }

    /// <summary>
    /// Stream <paramref name="input"/> into the store, returning its content id
    /// (lowercase hex SHA-256) and length. Idempotent: an identical blob that
    /// already exists is not re-written. The bytes and the index record are
    /// fsync'd before returning, so a returned id is durable.
    /// </summary>
    public async Task<(string Id, long Length)> PutAsync(Stream input, CancellationToken ct = default)
    {
        // Pass 1: stream to a temp file while hashing, so we never buffer the
        // whole blob and we know the id (for dedup) before committing.
        var tmp = _dataPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        long length;
        string id;
        try
        {
            using (var sha = SHA256.Create())
            using (var tmpFs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan))
            {
                var buffer = new byte[1 << 20];
                long total = 0;
                int n;
                while ((n = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    sha.TransformBlock(buffer, 0, n, null, 0);
                    await tmpFs.WriteAsync(buffer.AsMemory(0, n), ct);
                    total += n;
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                length = total;
                id = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            }

            lock (_lock)
            {
                if (_index.TryGetValue(id, out var existing) && existing.Live)
                    return (id, existing.Length); // dedup — identical content already stored

                // Pass 2: append the temp bytes to the data file at the end.
                var data = DataAppend();
                long offset = data.Seek(0, SeekOrigin.End);
                using (var tmpRead = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.None, 1 << 20, FileOptions.SequentialScan))
                    tmpRead.CopyTo(data, 1 << 20);
                data.Flush(true); // fsync bytes before we index them

                AppendIndexRecord(id, offset, length, live: true);
                _index[id] = new Entry(offset, length, Live: true);
                return (id, length);
            }
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    /// <summary>
    /// Open a read stream over a blob's bytes, optionally a byte range
    /// <c>[start, start+count)</c> for HTTP Range support. Returns null if the
    /// blob doesn't exist. The stream is independent of writers.
    /// </summary>
    public Stream? OpenRead(string sha256Hex, long start = 0, long? count = null)
    {
        Entry e;
        lock (_lock)
        {
            if (!_index.TryGetValue(sha256Hex, out e) || !e.Live) return null;
        }
        if (start < 0 || start > e.Length) return null;
        long available = e.Length - start;
        long len = count.HasValue ? Math.Min(count.Value, available) : available;
        return new BoundedFileStream(_dataPath, e.Offset + start, len);
    }

    /// <summary>Length of a live blob, or null if absent.</summary>
    public long? LengthOf(string sha256Hex)
    {
        lock (_lock) return _index.TryGetValue(sha256Hex, out var e) && e.Live ? e.Length : null;
    }

    /// <summary>
    /// Out-of-band mark-sweep GC: mark every blob NOT in
    /// <paramref name="referencedIds"/> as collected (tombstoned in the index).
    /// The bytes stay in the data file until a future compaction, but a
    /// collected id is no longer readable and stops counting against
    /// <see cref="LiveBytes"/>. Returns the number collected. Never runs on the
    /// transactional hot path — call it from an admin endpoint / background job.
    /// </summary>
    public int GarbageCollect(ISet<string> referencedIds)
    {
        lock (_lock)
        {
            int collected = 0;
            foreach (var id in _index.Keys.ToList())
            {
                var e = _index[id];
                if (e.Live && !referencedIds.Contains(id))
                {
                    AppendIndexRecord(id, e.Offset, e.Length, live: false);
                    _index[id] = e with { Live = false };
                    collected++;
                }
            }
            return collected;
        }
    }

    private void AppendIndexRecord(string idHex, long offset, long length, bool live)
    {
        var rec = new byte[IndexRecordSize];
        Convert.FromHexString(idHex).CopyTo(rec, 0);
        BitConverter.TryWriteBytes(rec.AsSpan(IdBytes), offset);
        BitConverter.TryWriteBytes(rec.AsSpan(IdBytes + 8), length);
        rec[IdBytes + 16] = (byte)(live ? 1 : 0);
        var idx = IndexAppend();
        idx.Seek(0, SeekOrigin.End);
        idx.Write(rec, 0, rec.Length);
        idx.Flush(true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock)
        {
            try { _dataAppend?.Dispose(); } catch { }
            try { _indexAppend?.Dispose(); } catch { }
            _dataAppend = null;
            _indexAppend = null;
        }
    }

    /// <summary>A read-only stream bounded to a slice of the blob data file, so
    /// callers can Range-read a blob without seeing neighbouring blobs.</summary>
    private sealed class BoundedFileStream : Stream
    {
        private readonly FileStream _fs;
        private long _remaining;

        public BoundedFileStream(string path, long start, long length)
        {
            _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
            _fs.Seek(start, SeekOrigin.Begin);
            _remaining = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            int toRead = (int)Math.Min(count, _remaining);
            int n = _fs.Read(buffer, offset, toRead);
            _remaining -= n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long o, SeekOrigin r) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _fs.Dispose();
            base.Dispose(disposing);
        }
    }
}
