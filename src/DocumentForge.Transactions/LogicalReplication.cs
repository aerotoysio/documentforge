using System.Net;
using System.Net.Sockets;
using DocumentForge.Core;

namespace DocumentForge.Transactions;

public enum LogicalOpType : byte
{
    Insert = 0x01,
    Delete = 0x02,
    CreateIndex = 0x03,
    DropIndex = 0x04,
    /// <summary>
    /// A multi-doc transaction broadcast as a single atomic batch. The Data
    /// payload is a length-prefixed list of sub-ops; followers deserialize
    /// and apply them all under one write lock. Issue #13 — pre-fix
    /// transactions were broadcast as individual ops, so followers could
    /// observe a partial mid-tx state.
    /// </summary>
    TxBatch = 0x05,
    Heartbeat = 0xFE,
    Handshake = 0xFF,
}

/// <summary>
/// Serialization helpers for the <see cref="LogicalOpType.TxBatch"/> payload.
/// On-disk layout:
/// <code>
///   [SubOpCount:4]
///   for each:
///     [SubOpType:1][CollLen:2][CollBytes][DataLen:4][DataBytes]
/// </code>
/// Mirrors the per-op wire framing so the follower can reuse the same
/// dispatch logic on each sub-op.
/// </summary>
public static class TxBatchPayload
{
    public sealed record SubOp(LogicalOpType OpType, string Collection, byte[] Data);

    public static byte[] Serialize(IReadOnlyList<SubOp> ops)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(ops.Count);
        foreach (var op in ops)
        {
            w.Write((byte)op.OpType);
            var cb = System.Text.Encoding.UTF8.GetBytes(op.Collection);
            w.Write((short)cb.Length);
            w.Write(cb);
            w.Write(op.Data.Length);
            w.Write(op.Data);
        }
        return ms.ToArray();
    }

    public static List<SubOp> Deserialize(byte[] payload)
    {
        using var ms = new MemoryStream(payload);
        using var r = new BinaryReader(ms);
        int count = r.ReadInt32();
        var list = new List<SubOp>(count);
        for (int i = 0; i < count; i++)
        {
            var opType = (LogicalOpType)r.ReadByte();
            int collLen = r.ReadInt16();
            var collBytes = r.ReadBytes(collLen);
            int dataLen = r.ReadInt32();
            var data = r.ReadBytes(dataLen);
            list.Add(new SubOp(opType, System.Text.Encoding.UTF8.GetString(collBytes), data));
        }
        return list;
    }
}

/// <summary>A single logical operation with monotonic sequence number.</summary>
public readonly struct LogicalOp
{
    public ulong Seq { get; init; }
    public LogicalOpType OpType { get; init; }
    public string Collection { get; init; }
    public byte[] Data { get; init; }

    public LogicalOp(ulong seq, LogicalOpType opType, string collection, byte[] data)
    {
        Seq = seq;
        OpType = opType;
        Collection = collection;
        Data = data;
    }
}

/// <summary>
/// In-memory ring buffer of recent ops so disconnected followers can catch up.
/// Thread-safe. If a follower is too far behind (requested seq is before the
/// oldest buffered op), the caller must do a full resync.
/// </summary>
public sealed class OpLogBuffer
{
    private readonly LogicalOp[] _buffer;
    private readonly object _lock = new();
    private int _head; // next write position
    private ulong _oldestSeq = ulong.MaxValue;
    private ulong _newestSeq;
    private int _count;

    public int Capacity => _buffer.Length;
    public int Count { get { lock (_lock) return _count; } }
    public ulong OldestSeq { get { lock (_lock) return _oldestSeq; } }
    public ulong NewestSeq { get { lock (_lock) return _newestSeq; } }

    public OpLogBuffer(int capacity = 10_000)
    {
        _buffer = new LogicalOp[capacity];
    }

    public void Append(LogicalOp op)
    {
        lock (_lock)
        {
            _buffer[_head] = op;
            _head = (_head + 1) % _buffer.Length;
            if (_count < _buffer.Length) _count++;
            if (_oldestSeq == ulong.MaxValue || _count == _buffer.Length)
                _oldestSeq = GetOldestUnsafe();
            _newestSeq = op.Seq;
        }
    }

