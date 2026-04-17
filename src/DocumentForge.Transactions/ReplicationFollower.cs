using System.Net.Sockets;
using DocumentForge.Core;

namespace DocumentForge.Transactions;

/// <summary>
/// Follower-side replication client. Connects to a leader and applies streamed page writes
/// directly to the local data file. The engine must re-open the file after replication
/// catches up (or on demand) to see the changes.
///
/// For MVP: applies writes to the file directly, bypassing the follower's page cache.
/// Simplest correct implementation - avoids cache coherence issues.
/// </summary>
public sealed class ReplicationFollower : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly Action<PageId, byte[]> _applyPage;
    private TcpClient? _client;
    private Task? _streamTask;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;
    private long _pagesApplied;

    public long PagesApplied => Interlocked.Read(ref _pagesApplied);

    public event Action<PageId>? PageApplied;

    /// <summary>
    /// Create a follower that applies incoming page writes via a callback provided by the engine.
    /// The callback is expected to write the page through the engine's cache/data file path.
    /// </summary>
    public ReplicationFollower(string host, int port, Action<PageId, byte[]> applyPage)
    {
        _host = host;
        _port = port;
        _applyPage = applyPage;
    }

    public void Start()
    {
        _streamTask = Task.Run(StreamAsync);
    }

    private async Task StreamAsync()
    {
        try
        {
            _client = new TcpClient();
            await _client.ConnectAsync(_host, _port, _cts.Token);
            Console.WriteLine($"[Replication] Connected to leader at {_host}:{_port}");

            var stream = _client.GetStream();
            const int recordSize = 12 + Constants.PageSize;
            var buffer = new byte[recordSize];

            while (!_cts.IsCancellationRequested)
            {
                int totalRead = 0;
                while (totalRead < recordSize)
                {
                    int n = await stream.ReadAsync(buffer.AsMemory(totalRead, recordSize - totalRead), _cts.Token);
                    if (n == 0) return; // leader disconnected
                    totalRead += n;
                }

                if (buffer[0] != (byte)'D' || buffer[1] != (byte)'F' ||
                    buffer[2] != (byte)'R' || buffer[3] != (byte)'P')
                {
                    Console.WriteLine("[Replication] Bad magic - aborting");
                    return;
                }

                var pageId = new PageId(BitConverter.ToUInt32(buffer, 4));
                var expectedCrc = BitConverter.ToUInt32(buffer, 8);
                var actualCrc = RecoveryLog.Crc32(buffer.AsSpan(12, Constants.PageSize));
                if (actualCrc != expectedCrc)
                {
                    Console.WriteLine($"[Replication] Checksum mismatch on page {pageId} - skipping");
                    continue;
                }

                var pageData = new byte[Constants.PageSize];
                Array.Copy(buffer, 12, pageData, 0, Constants.PageSize);

                // Apply via the engine's callback (which writes through cache + data file)
                _applyPage(pageId, pageData);

                Interlocked.Increment(ref _pagesApplied);
                PageApplied?.Invoke(pageId);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[Replication] Follower stream error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _cts.Cancel();
        try { _client?.Close(); } catch { }
        _disposed = true;
    }
}
