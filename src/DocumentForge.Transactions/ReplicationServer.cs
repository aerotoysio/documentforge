using System.Net;
using System.Net.Sockets;
using DocumentForge.Core;

namespace DocumentForge.Transactions;

/// <summary>
/// Leader-side replication server. Accepts follower connections and streams
/// page writes to them as they happen. Protocol is super-simple binary:
///
/// On connect: send a snapshot of the entire data file (optional phase 1 - for now assume
/// follower is empty or caller has already copied the file).
///
/// Stream format per page write:
///   [Magic:4 "DFRP"][PageId:4][Checksum:4][PageData:8192]
/// </summary>
public sealed class ReplicationServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly List<TcpClient> _followers = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptTask;
    private bool _disposed;

    public int Port { get; }
    public int FollowerCount { get { lock (_lock) return _followers.Count; } }

    public ReplicationServer(int port)
    {
        Port = port;
        _listener = new TcpListener(IPAddress.Any, port);
    }

    public void Start()
    {
        _listener.Start();
        _acceptTask = Task.Run(AcceptFollowersAsync);
    }

    private async Task AcceptFollowersAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                lock (_lock) _followers.Add(client);
                Console.WriteLine($"[Replication] Follower connected: {client.Client.RemoteEndPoint}");
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[Replication] Accept error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Broadcast a page write to all connected followers.
    /// Callers should typically invoke this from a PreFlushHook.
    /// </summary>
    public void BroadcastPageWrite(PageId pageId, byte[] pageData)
    {
        if (pageData.Length != Constants.PageSize) return;

        var record = BuildRecord(pageId, pageData);
        lock (_lock)
        {
            var dead = new List<TcpClient>();
            foreach (var follower in _followers)
            {
                try
                {
                    follower.GetStream().Write(record, 0, record.Length);
                }
                catch
                {
                    dead.Add(follower);
                }
            }
            foreach (var d in dead)
            {
                _followers.Remove(d);
                try { d.Close(); } catch { }
                Console.WriteLine("[Replication] Follower disconnected (dead socket)");
            }
        }
    }

    private static byte[] BuildRecord(PageId pageId, byte[] pageData)
    {
        var record = new byte[12 + Constants.PageSize];
        record[0] = (byte)'D'; record[1] = (byte)'F'; record[2] = (byte)'R'; record[3] = (byte)'P';
        BitConverter.TryWriteBytes(record.AsSpan(4), pageId.Value);
        BitConverter.TryWriteBytes(record.AsSpan(8), RecoveryLog.Crc32(pageData));
        pageData.CopyTo(record, 12);
        return record;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        lock (_lock)
        {
            foreach (var f in _followers)
                try { f.Close(); } catch { }
            _followers.Clear();
        }
        _disposed = true;
    }
}
