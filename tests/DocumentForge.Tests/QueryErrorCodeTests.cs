using Xunit;
using DocumentForge.Engine;
using DocumentForge.Query;

namespace DocumentForge.Tests;

/// <summary>
/// Issue #69 — reads against a collection that has not been lazily created
/// yet fail with a STABLE machine-readable code ("collectionNotFound") so
/// clients can treat it as empty without string-matching the human message.
/// The error-vs-empty behaviour itself is deliberate (catches typos); the
/// code is the contract.
/// </summary>
public class QueryErrorCodeTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DocumentForgeDb _db;

    public QueryErrorCodeTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.dfdb");
        _db = DocumentForgeDb.Create(_dbPath);
    }

    [Fact]
    public void Select_UnknownCollection_CarriesCollectionNotFoundCode()
    {
        var r = _db.Execute("SELECT * FROM never_written");
        Assert.False(r.Success);
        Assert.Equal(QueryResult.Codes.CollectionNotFound, r.ErrorCode);
    }

    [Fact]
    public void Select_UnknownCollection_WithWhere_CarriesCode()
    {
        _db.Insert("orders", """{"pnr": "ABC"}""");
        var r = _db.Execute("SELECT * FROM widgets WHERE id = 'w-1'");
        Assert.False(r.Success);
        Assert.Equal(QueryResult.Codes.CollectionNotFound, r.ErrorCode);
    }

    [Fact]
    public void CreateIndex_UnknownCollection_CarriesCode()
    {
        var r = _db.Execute("CREATE INDEX idx_x ON widgets (id)");
        Assert.False(r.Success);
        Assert.Equal(QueryResult.Codes.CollectionNotFound, r.ErrorCode);
    }

    [Fact]
    public void ParseError_HasNoErrorCode()
    {
        var r = _db.Execute("SELECT FROM WHERE !!");
        Assert.False(r.Success);
        Assert.Null(r.ErrorCode);
    }

    [Fact]
    public void Select_ExistingCollection_HasNoErrorCode()
    {
        _db.Insert("orders", """{"pnr": "ABC"}""");
        var r = _db.Execute("SELECT * FROM orders");
        Assert.True(r.Success);
        Assert.Null(r.ErrorCode);
    }

    [Fact]
    public void UpdateAndDelete_UnknownCollection_StayNoOpSuccesses()
    {
        // UPDATE/DELETE on a missing collection are no-ops (0 affected),
        // consistent with SQL semantics on an empty table — they must not
        // grow an error code by accident.
        var upd = _db.Execute("UPDATE widgets SET x = 1");
        Assert.True(upd.Success);
        Assert.Equal(0, upd.AffectedCount);

        var del = _db.Execute("DELETE FROM widgets");
        Assert.True(del.Success);
        Assert.Equal(0, del.AffectedCount);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
