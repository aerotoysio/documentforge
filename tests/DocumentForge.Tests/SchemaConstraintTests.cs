using DocumentForge.Core;
using DocumentForge.Document;
using DocumentForge.Engine;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// Issue #106 — opt-in per-collection schema / constraints. Required fields,
/// field types, and CHECK conditions are enforced before every write path
/// (Insert, bulk, Replace, conditional-update, transaction, and SQL
/// INSERT/UPDATE); a collection with no schema stays schemaless. Config is
/// persisted and reloaded on restart.
/// </summary>
public sealed class SchemaConstraintTests : IDisposable
{
    private readonly string _dbPath;
    private DocumentForgeDb _db;

    public SchemaConstraintTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"schema_{Guid.NewGuid():N}.dfdb");
        _db = DocumentForgeDb.Create(_dbPath);
    }

    private static CollectionSchema Schema(
        string collection,
        string[]? required = null,
        (string field, FieldTypeConstraint type)[]? types = null,
        UpdateCondition[]? checks = null) =>
        new(collection,
            required ?? Array.Empty<string>(),
            (types ?? Array.Empty<(string, FieldTypeConstraint)>()).ToDictionary(t => t.field, t => t.type),
            checks ?? Array.Empty<UpdateCondition>());

    [Fact]
    public void SchemalessByDefault_AnyDocInserts()
    {
        // The audit's baseline: no schema means {} still lands.
        var id = _db.Insert("orders", "{}");
        Assert.NotNull(_db.GetCollection("orders")!.FindById(id));
    }

    [Fact]
    public void RequiredField_RejectsMissing_AcceptsPresent()
    {
        _db.ConfigureSchema(Schema("orders", required: new[] { "pnr" }));

        Assert.Throws<SchemaViolationException>(() => _db.Insert("orders", """{"passenger":"A"}"""));
        var id = _db.Insert("orders", """{"pnr":"ABC","passenger":"A"}""");
        Assert.NotNull(_db.GetCollection("orders")!.FindById(id));
    }

    [Fact]
    public void TypeConstraint_RejectsWrongType()
    {
        _db.ConfigureSchema(Schema("orders", types: new[] { ("pnr", FieldTypeConstraint.String) }));

        Assert.Throws<SchemaViolationException>(() => _db.Insert("orders", """{"pnr":123}"""));
        var id = _db.Insert("orders", """{"pnr":"ABC"}""");
        Assert.NotNull(_db.GetCollection("orders")!.FindById(id));
    }

    [Fact]
    public void CheckConstraint_EnforcesInvariant()
    {
        _db.ConfigureSchema(Schema("inventory",
            checks: new[] { new UpdateCondition("seatCount", ConditionOp.Gte, BsonValue.FromInt32(0)) }));

        Assert.Throws<SchemaViolationException>(() => _db.Insert("inventory", """{"seatCount":-1}"""));
        var id = _db.Insert("inventory", """{"seatCount":5}""");
        Assert.NotNull(_db.GetCollection("inventory")!.FindById(id));
    }

    [Fact]
    public void Schema_EnforcedOnReplace()
    {
        _db.ConfigureSchema(Schema("orders", required: new[] { "pnr" }));
        var id = _db.Insert("orders", """{"pnr":"ABC"}""");
        // A replace that drops the required field must be rejected.
        Assert.Throws<SchemaViolationException>(() => _db.Replace("orders", id, """{"passenger":"A"}"""));
        Assert.Equal("ABC", _db.GetCollection("orders")!.FindById(id)!["pnr"].AsString);
    }

    [Fact]
    public void Schema_EnforcedOnSqlInsert_AndSqlUpdate()
    {
        _db.ConfigureSchema(Schema("inventory",
            checks: new[] { new UpdateCondition("seatCount", ConditionOp.Gte, BsonValue.FromInt32(0)) }));

        var bad = _db.Execute("""INSERT INTO inventory {"seatCount": -3}""");
        Assert.False(bad.Success); // SQL insert blocked by CHECK

        _db.Insert("inventory", """{"sku":"A","seatCount":1}""");
        var badUpdate = _db.Execute("UPDATE inventory SET seatCount = -5");
        Assert.False(badUpdate.Success); // SQL update blocked by CHECK
        Assert.Equal(1, _db.GetCollection("inventory")!.FindAll().First()["seatCount"].AsInt32);
    }

    [Fact]
    public void Schema_EnforcedInTransaction_RollsBackWholeBatch()
    {
        _db.ConfigureSchema(Schema("orders", required: new[] { "pnr" }));
        using var tx = _db.BeginTransaction();
        tx.Insert("orders", BsonDocument.FromJson("""{"pnr":"OK"}"""));
        tx.Insert("orders", BsonDocument.FromJson("""{"passenger":"no pnr"}""")); // violates schema
        Assert.Throws<SchemaViolationException>(() => tx.Commit());
        // Whole transaction rejected before any storage mutation → neither doc lands.
        Assert.Empty(_db.Execute("SELECT * FROM orders").Documents);
    }

    [Fact]
    public void ExistingDocs_NotRevalidated_OnConfigure()
    {
        var id = _db.Insert("orders", """{"passenger":"A"}"""); // no pnr
        _db.ConfigureSchema(Schema("orders", required: new[] { "pnr" }));
        // Configuring a schema doesn't retroactively delete/invalidate the doc.
        Assert.NotNull(_db.GetCollection("orders")!.FindById(id));
    }

    [Fact]
    public void Schema_SurvivesRestart()
    {
        _db.ConfigureSchema(Schema("orders",
            required: new[] { "pnr" },
            types: new[] { ("pnr", FieldTypeConstraint.String) },
            checks: new[] { new UpdateCondition("fare", ConditionOp.Gte, BsonValue.FromInt32(0)) }));
        _db.Dispose();

        _db = DocumentForgeDb.Open(_dbPath);
        var s = _db.GetSchema("orders");
        Assert.NotNull(s);
        Assert.Contains("pnr", s!.Required);
        Assert.Equal(FieldTypeConstraint.String, s.Types["pnr"]);
        Assert.Single(s.Checks);
        // Still enforced after reload.
        Assert.Throws<SchemaViolationException>(() => _db.Insert("orders", """{"pnr":"X","fare":-1}"""));
    }

    [Fact]
    public void RemoveSchema_RestoresSchemaless()
    {
        _db.ConfigureSchema(Schema("orders", required: new[] { "pnr" }));
        Assert.True(_db.RemoveSchema("orders"));
        Assert.Null(_db.GetSchema("orders"));
        var id = _db.Insert("orders", "{}"); // schemaless again
        Assert.NotNull(_db.GetCollection("orders")!.FindById(id));
    }

    [Fact]
    public void SchemaMetaCollection_Hidden_AndCannotBeSchemaTargeted()
    {
        _db.ConfigureSchema(Schema("orders", required: new[] { "pnr" }));
        Assert.DoesNotContain("__schema_meta", _db.GetCollectionNames());
        Assert.Throws<ArgumentException>(() => _db.ConfigureSchema(Schema("__schema_meta", required: new[] { "x" })));
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        foreach (var ext in new[] { "", ".wal", ".recovery", ".lock" })
            try { File.Delete(_dbPath + ext); } catch { }
    }
}
