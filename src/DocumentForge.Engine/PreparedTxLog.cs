using System.Buffers.Binary;
using System.Text;
using DocumentForge.Document;
using DocumentForge.Engine.Cluster;
using DocumentForge.Transactions;

namespace DocumentForge.Engine;

/// <summary>
/// Append-only log of prepared-transaction records on a participant shard.
/// Lives at <c>{db}.prepared.log</c> alongside the data file.
///
/// <para>
/// 2PC needs durability: once the participant returns PREPARED to the
/// coordinator, the staged ops must survive a process crash. This log is
/// what gives us that — the Prepare entry is fsync'd before we send
/// PREPARED. On restart, Phase C scans the log; for Phase B we just
/// surface the in-flight set so callers can decide.
/// </para>
///
/// <para>Record types:</para>
/// <list type="bullet">
///   <item><c>PREPARED(txId, coordinatorShardId, ops)</c></item>
///   <item><c>RESOLVED(txId, committed?)</c></item>
/// </list>
///
/// <para>
/// On-disk layout per record:
/// <code>
/// [Magic:4=DFTX] [Type:1] [Length:4] [Payload:Length] [CRC32:4]
/// </code>
/// CRC covers Type + Length + Payload. A truncated tail (partial write
/// during a crash) fails CRC and is skipped on scan.
/// </para>
/// </summary>
internal sealed class PreparedTxLog : IDisposable
{
    private static readonly byte[] Magic = { (byte)'D', (byte)'F', (byte)'T', (byte)'X' };
    private const byte TypePrepared = 1;
    private const byte TypeResolvedCommitted = 2;
    private const byte TypeResolvedAborted = 3;

    private readonly string _path;
    private readonly object _writeLock = new();
    private FileStream? _stream;

    public string Path => _path;

    public PreparedTxLog(string path)
    {
        _path = path;
        _stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        _stream.Seek(0, SeekOrigin.End);
    }

    public void AppendPrepared(string txId, string coordinatorShardId, IReadOnlyList<ShardTxOp> ops)
    {
        var payload = SerializePreparedPayload(txId, coordinatorShardId, ops);
        AppendRecord(TypePrepared, payload);
    }

    public void AppendResolved(string txId, bool committed)
    {
        var payload = Encoding.UTF8.GetBytes(txId);
        AppendRecord(committed ? TypeResolvedCommitted : TypeResolvedAborted, payload);
    }

    private void AppendRecord(byte type, byte[] payload)
    {
        lock (_writeLock)
        {
            if (_stream is null) throw new ObjectDisposedException(nameof(PreparedTxLog));

            // CRC covers Type + Length + Payload. Build the CRC input in one
            // buffer so the on-disk format matches the scan-side recompute.
            var crcInput = new byte[1 + 4 + payload.Length];
            crcInput[0] = type;
            BinaryPrimitives.WriteInt32LittleEndian(crcInput.AsSpan(1, 4), payload.Length);
            payload.CopyTo(crcInput, 5);
            uint crcVal = RecoveryLog.Crc32(crcInput);

            _stream.Write(Magic);
            _stream.Write(crcInput);
            Span<byte> crcBuf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(crcBuf, crcVal);
            _stream.Write(crcBuf);
            _stream.Flush(flushToDisk: true);
        }
    }

    /// <summary>
    /// Scan the log and return all txIds that have a PREPARED record but no
    /// matching RESOLVED record. Phase C will use this for recovery; Phase B
    /// uses it for a sanity check on Open ("if there's an in-flight prepared
    /// tx after restart, log a warning — recovery comes in Phase C").
    /// </summary>
    public IReadOnlyList<PreparedTxRecord> ScanInFlight()
    {
        lock (_writeLock)
        {
            if (_stream is null) throw new ObjectDisposedException(nameof(PreparedTxLog));
            var inFlight = new Dictionary<string, PreparedTxRecord>(StringComparer.Ordinal);

            _stream.Seek(0, SeekOrigin.Begin);

            // Header is fixed-size (Magic[4] + Type[1] + Length[4]); reuse the
            // same span across iterations rather than re-stackallocing.
            Span<byte> hdr = stackalloc byte[4 + 1 + 4];
            Span<byte> crcBuf = stackalloc byte[4];

            while (_stream.Position < _stream.Length)
            {
                var startPos = _stream.Position;
                if (!TryReadExact(_stream, hdr)) { _stream.SetLength(startPos); break; }
                if (!hdr.Slice(0, 4).SequenceEqual(Magic)) { _stream.SetLength(startPos); break; }

                byte type = hdr[4];
                int length = BinaryPrimitives.ReadInt32LittleEndian(hdr.Slice(5, 4));
                if (length < 0 || _stream.Position + length + 4 > _stream.Length)
                {
                    _stream.SetLength(startPos); break;
                }

                var payload = new byte[length];
                if (!TryReadExact(_stream, payload)) { _stream.SetLength(startPos); break; }
                if (!TryReadExact(_stream, crcBuf)) { _stream.SetLength(startPos); break; }
                uint crcOnDisk = BinaryPrimitives.ReadUInt32LittleEndian(crcBuf);

                var crcInput = new byte[1 + 4 + length];
                crcInput[0] = type;
                BinaryPrimitives.WriteInt32LittleEndian(crcInput.AsSpan(1, 4), length);
                payload.CopyTo(crcInput, 5);
                if (RecoveryLog.Crc32(crcInput) != crcOnDisk) { _stream.SetLength(startPos); break; }

                if (type == TypePrepared)
                {
                    var rec = DeserializePreparedPayload(payload);
                    inFlight[rec.TxId] = rec;
                }
                else if (type == TypeResolvedCommitted || type == TypeResolvedAborted)
                {
                    var txId = Encoding.UTF8.GetString(payload);
                    inFlight.Remove(txId);
                }
            }

            _stream.Seek(0, SeekOrigin.End);
            return inFlight.Values.ToList();
        }
    }