    private ulong GetOldestUnsafe()
    {
        // Oldest is at (_head - _count) modulo capacity
        int start = (_head - _count + _buffer.Length) % _buffer.Length;
        return _buffer[start].Seq;
    }

    /// <summary>
    /// Returns all ops with Seq > afterSeq, in order. Returns null if afterSeq is older
    /// than anything we have (caller should trigger full resync).
    /// </summary>
    public List<LogicalOp>? GetOpsAfter(ulong afterSeq)
    {
        lock (_lock)
        {
            if (_count == 0)
                return new List<LogicalOp>();

            // If requested seq is before everything we have, can't catch up
            if (afterSeq > 0 && afterSeq < _oldestSeq - 1)
                return null;

            var result = new List<LogicalOp>();
            int start = (_head - _count + _buffer.Length) % _buffer.Length;
            for (int i = 0; i < _count; i++)
            {
                int idx = (start + i) % _buffer.Length;
                if (_buffer[idx].Seq > afterSeq)
                    result.Add(_buffer[idx]);
            }
            return result;
        }
    }
}

/// <summary>
/// Leader-side logical replication server with sequence numbers and follower catchup.
/// </summary>
/// <summary>
/// Snapshot of one connected follower as the leader sees it. Exposed via
/// <see cref="LogicalReplicationServer.GetFollowers"/> so the admin status
/// endpoint can wire up topology without manual configuration.
/// </summary>
/// <param name="Endpoint">Remote address as <c>"host:port"</c>, or <c>"unknown"</c>
/// if the socket has already torn down between snapshot and stringify.</param>
/// <param name="ConnectedAtUtc">When the follower handshake completed.</param>
/// <param name="HandshakeSeq">The follower's last-applied seq at handshake.
/// We don't track ongoing acks today (Phase 2 of replication tx work);
/// callers reading lag should compare this against
/// <see cref="LogicalReplicationServer.CurrentSeq"/> with the understanding
/// that it's a worst-case lower bound, not a live ack.</param>
public readonly record struct FollowerInfo(string Endpoint, DateTime ConnectedAtUtc, ulong HandshakeSeq);

public sealed class LogicalReplicationServer : IDisposable
{
    private readonly TcpListener _listener;
    // Records the metadata we collected at handshake alongside the live socket
    // so the status endpoint can report "who is connected" without the follower
    // having to identify itself separately.
    private readonly List<FollowerConn> _followers = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly string? _secret;
    private Task? _acceptTask;
    private Task? _heartbeatTask;
    private bool _disposed;

    public OpLogBuffer OpLog { get; }
    private long _nextSeq = 1; // start at 1 so 0 means "nothing yet"

    private static readonly byte[] Magic = "DFLR"u8.ToArray();

    public int Port { get; }
    public int FollowerCount { get { lock (_lock) return _followers.Count; } }
    public ulong CurrentSeq => (ulong)Interlocked.Read(ref _nextSeq) - 1;

    /// <summary>
    /// Snapshot of currently-connected followers. Used by the
    /// <c>/replication/status</c> admin endpoint for topology auto-discovery.
    /// </summary>
    public IReadOnlyList<FollowerInfo> GetFollowers()
    {
        lock (_lock)
        {
            var list = new List<FollowerInfo>(_followers.Count);
            foreach (var f in _followers)
                list.Add(new FollowerInfo(f.Endpoint, f.ConnectedAtUtc, f.HandshakeSeq));
            return list;
        }
    }

    /// <summary>
    /// One connected follower's bookkeeping. Endpoint and timestamps are
    /// stamped during the handshake handler before the socket gets added
    /// to the broadcast list, so by the time <see cref="GetFollowers"/>
    /// observes the entry every field is already populated.
    /// </summary>
    private sealed class FollowerConn
    {
        public required TcpClient Client { get; init; }
        public required string Endpoint { get; init; }
        public required DateTime ConnectedAtUtc { get; init; }
        public required ulong HandshakeSeq { get; init; }
    }

