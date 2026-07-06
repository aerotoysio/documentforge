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

    [Fact]
    public async Task Compact_ReclaimsSpaceFromGCdBlobs_KeepsLiveReadable()
    {
        string keepId, keep2Id;
        long before;
        using (var store = new BlobStore(_base))
        {
            (keepId, _) = await store.PutAsync(new MemoryStream(Enumerable.Repeat((byte)7, 4096).ToArray()));
            var (dropId, _) = await store.PutAsync(new MemoryStream(Enumerable.Repeat((byte)9, 8192).ToArray()));
            (keep2Id, _) = await store.PutAsync(S("second keeper"));
            before = new FileInfo(_base).Length;

            Assert.Equal(1, store.GarbageCollect(new HashSet<string> { keepId, keep2Id })); // drop the 8 KB one

            var r = store.Compact();
            Assert.Equal(2, r.LiveBlobs);
            Assert.Equal(1, r.DroppedBlobs);
            Assert.True(r.BytesReclaimed >= 8192, $"expected >= 8192 reclaimed, got {r.BytesReclaimed}");

            // Data file physically shrank, live blobs still byte-for-byte correct.
            Assert.True(new FileInfo(_base).Length < before);
            Assert.False(store.Contains(dropId));
            using var read = store.OpenRead(keepId)!;
            var back = new MemoryStream(); read.CopyTo(back);
            Assert.Equal(Enumerable.Repeat((byte)7, 4096).ToArray(), back.ToArray());
            Assert.Equal("second keeper", ReadAll(store.OpenRead(keep2Id)));
        }

        // Survives reopen — the compacted index + data are authoritative.
        using (var reopened = new BlobStore(_base))
        {
            Assert.True(reopened.Contains(keepId));
            Assert.True(reopened.Contains(keep2Id));
            Assert.Equal(2, reopened.Count);
        }
    }

    [Fact]
    public async Task Compact_IsCrashSafe_FinishesSwapWhenMarkerPresent()
    {
        string id;
        using (var store = new BlobStore(_base))
            (id, _) = await store.PutAsync(S("survives a mid-swap crash"));

        // Simulate a crash AFTER the temps + marker were written but BEFORE the
        // rename: stage valid temps (a copy of the good files), corrupt the live
        // data file, and leave the marker. RecoverCompaction must finish the swap.
        File.Copy(_base, _base + ".compact", overwrite: true);
        File.Copy(_base + ".idx", _base + ".idx.compact", overwrite: true);
        File.WriteAllText(_base + ".compact.ready", "");
        File.WriteAllBytes(_base, Array.Empty<byte>()); // "torn" live data

        using (var recovered = new BlobStore(_base))
        {
            Assert.True(recovered.Contains(id));
            Assert.Equal("survives a mid-swap crash", ReadAll(recovered.OpenRead(id)));
            Assert.False(File.Exists(_base + ".compact.ready")); // marker cleared
        }
    }

    [Fact]
    public async Task Compact_DiscardsStrayTemps_WhenNoMarker()
    {
        string id;
        using (var store = new BlobStore(_base))
            (id, _) = await store.PutAsync(S("intact"));

        // A stray temp with no marker = an interrupted compaction before commit;
        // it must be discarded and the original data left authoritative.
        File.WriteAllText(_base + ".compact", "garbage");

        using (var store = new BlobStore(_base))
        {
            Assert.True(store.Contains(id));
            Assert.Equal("intact", ReadAll(store.OpenRead(id)));
            Assert.False(File.Exists(_base + ".compact")); // stray removed
        }
    }

    public void Dispose()
    {
        foreach (var f in new[] { _base, _base + ".idx", _base + ".compact", _base + ".idx.compact", _base + ".compact.ready" })
            try { File.Delete(f); } catch { }
    }
}
