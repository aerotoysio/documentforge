using DocumentForge.Cli;
using DocumentForge.Cli.Blobs;
using DocumentForge.Cli.Commands;
using DocumentForge.Engine;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// Blob follow-up (#109) — backup + restore must capture the out-of-line blob
/// store, so a restored database's attachments come back with it.
/// </summary>
public sealed class BlobBackupTests : IDisposable
{
    private readonly string _dataDir;
    private readonly DatabaseRegistry _registry = new();

    public BlobBackupTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"blobbk_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
        _registry.AttachOrCreate("_system", Path.Combine(_dataDir, "_system.dfdb"));
    }

    [Fact]
    public async Task BackupAndRestore_CarriesBlobStore_AndAttachmentResolves()
    {
        _registry.AttachOrCreate("shop", Path.Combine(_dataDir, "shop.dfdb"));
        var db = _registry.Get("shop");

        // Stage a blob in the sibling store + a document referencing it.
        var blobs = new BlobStoreManager();
        string hash; long len;
        using (var store = blobs.For(db))
            (hash, len) = await store.PutAsync(new MemoryStream(Enumerable.Repeat((byte)42, 5000).ToArray()));
        var descriptor = BsonDocumentFrom(hash, len);
        var id = db.Insert("products", $$"""{"name":"widget","photo":{{descriptor}}}""");

        var backup = new BackupManager(_registry, _dataDir);
        var record = backup.BackupOne("shop");

        // The backup captured the blob sidecars next to the snapshot.
        Assert.True(File.Exists(record.Path), "snapshot missing");
        Assert.True(File.Exists(record.Path + ".blobs"), "blob data not captured");
        Assert.True(File.Exists(record.Path + ".blobs.idx"), "blob index not captured");

        // Restore to a new database and confirm the blob bytes came along.
        var targetPath = backup.RestoreToNewDatabase(record.Id, "shop_restored");
        Assert.True(File.Exists(targetPath + ".blobs"));
        using (var restoredStore = new BlobStore(targetPath + ".blobs"))
        {
            Assert.NotNull(restoredStore.OpenRead(hash));
            using var r = restoredStore.OpenRead(hash)!;
            var back = new MemoryStream(); r.CopyTo(back);
            Assert.Equal(Enumerable.Repeat((byte)42, 5000).ToArray(), back.ToArray());
        }

        // The restored document still references the same blob hash.
        var restored = _registry.Get("shop_restored");
        var doc = restored.GetCollection("products")!.FindById(id)!;
        Assert.Contains(hash, doc.ToJson());
    }

    [Fact]
    public void Backup_DbWithoutBlobs_SkipsSidecarsCleanly()
    {
        _registry.AttachOrCreate("plain", Path.Combine(_dataDir, "plain.dfdb"));
        _registry.Get("plain").Insert("t", """{"x":1}""");

        var backup = new BackupManager(_registry, _dataDir);
        var record = backup.BackupOne("plain");
        Assert.True(File.Exists(record.Path));
        Assert.False(File.Exists(record.Path + ".blobs")); // nothing to capture, no error
    }

    private static string BsonDocumentFrom(string hash, long len) =>
        BlobStoreManager.DescriptorJson(hash, len, "image/png");

    public void Dispose()
    {
        try { _registry.Dispose(); } catch { }
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
    }
}