    public LogicalReplicationServer(int port, int opLogCapacity = 10_000, string? secret = null)
    {
        Port = port;
        OpLog = new OpLogBuffer(opLogCapacity);
        _listener = new TcpListener(IPAddress.Any, port);
        _secret = secret;
    }

    public void Start()
    {
        _listener.Start();
        _acceptTask = Task.Run(AcceptFollowersAsync);
        _heartbeatTask = Task.Run(HeartbeatLoopAsync);
    }

    /// <summary>Assign next sequence number, append to buffer, and broadcast.</summary>
    public ulong BroadcastNewOp(LogicalOpType opType, string collection, byte[] data)
    {
        ulong seq = (ulong)Interlocked.Increment(ref _nextSeq) - 1;
        var op = new LogicalOp(seq, opType, collection, data);
        OpLog.Append(op);
        BroadcastTo(null, op); // null = broadcast to all
        return seq;
    }

    private void BroadcastTo(TcpClient? specific, LogicalOp op)
    {
        var record = BuildRecord(op);
        lock (_lock)
        {
            if (specific is not null)
            {
                try { specific.GetStream().Write(record, 0, record.Length); } catch { }
                return;
            }

            var dead = new List<FollowerConn>();
            foreach (var f in _followers)
            {
                try { f.Client.GetStream().Write(record, 0, record.Length); }
                catch { dead.Add(f); }
            }
            foreach (var d in dead)
            {
                _followers.Remove(d);
                try { d.Client.Close(); } catch { }
                Console.WriteLine($"[LogicalRep] Follower disconnected ({d.Endpoint})");
            }
        }
    }

