using System.Text;
using DocumentForge.Cli.Blobs;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// Issues #109/#71 — the content-addressed out-of-line blob store: round-trip,
/// dedup, byte-range reads, restart durability, and mark-sweep GC.
/// </summary>
public sealed class BlobStoreTests : IDisposable
{
    private readonly string _base;

    public BlobStoreTests()
    {
        _base = Path.Combine(Path.GetTempPath(), $"blobstore_{Guid.NewGuid():N}.blobs");
    }

    private static Stream S(string text) => new MemoryStream(Encoding.UTF8.GetBytes(text));

    private static string ReadAll(Stream? s)
    {
        Assert.NotNull(s);
        using var r = new StreamReader(s!);
        return r.ReadToEnd();
    }

    [Fact]
    public async Task PutThenRead_RoundTrips()
    {
        using var store = new BlobStore(_base);
        var (id, len) = await store.PutAsync(S("hello blob world"));
        Assert.Equal(16, len);
        Assert.True(store.Contains(id));
        Assert.Equal("hello blob world", ReadAll(store.OpenRead(id)));
    }

    [Fact]
    public async Task IdenticalContent_IsDeduped_ToOneCopy()
    {
        using var store = new BlobStore(_base);
        var (id1, _) = await store.PutAsync(S("same bytes"));
        var (id2, _) = await store.PutAsync(S("same bytes"));
        Assert.Equal(id1, id2);            // content-addressed → same id
        Assert.Equal(1, store.Count);      // stored once
    }

    [Fact]
    public async Task DifferentContent_GetsDistinctIds()
    {
        using var store = new BlobStore(_base);
        var (a, _) = await store.PutAsync(S("aaa"));
        var (b, _) = await store.PutAsync(S("bbb"));
        Assert.NotEqual(a, b);
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public async Task RangeRead_ReturnsTheRequestedSlice()
    {
        using var store = new BlobStore(_base);
        var (id, _) = await store.PutAsync(S("0123456789"));
        Assert.Equal("234", ReadAll(store.OpenRead(id, start: 2, count: 3)));
        Assert.Equal("789", ReadAll(store.OpenRead(id, start: 7)));           // to end
        Assert.Equal("0123456789", ReadAll(store.OpenRead(id)));             // whole
    }

    [Fact]
    public async Task Blobs_SurviveReopen()
    {
        string id;
        using (var store = new BlobStore(_base))
            (id, _) = await store.PutAsync(S("durable"));

        using (var reopened = new BlobStore(_base))
        {
            Assert.True(reopened.Contains(id));
            Assert.Equal("durable", ReadAll(reopened.OpenRead(id)));
        }
    }

    [Fact]
    public async Task GarbageCollect_RemovesUnreferenced_KeepsReferenced()
    {
        using var store = new BlobStore(_base);
        var (keep, _) = await store.PutAsync(S("keep me"));
        var (drop, _) = await store.PutAsync(S("collect me"));

        var collected = store.GarbageCollect(new HashSet<string> { keep });
        Assert.Equal(1, collected);
        Assert.True(store.Contains(keep));
        Assert.False(store.Contains(drop));
        Assert.Null(store.OpenRead(drop)); // collected → unreadable
    }

    [Fact]
    public async Task LargeBlob_StreamsWithoutBuffering()
    {
        // 8 MB of pseudo-random data — proves the streamed path handles sizes
        // that base64-in-JSON couldn't, and read-back matches byte-for-byte.
        using var store = new BlobStore(_base);
        var data = new byte[8 * 1024 * 1024];
        new Random(1234).NextBytes(data);
        var (id, len) = await store.PutAsync(new MemoryStream(data));
        Assert.Equal(data.Length, len);

        using var read = store.OpenRead(id)!;
        var back = new MemoryStream();
        read.CopyTo(back);
        Assert.Equal(data, back.ToArray());
    }

    public void Dispose()
    {
        foreach (var f in new[] { _base, _base + ".idx" })
            try { File.Delete(f); } catch { }
    }
}
