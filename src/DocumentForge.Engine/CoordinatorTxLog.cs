using System.Buffers.Binary;
using System.Text;
using DocumentForge.Transactions;

namespace DocumentForge.Engine;

/// <summary>
/// Append-only log of cluster-tx decisions on a coordinator shard. Lives at
/// <c>{db}.coord.log</c> alongside the data file.
///
/// <para>
/// 2PC's "point of no return" is the moment the coordinator durably records
/// COMMIT_DECISION. After that, every participant must eventually commit —
/// even if the coordinator dies before broadcasting, recovery (Phase C.2)
/// reads this log and replays the broadcast. Without the log, a coordinator
/// crash between deciding and broadcasting leaves the cluster inconsistent
/// (some PREPARED, no decision in sight).
/// </para>
///
/// <para>Record types:</para>
/// <list type="bullet">
///   <item><c>COMMIT_DECISION(txId)</c> — written before broadcasting COMMIT.</item>
///   <item><c>DONE(txId)</c> — written after every participant ACK'd COMMIT.</item>
/// </list>
///
/// <para>
/// ABORT is implicit: a tx with no COMMIT_DECISION record is, by
/// convention, aborted. Saves us a record type and a wire op.
/// </para>
///
/// <para>
/// On-disk layout per record (mirrors <see cref="PreparedTxLog"/> for
/// consistency):
/// <code>
/// [Magic:4=DFCT] [Type:1] [Length:4] [Payload:Length] [CRC32:4]
/// </code>
/// CRC covers Type + Length + Payload. A truncated tail fails CRC and is
/// skipped on scan.
/// </para>
/// </summary>
internal sealed class CoordinatorTxLog : IDisposable
{
    private static readonly byte[] Magic = { (byte)'D', (byte)'F', (byte)'C', (byte)'T' };
    private const byte TypeCommitDecision = 1;
    private const byte TypeDone = 2;

    private readonly string _path;
    private readonly object _writeLock = new();
    private FileStream? _stream;

    public string Path => _path;

    public CoordinatorTxLog(string path)
    {
        _path = path;
        _stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        _stream.Seek(0, SeekOrigin.End);
    }

    public void AppendCommitDecision(string txId) =>
        AppendRecord(TypeCommitDecision, Encoding.UTF8.GetBytes(txId));

    public void AppendDone(string txId) =>
        AppendRecord(TypeDone, Encoding.UTF8.GetBytes(txId));

    private void AppendRecord(byte type, byte[] payload)
    {
        lock (_writeLock)
        {
            if (_stream is null) throw new ObjectDisposedException(nameof(CoordinatorTxLog));

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
    /// Scan the log and return a snapshot of decisions: txId → (committed,
    /// done?). Phase C.2 uses this to (a) replay COMMIT broadcasts for
    /// decided-but-not-DONE txns and (b) answer participant decision
    /// queries.
    /// </summary>
    public IReadOnlyDictionary<string, CoordinatorTxState> Scan()
    {
        lock (_writeLock)
        {
            if (_stream is null) throw new ObjectDisposedException(nameof(CoordinatorTxLog));
            var states = new Dictionary<string, CoordinatorTxState>(StringComparer.Ordinal);

            _stream.Seek(0, SeekOrigin.Begin);
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

                var txId = Encoding.UTF8.GetString(payload);
                if (type == TypeCommitDecision)
                {
                    states[txId] = new CoordinatorTxState(txId, Decided: true, Done: false);
                }
                else if (type == TypeDone)
                {
                    if (states.TryGetValue(txId, out var s))
                        states[txId] = s with { Done = true };
                    else
                        // DONE without COMMIT_DECISION shouldn't happen, but treat as "done" anyway.
                        states[txId] = new CoordinatorTxState(txId, Decided: true, Done: true);
                }
            }

            _stream.Seek(0, SeekOrigin.End);
            return states;
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
/// Per-tx state extracted from a coordinator log scan. <c>Decided</c> means
/// a COMMIT_DECISION record exists (the point of no return); <c>Done</c>
/// means a matching DONE record also exists (every participant ACK'd
/// COMMIT — recovery has nothing to do for this tx).
/// </summary>
public sealed record CoordinatorTxState(string TxId, bool Decided, bool Done);