    private async Task AcceptFollowersAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => HandleFollowerHandshakeAsync(client));
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { Console.WriteLine($"[LogicalRep] Accept error: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Read the follower's handshake (their last-applied seq), replay catchup, then add
    /// to the live broadcast list.
    /// </summary>
    private async Task HandleFollowerHandshakeAsync(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var handshake = new byte[4 + 1 + 8];
            int read = 0;
            while (read < handshake.Length)
            {
                int n = await stream.ReadAsync(handshake.AsMemory(read), _cts.Token);
                if (n == 0) { client.Close(); return; }
                read += n;
            }
            if (handshake[0] != 'D' || handshake[1] != 'F' || handshake[2] != 'L' || handshake[3] != 'R' ||
                handshake[4] != (byte)LogicalOpType.Handshake)
            {
                Console.WriteLine("[LogicalRep] Bad handshake from follower");
                client.Close();
                return;
            }
            var followerLastSeq = BitConverter.ToUInt64(handshake, 5);

            // Optional shared-secret check
            if (_secret is not null)
            {
                var lenBuf = new byte[2];
                if (!await ReadExactBytes(stream, lenBuf, _cts.Token)) { client.Close(); return; }
                var secretLen = BitConverter.ToUInt16(lenBuf);
                if (secretLen is 0 or > 256) { Console.WriteLine("[LogicalRep] Invalid secret length"); client.Close(); return; }
                var secretBuf = new byte[secretLen];
                if (!await ReadExactBytes(stream, secretBuf, _cts.Token)) { client.Close(); return; }
                var presented = System.Text.Encoding.UTF8.GetString(secretBuf);
                if (!ConstantTimeEquals(presented, _secret))
                {
                    Console.WriteLine($"[LogicalRep] SECURITY: follower presented invalid replication secret. Dropping.");
                    client.Close();
                    return;
                }
            }

            // Figure out what we need to catchup
            var catchupOps = OpLog.GetOpsAfter(followerLastSeq);
            if (catchupOps is null)
            {
                // Follower is too far behind - we can't catchup from buffer.
                // For MVP, send a marker and let the follower decide what to do.
                // A full snapshot would go here in a more complete system.
                Console.WriteLine($"[LogicalRep] Follower at seq {followerLastSeq} is too far behind " +
                                  $"(oldest buffered: {OpLog.OldestSeq}). Sending empty stream - follower should reset.");
                catchupOps = new List<LogicalOp>();
            }

            Console.WriteLine($"[LogicalRep] Follower connected at seq {followerLastSeq}, " +
                              $"replaying {catchupOps.Count} ops to reach current seq {OpLog.NewestSeq}");

            // Send catchup ops first
            foreach (var op in catchupOps)
            {
                try { stream.Write(BuildRecord(op)); }
                catch { client.Close(); return; }
            }

            // Add to live broadcast list with the metadata that the status
            // endpoint will surface. RemoteEndPoint is captured now because
            // it's only reachable while the socket is connected; once it
            // closes, the endpoint is gone.
            var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            var conn = new FollowerConn
            {
                Client = client,
                Endpoint = endpoint,
                ConnectedAtUtc = DateTime.UtcNow,
                HandshakeSeq = followerLastSeq,
            };
            lock (_lock) _followers.Add(conn);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LogicalRep] Handshake error: {ex.Message}");
            try { client.Close(); } catch { }
        }
    }

    private async Task HeartbeatLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(2000, _cts.Token); }
            catch { break; }

            var op = new LogicalOp(CurrentSeq, LogicalOpType.Heartbeat, "", Array.Empty<byte>());
            BroadcastTo(null, op);
        }
    }

    private static async Task<bool> ReadExactBytes(System.Net.Sockets.NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (n == 0) return false;
            total += n;
        }
        return true;
    }

    private static bool ConstantTimeEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    private static byte[] BuildRecord(LogicalOp op)
    {
        var collBytes = System.Text.Encoding.UTF8.GetBytes(op.Collection);
        var totalLen = 4 + 1 + 8 + 2 + collBytes.Length + 4 + op.Data.Length;
        var record = new byte[totalLen];
        int i = 0;
        Magic.CopyTo(record.AsSpan(i)); i += 4;
        record[i++] = (byte)op.OpType;
        BitConverter.TryWriteBytes(record.AsSpan(i), op.Seq); i += 8;
        BitConverter.TryWriteBytes(record.AsSpan(i), (short)collBytes.Length); i += 2;
        collBytes.CopyTo(record.AsSpan(i)); i += collBytes.Length;
        BitConverter.TryWriteBytes(record.AsSpan(i), op.Data.Length); i += 4;
        op.Data.CopyTo(record.AsSpan(i));
        return record;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        lock (_lock)
        {
            foreach (var f in _followers) try { f.Client.Close(); } catch { }
            _followers.Clear();
        }
        _disposed = true;
    }
}

