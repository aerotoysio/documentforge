using System.Net;
using System.Net.Http;
using DocumentForge.Cli.Auth;
using DocumentForge.Cli.Blobs;
using DocumentForge.Cli.Commands;
using DocumentForge.Engine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// Blob follow-up (#109/#71) — the out-of-line blob + attachment API scoped to a
/// named database (<c>/db/{name}/...</c>), so blobs work in the multi-DB hosting
/// model and each tenant's bytes stay in its own sibling store.
/// </summary>
public sealed class ScopedBlobRoutesTests : IDisposable
{
    private readonly List<IDisposable> _disposables = new();
    private readonly List<string> _dirs = new();
    private static readonly HttpClient _http = new();

    private (DatabaseRegistry registry, string baseUrl, string dataDir) Boot()
    {
        var port = FreePort();
        var dataDir = Path.Combine(Path.GetTempPath(), $"sblob_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        _dirs.Add(dataDir);

        var registry = new DatabaseRegistry();
        _disposables.Add(registry);
        var blobs = new BlobStoreManager();
        _disposables.Add(blobs);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        app.Use(async (ctx, next) => { ctx.Items[AuthContext.ContextKey] = AuthContext.DevMode(); await next(); });
        DatabaseEndpoints.Map(app, registry, dataDir, catalog: null, backupManager: null, walArchiver: null, pitr: null, blobs: blobs);
        app.StartAsync().GetAwaiter().GetResult();
        _disposables.Add(new Stopper(app));
        return (registry, $"http://127.0.0.1:{port}", dataDir);
    }

    [Fact]
    public async Task ScopedBlob_UploadDownload_RoundTrips_PerDatabase()
    {
        var (registry, baseUrl, dataDir) = Boot();
        registry.AttachOrCreate("media", Path.Combine(dataDir, "media.dfdb"));
        var db = registry.Get("media");
        var id = db.Insert("clips", """{"title":"a"}""").ToString();

        var payload = Enumerable.Range(0, 2048).Select(i => (byte)(i % 251)).ToArray();
        var put = await _http.PutAsync($"{baseUrl}/db/media/collections/clips/{id}/blob/file", new ByteArrayContent(payload));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var got = await _http.GetByteArrayAsync($"{baseUrl}/db/media/collections/clips/{id}/blob/file");
        Assert.Equal(payload, got);

        // The bytes live in THIS db's sibling store, not the data file.
        Assert.True(File.Exists(Path.Combine(dataDir, "media.dfdb.blobs")));
        // SELECT returns the descriptor, not the payload.
        var doc = db.GetCollection("clips")!.FindById(new DocumentForge.Core.DocumentId(Guid.Parse(id)))!;
        Assert.True(doc.ContainsKey("file"));
    }

    [Fact]
    public async Task ScopedBlob_ChunkedUpload_Resumable()
    {
        var (registry, baseUrl, dataDir) = Boot();
        registry.AttachOrCreate("media", Path.Combine(dataDir, "media.dfdb"));
        var id = registry.Get("media").Insert("clips", """{"t":1}""").ToString();
        var url = $"{baseUrl}/db/media/collections/clips/{id}/attachments/movie";
        var payload = Enumerable.Range(0, 30).Select(i => (byte)i).ToArray();

        var r1 = await PutChunk(url, payload[..12], 0, 30);
        Assert.Equal(HttpStatusCode.Accepted, r1.StatusCode);
        var r2 = await PutChunk(url, payload[12..30], 12, 30);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);

        var got = await _http.GetByteArrayAsync(url);
        Assert.Equal(payload, got);
    }

    [Fact]
    public async Task ScopedBlob_StoresAreIsolatedPerDatabase()
    {
        var (registry, baseUrl, dataDir) = Boot();
        registry.AttachOrCreate("alpha", Path.Combine(dataDir, "alpha.dfdb"));
        registry.AttachOrCreate("beta", Path.Combine(dataDir, "beta.dfdb"));
        var idA = registry.Get("alpha").Insert("c", """{"x":1}""").ToString();

        await _http.PutAsync($"{baseUrl}/db/alpha/collections/c/{idA}/blob/f", new ByteArrayContent(new byte[500]));

        var statsA = await _http.GetStringAsync($"{baseUrl}/db/alpha/blobs/stats");
        var statsB = await _http.GetStringAsync($"{baseUrl}/db/beta/blobs/stats");
        Assert.Contains("\"count\":1", statsA.Replace(" ", ""));
        Assert.Contains("\"count\":0", statsB.Replace(" ", "")); // beta has no blobs
    }

    [Fact]
    public async Task ScopedBlob_UnknownDatabase_Returns404()
    {
        var (_, baseUrl, _) = Boot();
        var resp = await _http.PutAsync($"{baseUrl}/db/ghost/collections/c/{Guid.NewGuid()}/blob/f", new ByteArrayContent(new byte[4]));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    private static async Task<HttpResponseMessage> PutChunk(string url, byte[] chunk, long start, long total)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(chunk) };
        req.Content.Headers.TryAddWithoutValidation("Content-Range", $"bytes {start}-{start + chunk.Length - 1}/{total}");
        return await _http.SendAsync(req);
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
        foreach (var d in _dirs)
            try { Directory.Delete(d, recursive: true); } catch { }
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
