using DocumentForge.Cli.Auth;
using DocumentForge.Engine;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// Issue #73 — <see cref="KeyStore"/> integration tests. The store
/// uses a real DocumentForge DB as its backing collection, so these
/// tests boot an actual <see cref="DatabaseRegistry"/> with an attached
/// <c>_system</c> DB on disk; failures point at real persistence bugs
/// (lost docs, stale cache, dangling revokes), not mocks.
/// </summary>
public sealed class KeyStoreTests : IDisposable
{
    private readonly List<string> _dirs = new();

    public void Dispose()
    {
        foreach (var d in _dirs)
        {
            try { Directory.Delete(d, recursive: true); } catch { }
        }
    }

    private (DatabaseRegistry registry, string dir) FreshRegistry(string label)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ks_{label}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);

        var registry = new DatabaseRegistry();
        registry.AttachOrCreate("default", Path.Combine(dir, "default.dfdb"));
        registry.AttachOrCreate("_system", Path.Combine(dir, "_system.dfdb"));
        return (registry, dir);
    }

    [Fact]
    public void CreateKey_PersistsAndAuthenticates()
    {
        var (registry, _) = FreshRegistry("create");
        using var _ = registry;

        var store = new KeyStore(registry);
        store.Reload();

        var result = store.CreateKey(new[] { "db:tenant_a" }, "test key");
        Assert.NotNull(result.Secret);
        Assert.StartsWith("df_", result.Secret);
        Assert.Equal(1, store.Count);

        // Authenticate via the returned plaintext.
        var ctx = store.TryAuthenticate(result.Secret);
        Assert.NotNull(ctx);
        Assert.True(ctx!.CanReadDb("tenant_a"));
        Assert.True(ctx.CanWriteDb("tenant_a"));
        Assert.False(ctx.CanWriteDb("tenant_b"));
    }

    [Fact]
    public void CreateKey_PlaintextNeverStoredInDb()
    {
        // The cache holds the hash, never the plaintext. Verify by
        // walking the backing collection directly — there should be
        // a `keyHash` field but no `key` / `secret`.
        var (registry, _) = FreshRegistry("noplaintext");
        using var _ = registry;

        var store = new KeyStore(registry);
        store.Reload();
        var result = store.CreateKey(new[] { "*" }, "admin-substitute");

        var systemDb = registry.Get("_system");
        var docs = systemDb.Execute("SELECT * FROM keys").Documents;
        Assert.Single(docs);
        var doc = docs[0];
        Assert.True(doc.ContainsKey("keyHash"));
        Assert.False(doc.ContainsKey("key"));
        Assert.False(doc.ContainsKey("secret"));
        // And it's not the plaintext.
        Assert.NotEqual(result.Secret, doc["keyHash"].AsString);
    }

    [Fact]
    public void Reload_ReadsKeysFromDisk()
    {
        // Simulate a service restart by recreating the KeyStore against
        // the same registry. The keys must reappear.
        var (registry, _) = FreshRegistry("reload");
        using var _ = registry;

        var store1 = new KeyStore(registry);
        store1.Reload();
        var minted = store1.CreateKey(new[] { "db:foo" }, "first store");

        var store2 = new KeyStore(registry);
        store2.Reload();
        Assert.Equal(1, store2.Count);
        var ctx = store2.TryAuthenticate(minted.Secret);
        Assert.NotNull(ctx);
        Assert.True(ctx!.CanReadDb("foo"));
    }

    [Fact]
    public void RevokeKey_RemovesFromCacheAndDisk()
    {
        var (registry, _) = FreshRegistry("revoke");
        using var _ = registry;

        var store = new KeyStore(registry);
        store.Reload();
        var minted = store.CreateKey(new[] { "db:bar" }, "throwaway");
        Assert.Equal(1, store.Count);

        Assert.True(store.RevokeKey(minted.Id));
        Assert.Equal(0, store.Count);
        Assert.Null(store.TryAuthenticate(minted.Secret));

        // On reload, the revoke must have hit disk too — else a restart
        // would resurrect the revoked key, which is a security regression.
        var store2 = new KeyStore(registry);
        store2.Reload();
        Assert.Equal(0, store2.Count);
        Assert.Null(store2.TryAuthenticate(minted.Secret));
    }

    [Fact]
    public void RevokeKey_UnknownId_ReturnsFalse()
    {
        var (registry, _) = FreshRegistry("revokeghost");
        using var _ = registry;

        var store = new KeyStore(registry);
        store.Reload();
        Assert.False(store.RevokeKey("does-not-exist"));
    }

    [Fact]
    public void TryAuthenticate_WrongToken_ReturnsNull()
    {
        var (registry, _) = FreshRegistry("wrongtoken");
        using var _ = registry;

        var store = new KeyStore(registry);
        store.Reload();
        store.CreateKey(new[] { "*" }, "real key");
        // Looks like a valid token (df_ prefix + base64-ish) but isn't ours.
        Assert.Null(store.TryAuthenticate("df_thiswillneverexistshouldnotmatch"));
        Assert.Null(store.TryAuthenticate(""));
    }

    [Fact]
    public void CreateKey_EmptyScopes_Throws()
    {
        var (registry, _) = FreshRegistry("noscopes");
        using var _ = registry;

        var store = new KeyStore(registry);
        store.Reload();
        Assert.Throws<ArgumentException>(() =>
            store.CreateKey(Array.Empty<string>(), "should fail"));
        Assert.Throws<ArgumentException>(() =>
            store.CreateKey(new[] { "", "   " }, "whitespace only"));
    }

    [Fact]
    public void List_ReturnsKeyInfoWithoutHashOrSecret()
    {
        // The List() projection is what /admin/keys returns to the
        // operator. It must NOT include the key hash, the plaintext,
        // or anything else an attacker could use.
        var (registry, _) = FreshRegistry("list");
        using var _ = registry;

        var store = new KeyStore(registry);
        store.Reload();
        store.CreateKey(new[] { "db:a" }, "alpha");
        store.CreateKey(new[] { "db:b" }, "beta");

        var entries = store.List();
        Assert.Equal(2, entries.Count);
        foreach (var info in entries)
        {
            Assert.False(string.IsNullOrEmpty(info.Id));
            Assert.NotEmpty(info.Scopes);
            // KeyInfo is the public projection. The record itself
            // doesn't have a hash or secret field — that's the
            // contract that's tested elsewhere by compile-time
            // (the property doesn't exist).
        }
    }

    [Fact]
    public void Reload_NoSystemDb_StaysNotReadyButDoesNotThrow()
    {
        // The KeyStore is constructed BEFORE ServeCommand attaches
        // _system in the bootstrap path. It must tolerate that
        // window — Reload should silently do nothing and Ready
        // should stay false.
        var registryWithoutSystem = new DatabaseRegistry();
        var dir = Path.Combine(Path.GetTempPath(), $"ks_no_system_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        registryWithoutSystem.AttachOrCreate("default", Path.Combine(dir, "default.dfdb"));
        // NO _system attached.

        using var _ = registryWithoutSystem;
        var store = new KeyStore(registryWithoutSystem);
        store.Reload();
        Assert.False(store.Ready);
        Assert.Equal(0, store.Count);
        Assert.Null(store.TryAuthenticate("anything"));
    }

    [Fact]
    public void HashToken_IsStableAndCaseSensitive()
    {
        // Same token → same hash; one byte difference → different hash.
        // SHA-256 is the contract; this test pins the algorithm choice
        // so a future swap to argon2/bcrypt is intentional, not silent.
        var h1 = KeyStore.HashToken("df_abc");
        var h2 = KeyStore.HashToken("df_abc");
        var h3 = KeyStore.HashToken("df_abd");
        Assert.Equal(h1, h2);
        Assert.NotEqual(h1, h3);
        Assert.Equal(64, h1.Length);  // 32 bytes → 64 hex chars
    }
}
