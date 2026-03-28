using DocumentForge.Core;
using DocumentForge.Engine;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// Phase 1 of Issue #66 (multi-database hosting). Exercises the
/// <see cref="DatabaseRegistry"/> in isolation — no HTTP layer, no
/// service plumbing — so regressions point at the right component.
/// </summary>
public sealed class DatabaseRegistryTests
{
    private static string FreshDir(string label)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"reg_{label}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CleanupDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    [Fact]
    public void Create_NewDatabase_AppearsInListAndIsDefault()
    {
        var dir = FreshDir("create");
        try
        {
            using var registry = new DatabaseRegistry();
            var db = registry.Create("acme", Path.Combine(dir, "acme.dfdb"));

            Assert.NotNull(db);
            Assert.Equal("acme", registry.DefaultDatabaseName);

            var list = registry.List();
            Assert.Single(list);
            Assert.Equal("acme", list[0].Name);
            Assert.True(list[0].IsDefault);
            Assert.True(File.Exists(list[0].FilePath));
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void Attach_ExistingFile_OpensWithoutRecreating()
    {
        var dir = FreshDir("attach");
        var path = Path.Combine(dir, "existing.dfdb");
        try
        {
            // Phase 1: stand up a DB outside the registry so we have a real
            // file to attach. Insert a doc so the test can prove attach
            // opens the SAME file (data survives).
            using (var db = DocumentForgeDb.OpenOrCreate(path))
            {
                db.Insert("orders", """{"pnr":"X"}""");
            }

            using var registry = new DatabaseRegistry();
            var reattached = registry.Attach("acme", path);

            var rows = reattached.Execute("SELECT * FROM orders").Documents;
            Assert.Single(rows);
            Assert.Equal("X", rows[0]["pnr"].AsString);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void Attach_DuplicateName_Throws()
    {
        var dir = FreshDir("dupname");
        try
        {
            using var registry = new DatabaseRegistry();
            registry.Create("acme", Path.Combine(dir, "a.dfdb"));

            var ex = Assert.Throws<DocumentForgeException>(() =>
                registry.Create("acme", Path.Combine(dir, "b.dfdb")));
            Assert.Contains("already attached", ex.Message);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void Attach_SameFileUnderDifferentName_Throws()
    {
        // Two engine instances racing on the same lock file would corrupt
        // recovery state. The registry catches the duplicate path up front.
        var dir = FreshDir("dupfile");
        var path = Path.Combine(dir, "shared.dfdb");
        try
        {
            using (var bootstrap = DocumentForgeDb.OpenOrCreate(path)) { }

            using var registry = new DatabaseRegistry();
            registry.Attach("alpha", path);

            var ex = Assert.Throws<DocumentForgeException>(() =>
                registry.Attach("beta", path));
            Assert.Contains("already attached as database 'alpha'", ex.Message);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void Get_ResolvesIndependentInstances()
    {
        // Two attached DBs share no state. A write to one isn't visible
        // in the other.
        var dir = FreshDir("isolation");
        try
        {
            using var registry = new DatabaseRegistry();
            var a = registry.Create("a", Path.Combine(dir, "a.dfdb"));
            var b = registry.Create("b", Path.Combine(dir, "b.dfdb"));

            a.Insert("orders", """{"pnr":"A1"}""");

            Assert.Single(registry.Get("a").Execute("SELECT * FROM orders").Documents);
            Assert.Empty(registry.Get("b").Execute("SELECT * FROM orders").Documents);
            // First-attached implicitly becomes default.
            Assert.Equal("a", registry.DefaultDatabaseName);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void Get_CaseInsensitiveLookup()
    {
        var dir = FreshDir("case");
        try
        {
            using var registry = new DatabaseRegistry();
            registry.Create("Acme", Path.Combine(dir, "x.dfdb"));

            // All three should resolve to the same engine.
            Assert.NotNull(registry.Get("Acme"));
            Assert.NotNull(registry.Get("ACME"));
            Assert.NotNull(registry.Get("acme"));
            Assert.Same(registry.Get("Acme"), registry.Get("acme"));
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void Get_UnknownName_Throws()
    {
        using var registry = new DatabaseRegistry();
        var ex = Assert.Throws<DocumentForgeException>(() => registry.Get("does_not_exist"));
        Assert.Contains("not attached", ex.Message);
    }

    [Fact]
    public void TryGet_UnknownName_ReturnsNull()
    {
        using var registry = new DatabaseRegistry();
        Assert.Null(registry.TryGet("does_not_exist"));
    }

    [Fact]
    public void GetDefault_NoDatabases_Throws()
    {
        using var registry = new DatabaseRegistry();
        var ex = Assert.Throws<DocumentForgeException>(() => registry.GetDefault());
        Assert.Contains("No databases are attached", ex.Message);
    }

    [Fact]
    public void Detach_RemovesAndKeepsFile()
    {
        var dir = FreshDir("detach");
        var path = Path.Combine(dir, "kept.dfdb");
        try
        {
            using var registry = new DatabaseRegistry();
            registry.Create("acme", path);
            Assert.True(File.Exists(path));

            Assert.True(registry.Detach("acme"));
            Assert.Empty(registry.List());
            Assert.Null(registry.DefaultDatabaseName);
            Assert.True(File.Exists(path), "Detach must NOT delete the data file.");
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void Detach_Unknown_ReturnsFalse()
    {
        using var registry = new DatabaseRegistry();
        Assert.False(registry.Detach("ghost"));
    }

    [Fact]
    public void Detach_PromotesNextDefault()
    {
        // The default is sticky to a single named DB. When that DB is
        // detached, *some* other attached DB takes over as default so
        // flat routes still resolve. Order isn't contractually defined
        // but the property "default is non-null while any DB is attached"
        // matters — clients hitting /collections/... shouldn't 500.
        var dir = FreshDir("defaultpromote");
        try
        {
            using var registry = new DatabaseRegistry();
            registry.Create("a", Path.Combine(dir, "a.dfdb"));
            registry.Create("b", Path.Combine(dir, "b.dfdb"));
            Assert.Equal("a", registry.DefaultDatabaseName);

            Assert.True(registry.Detach("a"));
            Assert.Equal("b", registry.DefaultDatabaseName);

            Assert.True(registry.Detach("b"));
            Assert.Null(registry.DefaultDatabaseName);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void Drop_DeletesSidecarFiles()
    {
        var dir = FreshDir("drop");
        var path = Path.Combine(dir, "doomed.dfdb");
        try
        {
            using var registry = new DatabaseRegistry();
            var db = registry.Create("acme", path);
            // Force WAL/recovery to materialise so we have something to clean up.
            db.Insert("orders", """{"pnr":"X"}""");

            Assert.True(File.Exists(path));
            Assert.True(registry.Drop("acme"));

            // Primary file and every sidecar must be gone.
            Assert.False(File.Exists(path));
            foreach (var ext in new[] { ".wal", ".recovery", ".lock", ".followerseq" })
                Assert.False(File.Exists(path + ext),
                    $"Drop did not clean up sidecar '{path + ext}'.");
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void SetDefault_PointsFlatRoutesAtNamedDb()
    {
        var dir = FreshDir("setdefault");
        try
        {
            using var registry = new DatabaseRegistry();
            registry.Create("a", Path.Combine(dir, "a.dfdb"));
            registry.Create("b", Path.Combine(dir, "b.dfdb"));

            registry.SetDefault("b");
            Assert.Equal("b", registry.DefaultDatabaseName);
            Assert.Same(registry.Get("b"), registry.GetDefault());
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void SetDefault_UnknownName_Throws()
    {
        using var registry = new DatabaseRegistry();
        Assert.Throws<DocumentForgeException>(() => registry.SetDefault("ghost"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("1starts_with_digit")]
    [InlineData("_starts_with_underscore")]
    [InlineData("has space")]
    [InlineData("has.dot")]
    [InlineData("has/slash")]
    public void Create_InvalidName_Throws(string badName)
    {
        var dir = FreshDir("invalid");
        try
        {
            using var registry = new DatabaseRegistry();
            Assert.Throws<ArgumentException>(() =>
                registry.Create(badName, Path.Combine(dir, "x.dfdb")));
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void Create_NameTooLong_Throws()
    {
        var dir = FreshDir("toolong");
        try
        {
            using var registry = new DatabaseRegistry();
            var name = "a" + new string('b', 64);  // 65 chars
            Assert.Throws<ArgumentException>(() =>
                registry.Create(name, Path.Combine(dir, "x.dfdb")));
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void Create_FileAlreadyExists_Throws()
    {
        var dir = FreshDir("exists");
        var path = Path.Combine(dir, "occupied.dfdb");
        try
        {
            // Use OpenOrCreate from the engine to materialise a real file.
            using (var bootstrap = DocumentForgeDb.OpenOrCreate(path)) { }

            using var registry = new DatabaseRegistry();
            var ex = Assert.Throws<DocumentForgeException>(() =>
                registry.Create("acme", path));
            Assert.Contains("already exists", ex.Message);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void AttachOrCreate_NewFile_Creates()
    {
        // Mirrors the ServeCommand path: file may or may not exist. On a
        // first-run service start the file doesn't exist; this is the
        // verb that handles it.
        var dir = FreshDir("attachorcreate_new");
        var path = Path.Combine(dir, "fresh.dfdb");
        try
        {
            using var registry = new DatabaseRegistry();
            Assert.False(File.Exists(path));
            var db = registry.AttachOrCreate("default", path);
            Assert.True(File.Exists(path));
            db.Insert("orders", """{"pnr":"X"}""");
            Assert.Single(db.Execute("SELECT * FROM orders").Documents);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void AttachOrCreate_ExistingFile_Attaches()
    {
        var dir = FreshDir("attachorcreate_existing");
        var path = Path.Combine(dir, "preexisting.dfdb");
        try
        {
            using (var seed = DocumentForgeDb.OpenOrCreate(path))
            {
                seed.Insert("orders", """{"pnr":"PRESEEDED"}""");
            }

            using var registry = new DatabaseRegistry();
            var db = registry.AttachOrCreate("default", path);
            var rows = db.Execute("SELECT * FROM orders").Documents;
            Assert.Single(rows);
            Assert.Equal("PRESEEDED", rows[0]["pnr"].AsString);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void Dispose_DisposesAllAttachedDatabases()
    {
        // After Dispose, the registry rejects further calls and the engines
        // are released (file locks freed, so another Open succeeds).
        var dir = FreshDir("dispose");
        var path = Path.Combine(dir, "x.dfdb");
        try
        {
            var registry = new DatabaseRegistry();
            registry.Create("acme", path);
            registry.Dispose();

            Assert.Throws<ObjectDisposedException>(() => registry.Get("acme"));

            // The lock was released — reopening succeeds. Pre-dispose this
            // would throw "file is locked by another process".
            using var reopen = DocumentForgeDb.Open(path);
            Assert.NotNull(reopen);
        }
        finally { CleanupDir(dir); }
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        using var registry = new DatabaseRegistry();
        registry.Dispose();
        // Second dispose must not throw — common pattern in DI lifetimes.
        registry.Dispose();
    }
}
