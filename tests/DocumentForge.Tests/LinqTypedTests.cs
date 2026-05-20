using DocumentForge.Engine;
using Xunit;

namespace DocumentForge.Tests;

// Regression tests for the typed-LINQ surface.
//   #62 — typed Insert serialised PascalCase while Where/Read used camelCase,
//         so typed inserts could never be found by a typed Where.
//   #63 — FormatLiteral emitted Guids unquoted, so Where(x => x.Id == guid)
//         parsed the RHS as an identifier and matched nothing.
public sealed class LinqTypedTests : IDisposable
{
    private readonly List<string> _paths = new();

    private DocumentForgeDb NewDb()
    {
        var p = Path.Combine(Path.GetTempPath(), $"linq_{Guid.NewGuid():N}.dfdb");
        _paths.Add(p);
        return DocumentForgeDb.OpenOrCreate(p);
    }

    public void Dispose()
    {
        foreach (var p in _paths)
            foreach (var f in new[] { p, p + ".wal", p + ".recovery", p + ".followerseq" })
                try { if (File.Exists(f)) File.Delete(f); } catch { /* best effort */ }
    }

    public sealed class User
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Email { get; set; } = "";
    }

    [Fact] // #62
    public void TypedInsert_IsFoundByTypedWhere()
    {
        using var db = NewDb();
        db.Collection<User>("users").Insert(new User { Email = "x@example.com" });

        var found = db.Collection<User>("users").Where(x => x.Email == "x@example.com").FirstOrDefault();

        Assert.NotNull(found);
        Assert.Equal("x@example.com", found!.Email);

        // Stored at the camelCase path, so raw SQL and indexes hit it too.
        Assert.Single(db.Execute("SELECT * FROM users WHERE email = 'x@example.com'").Documents);
    }

    [Fact] // #63
    public void TypedWhere_ByGuid_Roundtrips()
    {
        using var db = NewDb();
        var u = new User { Email = "g@example.com" };
        db.Collection<User>("users").Insert(u);

        var byId = db.Collection<User>("users").Where(x => x.Id == u.Id).FirstOrDefault();

        Assert.NotNull(byId);
        Assert.Equal(u.Id, byId!.Id);
        Assert.Equal("g@example.com", byId.Email);
    }
}
