using System.Net;
using System.Text;
using DocumentForge.Cli.Blobs;
using DocumentForge.Cli.Commands;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// Blob follow-up (#109) — lazy blob replication. A follower holds the
/// replicated descriptor but not the bytes; the first read pulls them from the
/// leader by content hash. Content-addressing makes the pull self-verifying.
/// </summary>
public sealed class BlobReplicationTests : IDisposable
{
    private readonly List<IDisposable> _disposables = new();
    private readonly List<string> _bases = new();
    private static readonly HttpClient _http = new();

    private BlobStore NewStore()
    {
        var basePath = Path.Combine(Path.GetTempPath(), $"blobrep_{Guid.NewGuid():N}.blobs");
        _bases.Add(basePath);
        var s = new BlobStore(basePath);
        _disposables.Add(s);
        return s;
    }

    private static Stream Bytes(string s) => new MemoryStream(Encoding.UTF8.GetBytes(s));

    [Fact]
    public async Task EnsureLocal_PullsMissingBytes_FromUpstream()
    {
        var leader = NewStore();
        var (hash, _) = await leader.PutAsync(Bytes("replicate me across the cluster"));

        var follower = NewStore();
        Assert.Null(follower.LengthOf(hash)); // follower has only the (implied) descriptor

        var mgr = new BlobStoreManager { Upstream = (h, ct) => Task.FromResult(leader.OpenRead(h)) };
        Assert.True(await mgr.EnsureLocalAsync(follower, hash));
        Assert.NotNull(follower.LengthOf(hash)); // bytes are now local
        using var r = follower.OpenRead(hash)!;
        Assert.Equal("replicate me across the cluster", new StreamReader(r).ReadToEnd());
    }

    [Fact]
    public async Task EnsureLocal_NoUpstream_ReturnsFalse()
    {
        var follower = NewStore();
        var mgr = new BlobStoreManager(); // no Upstream
        Assert.False(await mgr.EnsureLocalAsync(follower, new string('a', 64)));
    }

    [Fact]
    public async Task EnsureLocal_AlreadyPresent_DoesNotCallUpstream()
    {
        var follower = NewStore();
        var (hash, _) = await follower.PutAsync(Bytes("already here"));
        bool called = false;
        var mgr = new BlobStoreManager { Upstream = (h, ct) => { called = true; return Task.FromResult<Stream?>(null); } };
        Assert.True(await mgr.EnsureLocalAsync(follower, hash));
        Assert.False(called);
    }

    [Fact]
    public async Task EnsureLocal_UpstreamReturnsWrongBytes_Rejected()
    {
        // Integrity: an upstream that answers with bytes that don't hash to the
        // requested id must NOT satisfy the request — content-addressing catches
        // it (the bytes get filed under their real hash; the requested one stays
        // missing) so we never serve the wrong blob.
        var follower = NewStore();
        var requested = new string('b', 64); // some hash we ask for
        var mgr = new BlobStoreManager { Upstream = (h, ct) => Task.FromResult<Stream?>(Bytes("these are the WRONG bytes")) };

        Assert.False(await mgr.EnsureLocalAsync(follower, requested));
        Assert.Null(follower.LengthOf(requested)); // requested id still not satisfied
    }

    [Fact]
    public async Task Http_RawFetch_PullsOverHttp()
    {
        // Stand up a "leader" HTTP server exposing GET /blobs/raw/{hash} over a
        // real store, then have a follower manager pull through it via HttpClient.
        var leaderStore = NewStore();
        var (hash, _) = await leaderStore.PutAsync(Bytes("bytes over the wire"));

        var port = FreePort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        app.MapGet("/blobs/raw/{sha256}", (string sha256, HttpResponse response) =>
        {
            if (!BlobHandler.IsValidBlobId(sha256)) return Results.BadRequest();
            var stream = leaderStore.OpenRead(sha256);
            return stream is null ? Results.NotFound() : Results.Stream(stream, "application/octet-stream");
        });
        app.StartAsync().GetAwaiter().GetResult();
        _disposables.Add(new Stopper(app));
        var leaderUrl = $"http://127.0.0.1:{port}";

        var follower = NewStore();
        var mgr = new BlobStoreManager
        {
            Upstream = async (h, ct) =>
            {
                var resp = await _http.GetAsync($"{leaderUrl}/blobs/raw/{h}", HttpCompletionOption.ResponseHeadersRead, ct);
                return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStreamAsync(ct) : null;
            }
        };

        Assert.True(await mgr.EnsureLocalAsync(follower, hash));
        Assert.Equal("bytes over the wire", new StreamReader(follower.OpenRead(hash)!).ReadToEnd());

        // A hash the leader doesn't have → null → not satisfied.
        Assert.False(await mgr.EnsureLocalAsync(follower, new string('c', 64)));
    }

    private static int FreePort()
    {
        using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public void Dispose()
    {
        for (int i = _disposables.Count - 1; i >= 0; i--)
            try { _disposables[i].Dispose(); } catch { }
        foreach (var b in _bases)
            foreach (var ext in new[] { "", ".idx" })
                try { File.Delete(b + ext); } catch { }
    }

    private sealed class Stopper : IDisposable
    {
        private readonly WebApplication _app;
        public Stopper(WebApplication app) => _app = app;
        public void Dispose()
        {
            try { using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)); _app.StopAsync(cts.Token).GetAwaiter().GetResult(); } catch { }
            try { _app.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        }
    }
}