/// <summary>
/// Follower-side logical replication client.
/// - Sends a handshake with last-applied seq on connect (enables catchup)
/// - Persists last-applied seq to disk, survives restart
/// - Detects gaps in the op stream and logs warnings
/// - Ignores heartbeats (kept in stream for liveness detection)
/// </summary>
public sealed class LogicalReplicationFollower : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _seqFilePath;
    private readonly Action<LogicalOp> _apply;
    private readonly string? _secret;
    private TcpClient? _client;
    private Task? _streamTask;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;
    private ulong _lastAppliedSeq;
    private long _opsApplied;
    private long _gapsDetected;
    private DateTimeOffset _lastMessageAt = DateTimeOffset.MinValue;

    public ulong LastAppliedSeq => _lastAppliedSeq;
    public long OpsApplied => Interlocked.Read(ref _opsApplied);
    public long GapsDetected => Interlocked.Read(ref _gapsDetected);
    public DateTimeOffset LastMessageAt { get { lock (this) return _lastMessageAt; } }

    /// <summary>The leader this follower is configured to read from, as <c>"host:port"</c>.</summary>
    public string LeaderEndpoint => $"{_host}:{_port}";

    public LogicalReplicationFollower(string host, int port, string seqFilePath, Action<LogicalOp> apply, string? secret = null)
    {
        _host = host;
        _port = port;
        _seqFilePath = seqFilePath;
        _apply = apply;
        _secret = secret;
        _lastAppliedSeq = LoadSeq();
    }

    public void Start()
    {
        _streamTask = Task.Run(StreamAsync);
    }

    private ulong LoadSeq()
    {
        try
        {
            if (File.Exists(_seqFilePath))
                return BitConverter.ToUInt64(File.ReadAllBytes(_seqFilePath));
        }
        catch { }
        return 0;
    }

    private void SaveSeq(ulong seq)
    {
        try { File.WriteAllBytes(_seqFilePath, BitConverter.GetBytes(seq)); } catch { }
    }

    private async Task StreamAsync()
    {
        try
        {
            _client = new TcpClient();
            await _client.ConnectAsync(_host, _port, _cts.Token);
            Console.WriteLine($"[LogicalRep] Connecting as follower at seq {_lastAppliedSeq}");

            var stream = _client.GetStream();

            // Send handshake: [Magic:4][OpType:1=Handshake][LastSeq:8]
            var handshake = new byte[4 + 1 + 8];
            handshake[0] = (byte)'D'; handshake[1] = (byte)'F'; handshake[2] = (byte)'L'; handshake[3] = (byte)'R';
            handshake[4] = (byte)LogicalOpType.Handshake;
            BitConverter.TryWriteBytes(handshake.AsSpan(5), _lastAppliedSeq);
            await stream.WriteAsync(handshake, _cts.Token);

            // If a shared secret is configured, send it immediately after the handshake.
            // Format: [Len:2 u16][UTF-8 bytes]
            if (_secret is not null)
            {
                var secretBytes = System.Text.Encoding.UTF8.GetBytes(_secret);
                var prefix = new byte[2];
                BitConverter.TryWriteBytes(prefix, (ushort)secretBytes.Length);
                await stream.WriteAsync(prefix, _cts.Token);
                await stream.WriteAsync(secretBytes, _cts.Token);
            }

            while (!_cts.IsCancellationRequested)
            {
                var header = new byte[4 + 1 + 8 + 2];
                if (!await ReadExactAsync(stream, header, _cts.Token)) return;

                if (header[0] != 'D' || header[1] != 'F' || header[2] != 'L' || header[3] != 'R')
                { Console.WriteLine("[LogicalRep] Bad magic"); return; }

                var opType = (LogicalOpType)header[4];
                var seq = BitConverter.ToUInt64(header, 5);
                var collLen = BitConverter.ToInt16(header, 13);

                var collBytes = new byte[collLen];
                if (collLen > 0 && !await ReadExactAsync(stream, collBytes, _cts.Token)) return;
                var collection = System.Text.Encoding.UTF8.GetString(collBytes);

                var lenBuf = new byte[4];
                if (!await ReadExactAsync(stream, lenBuf, _cts.Token)) return;
                var dataLen = BitConverter.ToInt32(lenBuf);

                var data = new byte[dataLen];
                if (dataLen > 0 && !await ReadExactAsync(stream, data, _cts.Token)) return;

                lock (this) _lastMessageAt = DateTimeOffset.UtcNow;

                if (opType == LogicalOpType.Heartbeat)
                    continue; // just keeps the connection alive

                // Gap detection
                if (seq != _lastAppliedSeq + 1 && _lastAppliedSeq != 0)
                {
                    Interlocked.Increment(ref _gapsDetected);
                    Console.WriteLine($"[LogicalRep] WARNING: seq gap - expected {_lastAppliedSeq + 1}, got {seq}");
                }

                var op = new LogicalOp(seq, opType, collection, data);
                try { _apply(op); }
                catch (Exception ex) { Console.WriteLine($"[LogicalRep] Apply error: {ex.Message}"); }

                _lastAppliedSeq = seq;
                SaveSeq(seq);
                Interlocked.Increment(ref _opsApplied);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.WriteLine($"[LogicalRep] Follower stream error: {ex.Message}"); }
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (n == 0) return false;
            total += n;
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _cts.Cancel();
        try { _client?.Close(); } catch { }
        _disposed = true;
    }
}
