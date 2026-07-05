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

    /// <summary>
    /// Start of a full-database snapshot transfer (issue #20). Sent by the
    /// leader when a follower handshakes at seq 0 — or at any seq before the
    /// OpLog's oldest entry — so the follower can bootstrap without a manual
    /// scp. Data payload: [TotalSize:8][SnapshotSeq:8].
    /// </summary>
    SnapshotStart = 0x10,

    /// <summary>
    /// One chunk of snapshot data. Multiple of these follow a SnapshotStart
    /// in order; the follower writes them sequentially to a temp file.
    /// Data payload: raw bytes.
    /// </summary>
    SnapshotChunk = 0x11,

    /// <summary>
    /// End of snapshot transfer. The follower writes a marker so its next
    /// Open integrates the snapshot in place of the existing data file.
    /// Data payload: empty.
    /// </summary>
    SnapshotEnd = 0x12,

    Heartbeat = 0xFE,
    Handshake = 0xFF,

    /// <summary>Follower → leader acknowledgement carrying the highest seq the
    /// follower has durably applied. Enables semi-sync replication (issue #95):
    /// the leader can wait for a quorum of acks before acking the client.
    /// Wire frame: [Magic:4][Ack:1][appliedSeq:8].</summary>
    Ack = 0xFD,
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
/// <param name="HttpEndpoint">The follower's HTTP base URL, advertised
/// during the handshake (issue #51). Null if the follower is running an
/// older build that doesn't send the field — the admin-UI falls back to
/// guessing the HTTP port from the replication endpoint in that case.</param>
public readonly record struct FollowerInfo(
    string Endpoint,
    DateTime ConnectedAtUtc,
    ulong HandshakeSeq,
    string? HttpEndpoint);

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

    /// <summary>Issue #96 — this leader's monotonic term (epoch). Advertised to
    /// each connecting follower so a stale lower-term leader is refused.</summary>
    public ulong LeaderTerm { get; }

    /// <summary>Issue #96 — invoked when a connecting follower advertises a
    /// term HIGHER than this leader's, meaning a newer leader has superseded us.
    /// The engine wires this to step down (fence). Runs on the accept thread;
    /// keep it fast.</summary>
    public Action<ulong>? OnHigherTermObserved { get; set; }

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
                list.Add(new FollowerInfo(f.Endpoint, f.ConnectedAtUtc, f.HandshakeSeq, f.HttpEndpoint));
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
        /// <summary>HTTP base URL the follower advertised; null if it's running an older build. Issue #51.</summary>
        public string? HttpEndpoint { get; init; }
        /// <summary>Highest seq this follower has ACKed as durably applied (issue #95).</summary>
        public long AckedSeq;
    }

    // Issue #95 — replaceable pulse completed whenever any follower's AckedSeq
    // advances (or a follower drops), so WaitForReplication wakes without
    // polling and without an ever-growing signal count.
    private volatile TaskCompletionSource<bool> _ackPulse =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void PulseAcks()
    {
        var fresh = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _ackPulse, fresh).TrySetResult(true);
    }

    /// <summary>
    /// Issue #95 — semi-sync wait. Block until at least
    /// <paramref name="requiredAcks"/> followers have ACKed applying
    /// <paramref name="seq"/> (or higher), up to <paramref name="timeout"/>.
    /// Returns true if the quorum was reached. A requiredAcks of 0 returns
    /// immediately (async replication). Callers wait OUTSIDE the write lock so
    /// throughput isn't gated on the network round-trip.
    /// </summary>
    public async Task<bool> WaitForReplicationAsync(ulong seq, int requiredAcks, TimeSpan timeout, CancellationToken ct = default)
    {
        if (requiredAcks <= 0 || seq == 0) return true;
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            // Capture the pulse BEFORE checking, so an ack that lands between the
            // check and the await still wakes us (no lost wakeup).
            var pulse = _ackPulse.Task;
            if (CountAcksAtLeast(seq) >= requiredAcks) return true;
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) return false;
            var completed = await Task.WhenAny(pulse, Task.Delay(remaining, ct));
            if (completed != pulse && CountAcksAtLeast(seq) < requiredAcks) return false; // timed out
        }
    }

    private int CountAcksAtLeast(ulong seq)
    {
        lock (_lock)
        {
            int n = 0;
            foreach (var f in _followers)
                if ((ulong)Interlocked.Read(ref f.AckedSeq) >= seq) n++;
            return n;
        }
    }

    /// <summary>Number of followers whose applied-seq is at least <paramref name="seq"/>.</summary>
    public int AckedFollowerCount(ulong seq) => CountAcksAtLeast(seq);

    /// <summary>
    /// Optional snapshot provider — invoked when a follower handshakes with
    /// a seq the OpLog can't catch up from. The provider takes a consistent
    /// snapshot of the engine's data file and returns its path + the seq
    /// at which the snapshot was taken. The server streams the file as
    /// chunked SnapshotStart/Chunk/End messages, then resumes catch-up from
    /// snapshotSeq+1. Issue #20.
    ///
    /// <para>
    /// Returns null when no provider is wired — the server falls back to the
    /// pre-#20 behaviour (logs a warning, sends nothing, leaves the follower
    /// to manually scp the file).
    /// </para>
    /// </summary>
    public Func<(string TempPath, ulong SnapshotSeq)>? SnapshotProvider { get; set; }

    public LogicalReplicationServer(int port, int opLogCapacity = 10_000, string? secret = null, ulong leaderTerm = 0)
    {
        Port = port;
        LeaderTerm = leaderTerm;
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

            // Issue #51 — read the optional httpEndpoint suffix.
            // New followers write [endpointLen:2][endpointUtf8] right after
            // the secret block (or right after the basic handshake when no
            // secret is configured). Old followers don't write anything more,
            // so we use a short timeout to detect them and treat httpEndpoint
            // as null. The bytes from new followers arrive immediately on a
            // local network, so 200ms is plenty of headroom.
            string? followerHttpEndpoint = null;
            try
            {
                using var endpointTimeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
                using var linkedEndpointCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, endpointTimeoutCts.Token);
                var endpointLenBuf = new byte[2];
                if (await ReadExactBytes(stream, endpointLenBuf, linkedEndpointCts.Token))
                {
                    var endpointLen = BitConverter.ToUInt16(endpointLenBuf);
                    if (endpointLen > 0 && endpointLen <= 1024)
                    {
                        var endpointBuf = new byte[endpointLen];
                        if (await ReadExactBytes(stream, endpointBuf, linkedEndpointCts.Token))
                            followerHttpEndpoint = System.Text.Encoding.UTF8.GetString(endpointBuf);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Legacy follower — didn't send the endpoint suffix. That's
                // fine; httpEndpoint stays null and the admin-UI falls back
                // to its existing port-guess behaviour.
            }

            // Issue #96 — term fencing. Read the follower's advertised term
            // (back-compat: legacy followers send nothing, so a short timeout
            // treats them as term 0). If the follower carries a HIGHER term, a
            // newer leader has superseded us: fence ourselves and drop the
            // connection. Then advertise OUR term so the follower can refuse a
            // stale (lower-term) leader.
            ulong followerTerm = 0;
            try
            {
                using var termTimeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
                using var linkedTermCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, termTimeoutCts.Token);
                var termBuf = new byte[8];
                if (await ReadExactBytes(stream, termBuf, linkedTermCts.Token))
                    followerTerm = BitConverter.ToUInt64(termBuf, 0);
            }
            catch (OperationCanceledException) { /* legacy follower → term 0 */ }

            if (followerTerm > LeaderTerm)
            {
                Console.WriteLine($"[Fencing] Follower advertised term {followerTerm} > our term {LeaderTerm} — a newer leader exists. Stepping down.");
                try { OnHigherTermObserved?.Invoke(followerTerm); } catch { }
                client.Close();
                return;
            }

            // Advertise our term to the follower (the follower fences a
            // lower-term leader). Best-effort — a legacy follower ignores it.
            try
            {
                var myTerm = new byte[8];
                BitConverter.TryWriteBytes(myTerm, LeaderTerm);
                await stream.WriteAsync(myTerm, _cts.Token);
            }
            catch { client.Close(); return; }

            // Figure out what we need to catchup. If the OpLog can't help
            // (follower seq < oldest buffered, or follower is at seq 0 and
            // we have a snapshot provider — covers the case where the
            // leader had data BEFORE the replication server started, so
            // the OpLog never saw it), try a full snapshot transfer
            // (issue #20).
            var catchupOps = OpLog.GetOpsAfter(followerLastSeq);
            ulong streamFromSeq = followerLastSeq;
            bool needSnapshot = catchupOps is null
                || (followerLastSeq == 0 && SnapshotProvider is not null);
            if (needSnapshot)
            {
                if (SnapshotProvider is not null)
                {
                    Console.WriteLine($"[LogicalRep] Follower at seq {followerLastSeq} is too far behind " +
                                      $"(oldest buffered: {OpLog.OldestSeq}). Streaming snapshot.");
                    var (snapPath, snapSeq) = SnapshotProvider();
                    try
                    {
                        StreamSnapshotToFollower(stream, snapPath, snapSeq);
                        streamFromSeq = snapSeq;
                        // After the snapshot, refetch catchup from snapshotSeq.
                        // Anything written on the leader during the snapshot
                        // copy is in the OpLog and gets streamed next.
                        catchupOps = OpLog.GetOpsAfter(snapSeq) ?? new List<LogicalOp>();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[LogicalRep] Snapshot stream failed: {ex.Message}. Dropping follower.");
                        client.Close();
                        return;
                    }
                    finally
                    {
                        try { File.Delete(snapPath); } catch { /* best effort cleanup */ }
                    }
                }
                else
                {
                    Console.WriteLine($"[LogicalRep] Follower at seq {followerLastSeq} is too far behind " +
                                      $"(oldest buffered: {OpLog.OldestSeq}) and no snapshot provider wired. " +
                                      "Sending empty stream — follower must reset manually.");
                    catchupOps = new List<LogicalOp>();
                }
            }

            // catchupOps is non-null on every reachable path above, but the
            // nullable flow analysis can't see that through the `needSnapshot`
            // bool. Normalise to empty so the dereference is provably safe (CS8602).
            catchupOps ??= new List<LogicalOp>();
            Console.WriteLine($"[LogicalRep] Follower connected (seq {streamFromSeq} → {OpLog.NewestSeq}), " +
                              $"replaying {catchupOps.Count} ops");

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
                HttpEndpoint = followerHttpEndpoint,
            };
            lock (_lock) _followers.Add(conn);

            // Issue #95 — read this follower's applied-seq acks on the same
            // socket (concurrent with the broadcast writes; TCP is full-duplex)
            // so semi-sync waiters can make progress.
            _ = Task.Run(() => ReadFollowerAcksAsync(conn, stream));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LogicalRep] Handshake error: {ex.Message}");
            try { client.Close(); } catch { }
        }
    }

    /// <summary>
    /// Issue #95 — read a follower's applied-seq ACK frames
    /// (<c>[Magic:4][Ack:1][seq:8]</c>) and advance its <c>AckedSeq</c>, pulsing
    /// semi-sync waiters. Ends when the follower disconnects (also removing it
    /// from the broadcast list so a dead follower stops counting toward quorum).
    /// </summary>
    private async Task ReadFollowerAcksAsync(FollowerConn conn, NetworkStream stream)
    {
        var frame = new byte[4 + 1 + 8];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (!await ReadExactBytes(stream, frame, _cts.Token)) break;
                if (frame[0] != 'D' || frame[1] != 'F' || frame[2] != 'L' || frame[3] != 'R'
                    || frame[4] != (byte)LogicalOpType.Ack)
                    break; // unexpected frame on the ack channel — drop the follower

                var seq = BitConverter.ToUInt64(frame, 5);
                // Monotonic advance.
                long cur;
                do { cur = Interlocked.Read(ref conn.AckedSeq); if ((long)seq <= cur) break; }
                while (Interlocked.CompareExchange(ref conn.AckedSeq, (long)seq, cur) != cur);

                PulseAcks();
            }
        }
        catch { /* disconnect / read error → fall through to cleanup */ }
        finally
        {
            lock (_lock) _followers.Remove(conn);
            try { conn.Client.Close(); } catch { }
            // Wake any waiter so it re-evaluates quorum against the now-smaller set.
            PulseAcks();
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

    /// <summary>
    /// Stream a snapshot file to one specific follower as SnapshotStart →
    /// SnapshotChunk* → SnapshotEnd. Synchronous because we hold the write
    /// path in HandleFollowerHandshakeAsync — keeping the I/O sync there
    /// keeps the per-follower state machine readable.
    /// </summary>
    private static void StreamSnapshotToFollower(NetworkStream stream, string snapshotPath, ulong snapshotSeq)
    {
        var fi = new FileInfo(snapshotPath);
        var startPayload = new byte[16];
        BitConverter.TryWriteBytes(startPayload.AsSpan(0, 8), (long)fi.Length);
        BitConverter.TryWriteBytes(startPayload.AsSpan(8, 8), snapshotSeq);
        stream.Write(BuildRecord(new LogicalOp(snapshotSeq, LogicalOpType.SnapshotStart, "", startPayload)));

        // 64 KB chunks: matches the OS pagecache read-ahead unit and keeps
        // per-record overhead reasonable. Each chunk is one LogicalOp.
        const int ChunkSize = 64 * 1024;
        var buffer = new byte[ChunkSize];
        using (var fs = new FileStream(snapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            int read;
            while ((read = fs.Read(buffer, 0, ChunkSize)) > 0)
            {
                var chunk = new byte[read];
                Array.Copy(buffer, chunk, read);
                stream.Write(BuildRecord(new LogicalOp(snapshotSeq, LogicalOpType.SnapshotChunk, "", chunk)));
            }
        }

        stream.Write(BuildRecord(new LogicalOp(snapshotSeq, LogicalOpType.SnapshotEnd, "", Array.Empty<byte>())));
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
    private readonly string? _ownHttpEndpoint;
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

    /// <summary>Path the follower writes a snapshot to during a transfer
    /// (issue #20). When non-null and a SnapshotEnd arrives, the follower
    /// writes a sibling marker file (<c>{path}.seq</c>) holding the snapshot
    /// seq so the next Open of the data file can integrate it.</summary>
    public string? SnapshotTempPath { get; init; }

    /// <summary>Fired after SnapshotEnd, with the temp path holding the
    /// received snapshot bytes and the seq it represents. Subscribers
    /// (e.g. the engine) can use this to atomically swap the data file
    /// in-process. If unset, the snapshot just lands at SnapshotTempPath
    /// and the next Open integrates via the marker.</summary>
    public Action<string, ulong>? OnSnapshotReceived { get; set; }

    /// <summary>Issue #96 — this node's own term, advertised to the leader on
    /// the handshake so an old leader superseded by us can fence itself.</summary>
    public ulong OwnTerm { get; set; }

    /// <summary>Issue #96 — invoked with the leader's advertised term right
    /// after handshake. The engine wires this to <c>ObserveTerm</c>; it returns
    /// true when the leader is STALE (its term is lower than ours), telling the
    /// follower to reject and disconnect from that superseded leader.</summary>
    public Func<ulong, bool>? OnLeaderTerm { get; set; }

    // In-flight snapshot reception state. Only valid between SnapshotStart
    // and SnapshotEnd; reset on End or on disconnect mid-transfer.
    private FileStream? _snapshotWriter;
    private ulong _snapshotInFlightSeq;

    public LogicalReplicationFollower(string host, int port, string seqFilePath, Action<LogicalOp> apply, string? secret = null, string? ownHttpEndpoint = null)
    {
        _host = host;
        _port = port;
        _seqFilePath = seqFilePath;
        _apply = apply;
        _secret = secret;
        _ownHttpEndpoint = ownHttpEndpoint;
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

            // Issue #51 — advertise this follower's own HTTP base URL so
            // the leader can expose it on /replication/status. Always write
            // the 2-byte length prefix; legacy leaders that don't expect it
            // simply leave the bytes in their socket buffer and proceed
            // with catchup. Empty string when no endpoint is configured.
            var endpointBytes = _ownHttpEndpoint is null
                ? Array.Empty<byte>()
                : System.Text.Encoding.UTF8.GetBytes(_ownHttpEndpoint);
            var endpointLenPrefix = new byte[2];
            BitConverter.TryWriteBytes(endpointLenPrefix, (ushort)endpointBytes.Length);
            await stream.WriteAsync(endpointLenPrefix, _cts.Token);
            if (endpointBytes.Length > 0)
                await stream.WriteAsync(endpointBytes, _cts.Token);

            // Issue #96 — advertise our term, then read the leader's term. A
            // leader whose term is LOWER than ours is a superseded (stale)
            // leader; refuse it. Back-compat: a legacy leader sends no term, so
            // a short read timeout leaves leaderTerm effectively unknown and we
            // proceed without fencing (pre-#96 behaviour).
            var ownTermBuf = new byte[8];
            BitConverter.TryWriteBytes(ownTermBuf, OwnTerm);
            await stream.WriteAsync(ownTermBuf, _cts.Token);

            try
            {
                using var leaderTermTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                using var linkedLeaderTermCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, leaderTermTimeout.Token);
                var leaderTermBuf = new byte[8];
                if (await ReadExactAsync(stream, leaderTermBuf, linkedLeaderTermCts.Token))
                {
                    var leaderTerm = BitConverter.ToUInt64(leaderTermBuf, 0);
                    if (OnLeaderTerm?.Invoke(leaderTerm) == true)
                    {
                        Console.WriteLine($"[Fencing] Leader advertised stale term {leaderTerm} < our term {OwnTerm} — refusing this leader.");
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { /* legacy leader → no term; proceed */ }

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

                // Snapshot transfer (#20). Intercept before the apply
                // callback so the engine doesn't try to interpret these
                // as user ops. Snapshot transfer pauses normal seq
                // tracking; the SnapshotEnd handler resumes it at the
                // snapshot's seq.
                if (opType is LogicalOpType.SnapshotStart or LogicalOpType.SnapshotChunk or LogicalOpType.SnapshotEnd)
                {
                    HandleSnapshotOp(opType, data);
                    continue;
                }

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

                // Issue #95 — acknowledge the durably-applied seq so the leader
                // can offer semi-sync (wait for a follower before acking the
                // client). Best-effort: a legacy leader ignores the extra bytes.
                try
                {
                    var ack = new byte[4 + 1 + 8];
                    ack[0] = (byte)'D'; ack[1] = (byte)'F'; ack[2] = (byte)'L'; ack[3] = (byte)'R';
                    ack[4] = (byte)LogicalOpType.Ack;
                    BitConverter.TryWriteBytes(ack.AsSpan(5), seq);
                    await stream.WriteAsync(ack, _cts.Token);
                }
                catch { /* ack is advisory; a write failure just means no semi-sync credit */ }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Console.WriteLine($"[LogicalRep] Follower stream error: {ex.Message}"); }
    }

    private void HandleSnapshotOp(LogicalOpType opType, byte[] data)
    {
        if (SnapshotTempPath is null)
        {
            // No snapshot path configured — drop the bytes. The follower's
            // operator hasn't opted in to snapshot transfer, so the only
            // sensible behaviour is to log + ignore. Catch-up via OpLog
            // catches up the rest if reachable.
            if (opType == LogicalOpType.SnapshotStart)
                Console.WriteLine("[LogicalRep] Snapshot offered by leader but no SnapshotTempPath configured — dropping.");
            return;
        }

        switch (opType)
        {
            case LogicalOpType.SnapshotStart:
                {
                    // Payload: [TotalSize:8][SnapshotSeq:8]. We don't actually
                    // need TotalSize for streaming, but log it for diagnostics.
                    long totalSize = BitConverter.ToInt64(data, 0);
                    _snapshotInFlightSeq = BitConverter.ToUInt64(data, 8);
                    Console.WriteLine($"[LogicalRep] Snapshot transfer started: {totalSize} bytes @ seq {_snapshotInFlightSeq}");
                    // Truncate any stale half-snapshot from a prior failed transfer.
                    _snapshotWriter?.Dispose();
                    _snapshotWriter = new FileStream(SnapshotTempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    break;
                }
            case LogicalOpType.SnapshotChunk:
                _snapshotWriter?.Write(data, 0, data.Length);
                break;
            case LogicalOpType.SnapshotEnd:
                {
                    if (_snapshotWriter is null)
                    {
                        Console.WriteLine("[LogicalRep] SnapshotEnd received without prior Start — ignoring.");
                        return;
                    }
                    _snapshotWriter.Flush(true);
                    _snapshotWriter.Dispose();
                    _snapshotWriter = null;
                    Console.WriteLine($"[LogicalRep] Snapshot received at {SnapshotTempPath} (seq {_snapshotInFlightSeq})");

                    // Drop a marker so the next Open of the data file
                    // integrates the snapshot. The marker's content is the
                    // 8-byte snapshot seq.
                    var markerPath = SnapshotTempPath + ".seq";
                    try { File.WriteAllBytes(markerPath, BitConverter.GetBytes(_snapshotInFlightSeq)); }
                    catch (Exception ex) { Console.WriteLine($"[LogicalRep] Failed to write snapshot seq marker: {ex.Message}"); }

                    // Update our in-memory seq + persisted seq. Subsequent
                    // ops on the wire are assumed to be from snapshotSeq+1.
                    _lastAppliedSeq = _snapshotInFlightSeq;
                    SaveSeq(_snapshotInFlightSeq);

                    // Optional in-process hook (e.g. engine integrating live).
                    try { OnSnapshotReceived?.Invoke(SnapshotTempPath, _snapshotInFlightSeq); }
                    catch (Exception ex) { Console.WriteLine($"[LogicalRep] OnSnapshotReceived handler threw: {ex.Message}"); }
                    break;
                }
        }
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
