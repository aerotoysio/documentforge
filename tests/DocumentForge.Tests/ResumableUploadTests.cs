using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DocumentForge.Cli.Blobs;
using DocumentForge.Engine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// Issue #71 — resumable chunked blob upload (Content-Range), so a hundred-MB
/// video survives a dropped mobile connection and resumes from the exact byte.
/// </summary>
public sealed class ResumableUploadTests : IDisposable
{
    private readonly List<IDisposable> _disposables = new();
    private readonly List<string> _bases = new();
    private static readonly HttpClient _http = new();

    private ResumableUploadStore NewStore()
    {
        var basePath = Path.Combine(Path.GetTempPath(), $"rupload_{Guid.NewGuid():N}.blobs");
        _bases.Add(basePath);
        return new ResumableUploadStore(basePath);
    }

    private static async Task<ResumableUploadStore.ChunkResult> Append(
        ResumableUploadStore s, string key, long start, long total, byte[] chunk)
        => await s.AppendChunkAsync(key, start, total, new MemoryStream(chunk), CancellationToken.None);

    // ---------------------------------------------------------------- unit

    [Fact]
    public async Task SequentialChunks_Assemble_AndResumeOffsetIsFileLength()
    {
        var s = NewStore();
        const string key = "c/d/f";

        Assert.Equal(0, s.ReceivedBytes(key)); // nothing yet

        var r1 = await Append(s, key, 0, 9, new byte[] { 1, 2, 3, 4 });
        Assert.Equal(ResumableUploadStore.ChunkStatus.Partial, r1.Status);
        Assert.Equal(4, r1.Received);
        Assert.Equal(4, s.ReceivedBytes(key)); // resume offset == bytes stored

        var r2 = await Append(s, key, 4, 9, new byte[] { 5, 6, 7 });
        Assert.Equal(ResumableUploadStore.ChunkStatus.Partial, r2.Status);
        Assert.Equal(7, r2.Received);

        var r3 = await Append(s, key, 7, 9, new byte[] { 8, 9 });
        Assert.Equal(ResumableUploadStore.ChunkStatus.Complete, r3.Status);
        Assert.Equal(9, r3.Received);
        Assert.NotNull(r3.PartPath);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, File.ReadAllBytes(r3.PartPath!));
    }

    [Fact]
    public async Task OutOfOrderChunk_ReportsExpectedOffset_AndDoesNotCorrupt()
    {
        var s = NewStore();
        const string key = "k";
        await Append(s, key, 0, 10, new byte[] { 1, 2, 3 }); // received = 3

        // A chunk that starts at the wrong offset is rejected with the offset to
        // resume from — the session file is left untouched.
        var bad = await Append(s, key, 7, 10, new byte[] { 9, 9 });
        Assert.Equal(ResumableUploadStore.ChunkStatus.OffsetMismatch, bad.Status);
        Assert.Equal(3, bad.Received);
        Assert.Equal(3, s.ReceivedBytes(key)); // unchanged

        // Resuming from the correct offset works.
        var ok = await Append(s, key, 3, 10, new byte[] { 4, 5, 6, 7, 8, 9, 10 });
        Assert.Equal(ResumableUploadStore.ChunkStatus.Complete, ok.Status);
    }

    [Fact]
    public async Task Overflow_DiscardsSession()
    {
        var s = NewStore();
        const string key = "k";
        var r = await Append(s, key, 0, 3, new byte[] { 1, 2, 3, 4, 5 }); // more than total
        Assert.Equal(ResumableUploadStore.ChunkStatus.Overflow, r.Status);
        Assert.Equal(0, s.ReceivedBytes(key)); // session was dropped
    }

    [Fact]
    public async Task ResumeOffset_SurvivesNewStoreInstance()
    {
        // The resume offset is the .part file's length — a fresh store instance
        // (simulating a server restart) sees exactly the bytes already stored.
        var basePath = Path.Combine(Path.GetTempPath(), $"rupload_{Guid.NewGuid():N}.blobs");
        _bases.Add(basePath);
        const string key = "k";
        await new ResumableUploadStore(basePath).AppendChunkAsync(key, 0, 100, new MemoryStream(new byte[40]), CancellationToken.None);

        var reopened = new ResumableUploadStore(basePath);
        Assert.Equal(40, reopened.ReceivedBytes(key));
    }

    [Fact]
    public async Task SweepStale_RemovesOldSessionsOnly()
    {
        var s = NewStore();
        await Append(s, "old", 0, 100, new byte[10]);
        // maxAge in the future relative to "now" → nothing removed.
        Assert.Equal(0, s.SweepStale(TimeSpan.FromHours(1), DateTime.UtcNow));
        // Treat "now" as far ahead → the session is stale and reclaimed.
        Assert.Equal(1, s.SweepStale(TimeSpan.FromHours(1), DateTime.UtcNow.AddHours(2)));
    }

    [Theory]
    [InlineData("bytes 0-9/100", false, 0L, 100L)]
    [InlineData("bytes 40-79/100", false, 40L, 100L)]
    [InlineData("bytes */100", true, 0L, 100L)]
    public void ContentRange_ParsesValidForms(string header, bool probe, long start, long total)
    {
        Assert.True(BlobHandler.TryParseContentRange(header, out var isProbe, out var s, out var t));
        Assert.Equal(probe, isProbe);
        Assert.Equal(start, s);
        Assert.Equal(total, t);
    }

    [Theory]
    [InlineData("bytes 0-9/*")]     // unknown total unsupported
    [InlineData("items 0-9/100")]   // wrong unit
    [InlineData("bytes 100-109/100")] // start beyond total
    [InlineData("bytes 5-3/100")]   // end before start
    [InlineData("")]
    public void ContentRange_RejectsInvalid(string header)
        => Assert.False(BlobHandler.TryParseContentRange(header, out _, out _, out _));

    // ---------------------------------------------------------------- http e2e

    [Fact]
    public async Task Http_ChunkedUpload_ThreeChunks_AssemblesAndDownloads()
    {
        var (baseUrl, db) = BootBlobServer();
        var docId = db.Insert("media", """{"title":"clip"}""").ToString();

        // 25-byte payload uploaded in 10 + 10 + 5.
        var payload = Enumerable.Range(0, 25).Select(i => (byte)i).ToArray();
        var url = $"{baseUrl}/collections/media/{docId}/blob/video";

        var r1 = await PutChunk(url, payload[..10], 0, 25);
        Assert.Equal(HttpStatusCode.Accepted, r1.StatusCode);
        var r2 = await PutChunk(url, payload[10..20], 10, 25);
        Assert.Equal(HttpStatusCode.Accepted, r2.StatusCode);
        var r3 = await PutChunk(url, payload[20..25], 20, 25);
        Assert.Equal(HttpStatusCode.OK, r3.StatusCode); // final chunk → descriptor recorded

        // The whole blob downloads back byte-for-byte.
        var got = await _http.GetByteArrayAsync(url);
        Assert.Equal(payload, got);

        // And SELECT returns the descriptor, never the bytes.
        var doc = db.GetCollection("media")!.FindById(new DocumentForge.Core.DocumentId(Guid.Parse(docId)))!;
        Assert.True(doc.ContainsKey("video"));
        Assert.DoesNotContain("AAECAw", doc.ToJson()); // not base64 payload
    }

    [Fact]
    public async Task Http_StatusProbe_ReportsReceived_ThenResumes()
    {
        var (baseUrl, db) = BootBlobServer();
        var docId = db.Insert("media", """{"t":1}""").ToString();
        var url = $"{baseUrl}/collections/media/{docId}/blob/v";
        var payload = Enumerable.Range(0, 12).Select(i => (byte)i).ToArray();

        await PutChunk(url, payload[..8], 0, 12);

        // Probe: how much do you have? (Content-Range: bytes */12, empty body)
        var probe = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(Array.Empty<byte>()) };
        probe.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes */12");
        var pr = await _http.SendAsync(probe);
        Assert.Equal(HttpStatusCode.OK, pr.StatusCode);
        using var pj = JsonDocument.Parse(await pr.Content.ReadAsStringAsync());
        Assert.Equal(8, pj.RootElement.GetProperty("received").GetInt32());

        // Resume from 8 → completes.
        var fin = await PutChunk(url, payload[8..12], 8, 12);
        Assert.Equal(HttpStatusCode.OK, fin.StatusCode);
    }

    [Fact]
    public async Task Http_WrongOffset_Returns409_WithExpectedOffset()
    {
        var (baseUrl, db) = BootBlobServer();
        var docId = db.Insert("media", """{"t":1}""").ToString();
        var url = $"{baseUrl}/collections/media/{docId}/blob/v";

        await PutChunk(url, new byte[5], 0, 20); // received = 5
        var bad = await PutChunk(url, new byte[5], 15, 20); // wrong start
        Assert.Equal(HttpStatusCode.Conflict, bad.StatusCode);
        using var j = JsonDocument.Parse(await bad.Content.ReadAsStringAsync());
        Assert.Equal(5, j.RootElement.GetProperty("expectedOffset").GetInt32());
    }

    private static async Task<HttpResponseMessage> PutChunk(string url, byte[] chunk, long start, long total)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(chunk) };
        req.Content.Headers.TryAddWithoutValidation("Content-Range", $"bytes {start}-{start + chunk.Length - 1}/{total}");
        return await _http.SendAsync(req);
    }

    private (string baseUrl, DocumentForgeDb db) BootBlobServer()
    {
        var port = FreePort();
        var path = Path.Combine(Path.GetTempPath(), $"rup_{Guid.NewGuid():N}.dfdb");
        _bases.Add(path); _bases.Add(path + ".blobs");
        var db = DocumentForgeDb.Create(path);
        _disposables.Add(db);
        var blobs = new BlobStoreManager();
        _disposables.Add(blobs);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        app.MapPut("/collections/{name}/{id}/blob/{field}", (string name, string id, string field, HttpRequest request) =>
            BlobHandler.Upload(db, blobs.For(db), name, id, field, request));
        app.MapGet("/collections/{name}/{id}/blob/{field}", (string name, string id, string field, HttpRequest request, HttpResponse response) =>
            BlobHandler.Download(db, blobs, name, id, field, request, response));
        app.StartAsync().GetAwaiter().GetResult();
        _disposables.Add(new Stopper(app));
        return ($"http://127.0.0.1:{port}", db);
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
            foreach (var ext in new[] { "", ".idx", ".wal", ".recovery", ".lock" })
                try { File.Delete(b + ext); } catch { }
        foreach (var b in _bases)
            try { if (Directory.Exists(b + ".uploads")) Directory.Delete(b + ".uploads", true); } catch { }
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
