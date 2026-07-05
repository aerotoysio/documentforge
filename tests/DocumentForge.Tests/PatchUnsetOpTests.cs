using DocumentForge.Core;
using DocumentForge.Document;
using DocumentForge.Engine;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// Issue #70 — engine-level coverage for the Unset mutation op that PATCH's
/// merge semantics rely on (a null value in a merge body → remove the field).
/// Exercised directly through <see cref="DocumentForgeDb.TryConditionalUpdate"/>.
/// </summary>
public sealed class PatchUnsetOpTests
{
    private static (DocumentForgeDb db, string path) NewDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"patch_{Guid.NewGuid():N}.dfdb");
        return (DocumentForgeDb.Create(path), path);
    }

    private static void Cleanup(string path)
    {
        foreach (var ext in new[] { "", ".wal", ".recovery", ".lock", ".term" })
            try { File.Delete(path + ext); } catch { }
    }

    [Fact]
    public void Unset_RemovesField_AndIsIdempotent()
    {
        var (db, path) = NewDb();
        try
        {
            var id = db.Insert("t", """{"keep":1,"drop":"x"}""");

            var r1 = db.TryConditionalUpdate("t", id,
                Array.Empty<UpdateCondition>(),
                new[] { new UpdateOperation("drop", MutationOp.Unset, null) });
            Assert.True(r1.Succeeded);
            Assert.False(r1.Document!.ContainsKey("drop"));
            Assert.Equal(1, r1.Document["keep"].AsInt32);

            // Unsetting an already-absent field is a no-op success, not an error.
            var r2 = db.TryConditionalUpdate("t", id,
                Array.Empty<UpdateCondition>(),
                new[] { new UpdateOperation("drop", MutationOp.Unset, null) });
            Assert.True(r2.Succeeded);
            Assert.False(r2.Document!.ContainsKey("drop"));
        }
        finally { db.Dispose(); Cleanup(path); }
    }

    [Fact]
    public void SetAndUnset_InOneCall_AppliedTogether()
    {
        var (db, path) = NewDb();
        try
        {
            var id = db.Insert("t", """{"a":1,"b":2}""");
            var r = db.TryConditionalUpdate("t", id,
                Array.Empty<UpdateCondition>(),
                new[]
                {
                    new UpdateOperation("a", MutationOp.Set, BsonValue.FromInt32(99)),
                    new UpdateOperation("b", MutationOp.Unset, null),
                    new UpdateOperation("c", MutationOp.Set, BsonValue.FromString("new")),
                });
            Assert.True(r.Succeeded);
            Assert.Equal(99, r.Document!["a"].AsInt32);
            Assert.False(r.Document.ContainsKey("b"));
            Assert.Equal("new", r.Document["c"].AsString);
        }
        finally { db.Dispose(); Cleanup(path); }
    }

    [Fact]
    public void Unset_Id_Throws()
    {
        var (db, path) = NewDb();
        try
        {
            var id = db.Insert("t", """{"a":1}""");
            Assert.Throws<DocumentForgeException>(() => db.TryConditionalUpdate("t", id,
                Array.Empty<UpdateCondition>(),
                new[] { new UpdateOperation("_id", MutationOp.Unset, null) }));
        }
        finally { db.Dispose(); Cleanup(path); }
    }
}
