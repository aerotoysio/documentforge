using DocumentForge.Core;
using DocumentForge.Document;
using DocumentForge.Engine;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// Issue #104 — engine-side TTL / expiry. Documents whose expiry field is at or
/// before now are removed by the background sweep (or an on-demand sweep),
/// under the write lock so it's race-safe with concurrent updates. Config is
/// persisted and reloaded on restart.
/// </summary>
public sealed class TtlExpiryTests : IDisposable
{
    private readonly string _dbPath;
    private DocumentForgeDb _db;

    public TtlExpiryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"ttl_{Guid.NewGuid():N}.dfdb");
        _db = DocumentForgeDb.Create(_dbPath);
    }

    private static string IsoDaysFromNow(double days) =>
        DateTimeOffset.UtcNow.AddDays(days).ToString("O");

    [Fact]
    public void SweepExpired_DeletesPastHolds_KeepsFutureHolds()
    {
        _db.ConfigureTtl("holds", "expiry", TimeSpan.FromSeconds(1));
        var expired1 = _db.Insert("holds", $$"""{"seat":"1A","expiry":"{{IsoDaysFromNow(-1)}}"}""");
        var expired2 = _db.Insert("holds", $$"""{"seat":"1B","expiry":"{{IsoDaysFromNow(-0.001)}}"}""");
        var future = _db.Insert("holds", $$"""{"seat":"2A","expiry":"{{IsoDaysFromNow(1)}}"}""");
        var noExpiry = _db.Insert("holds", """{"seat":"3A"}""");

        var deleted = _db.SweepExpired();

        Assert.Equal(2, deleted);
        var coll = _db.GetCollection("holds")!;
        Assert.Null(coll.FindById(expired1));
        Assert.Null(coll.FindById(expired2));
        Assert.NotNull(coll.FindById(future));   // not yet expired
        Assert.NotNull(coll.FindById(noExpiry)); // no expiry field → never swept
    }

    [Fact]
    public void SweepExpired_SupportsUnixEpochMillis()
    {
        _db.ConfigureTtl("holds", "expiresAtMs");
        var past = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds();
        var future = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds();
        var expired = _db.Insert("holds", $$"""{"seat":"1A","expiresAtMs":{{past}}}""");
        var alive = _db.Insert("holds", $$"""{"seat":"2A","expiresAtMs":{{future}}}""");

        Assert.Equal(1, _db.SweepExpired());
        Assert.Null(_db.GetCollection("holds")!.FindById(expired));
        Assert.NotNull(_db.GetCollection("holds")!.FindById(alive));
    }

    [Fact]
    public void BackgroundSweeper_RemovesExpired_WithoutManualCall()
    {
        _db.ConfigureTtl("holds", "expiry", TimeSpan.FromSeconds(1));
        var id = _db.Insert("holds", $$"""{"seat":"1A","expiry":"{{IsoDaysFromNow(-1)}}"}""");

        // The timer fires ~every second; give it a couple cycles.
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < deadline && _db.GetCollection("holds")!.FindById(id) is not null)
            Thread.Sleep(200);

        Assert.Null(_db.GetCollection("holds")!.FindById(id));
    }

    [Fact]
    public void TtlConfig_SurvivesRestart_AndStillSweeps()
    {
        _db.ConfigureTtl("holds", "expiry", TimeSpan.FromSeconds(1));
        Assert.Single(_db.GetTtlConfigs());
        _db.Dispose();

        // Reopen: the persisted config must reload.
        _db = DocumentForgeDb.Open(_dbPath);
        var configs = _db.GetTtlConfigs();
        Assert.Single(configs);
        Assert.Equal("holds", configs[0].Collection);
        Assert.Equal("expiry", configs[0].Field);

        // And it still functions after restart.
        var expired = _db.Insert("holds", $$"""{"seat":"1A","expiry":"{{IsoDaysFromNow(-1)}}"}""");
        Assert.Equal(1, _db.SweepExpired());
        Assert.Null(_db.GetCollection("holds")!.FindById(expired));
    }

    [Fact]
    public void RemoveTtl_StopsSweeping()
    {
        _db.ConfigureTtl("holds", "expiry");
        Assert.True(_db.RemoveTtl("holds"));
        Assert.Empty(_db.GetTtlConfigs());

        var id = _db.Insert("holds", $$"""{"seat":"1A","expiry":"{{IsoDaysFromNow(-1)}}"}""");
        Assert.Equal(0, _db.SweepExpired()); // no rule → nothing swept
        Assert.NotNull(_db.GetCollection("holds")!.FindById(id));
    }

    [Fact]
    public void TtlMetaCollection_IsHiddenFromListings()
    {
        _db.ConfigureTtl("holds", "expiry");
        _db.Insert("holds", """{"seat":"1A"}""");
        Assert.DoesNotContain("__ttl_meta", _db.GetCollectionNames());
        Assert.Contains("holds", _db.GetCollectionNames());
    }

    [Fact]
    public void Sweep_ReconfigureOverwritesField()
    {
        _db.ConfigureTtl("holds", "oldField");
        _db.ConfigureTtl("holds", "expiry"); // replace
        var configs = _db.GetTtlConfigs();
        Assert.Single(configs);
        Assert.Equal("expiry", configs[0].Field);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        foreach (var ext in new[] { "", ".wal", ".recovery", ".lock" })
            try { File.Delete(_dbPath + ext); } catch { }
    }
}