    private static bool TryReadExact(FileStream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer.Slice(total));
            if (read == 0) return false;
            total += read;
        }
        return true;
    }

    private static bool TryReadExact(FileStream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0) return false;
            total += read;
        }
        return true;
    }

    private static byte[] SerializePreparedPayload(string txId, string coordinator, IReadOnlyList<ShardTxOp> ops)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8);
        WriteShortString(bw, txId);
        WriteShortString(bw, coordinator);
        bw.Write(ops.Count);
        foreach (var op in ops)
        {
            bw.Write((byte)op.Kind);
            WriteShortString(bw, op.Collection);
            switch (op.Kind)
            {
                case ShardTxOpKind.Insert:
                    var bytes = BsonSerializer.Serialize(op.Doc!);
                    bw.Write(bytes.Length);
                    bw.Write(bytes);
                    break;
                case ShardTxOpKind.DeleteByField:
                    WriteShortString(bw, op.Field!);
                    WriteLongString(bw, op.Value!);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported ShardTxOpKind {op.Kind}");
            }
        }
        return ms.ToArray();
    }

    private static PreparedTxRecord DeserializePreparedPayload(byte[] payload)
    {
        using var ms = new MemoryStream(payload);
        using var br = new BinaryReader(ms, Encoding.UTF8);
        var txId = ReadShortString(br);
        var coordinator = ReadShortString(br);
        int opCount = br.ReadInt32();
        var ops = new List<ShardTxOp>(opCount);
        for (int i = 0; i < opCount; i++)
        {
            var kind = (ShardTxOpKind)br.ReadByte();
            var coll = ReadShortString(br);
            switch (kind)
            {
                case ShardTxOpKind.Insert:
                    int bsonLen = br.ReadInt32();
                    var bsonBytes = br.ReadBytes(bsonLen);
                    var doc = BsonSerializer.Deserialize(bsonBytes);
                    ops.Add(ShardTxOp.ForInsert(coll, doc));
                    break;
                case ShardTxOpKind.DeleteByField:
                    var field = ReadShortString(br);
                    var value = ReadLongString(br);
                    ops.Add(ShardTxOp.ForDeleteByField(coll, field, value));
                    break;
                default:
                    throw new InvalidDataException($"Unknown ShardTxOpKind {kind}");
            }
        }
        return new PreparedTxRecord(txId, coordinator, ops);
    }

    private static void WriteShortString(BinaryWriter bw, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        if (bytes.Length > ushort.MaxValue) throw new ArgumentException("string too long", nameof(s));
        bw.Write((ushort)bytes.Length);
        bw.Write(bytes);
    }

    private static void WriteLongString(BinaryWriter bw, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        bw.Write(bytes.Length);
        bw.Write(bytes);
    }

    private static string ReadShortString(BinaryReader br)
    {
        ushort len = br.ReadUInt16();
        return Encoding.UTF8.GetString(br.ReadBytes(len));
    }

    private static string ReadLongString(BinaryReader br)
    {
        int len = br.ReadInt32();
        return Encoding.UTF8.GetString(br.ReadBytes(len));
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            _stream?.Dispose();
            _stream = null;
        }
    }
}

/// <summary>
/// One in-flight prepared-tx entry on a participant. Returned by
/// <see cref="IShardTransport.ScanInFlightPrepared"/> during recovery so
/// the cluster can route the tx to its coordinator and resolve it.
/// </summary>
public sealed record PreparedTxRecord(string TxId, string CoordinatorShardId, IReadOnlyList<ShardTxOp> Ops);
