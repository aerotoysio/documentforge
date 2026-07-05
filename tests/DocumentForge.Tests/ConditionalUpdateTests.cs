using DocumentForge.Core;
using DocumentForge.Document;
using DocumentForge.Engine;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// Issue #103 — the atomic conditional-update / compare-and-set primitive.
/// Covers the reservation double-booking scenario (two racers can't both take
/// the last seat), CAS via _etag, guard semantics, and the numeric mutations.
/// </summary>
public sealed class ConditionalUpdateTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DocumentForgeDb _db;

    public ConditionalUpdateTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cond_{Guid.NewGuid():N}.dfdb");
        _db = DocumentForgeDb.Create(_dbPath);
    }

    private static UpdateCondition Cond(string field, ConditionOp op, BsonValue? v = null) => new(field, op, v);
    private static UpdateOperation Op(string field, MutationOp op, BsonValue? v = null) => new(field, op, v);

    [Fact]
    public void DecrementIfAvailable_Succeeds_WhenSeatsRemain()
    {
        var id = _db.Insert("inventory", """{"sku":"A","seats":3}""");
        var r = _db.TryConditionalUpdate("inventory", id,
            new[] { Cond("seats", ConditionOp.Gte, BsonValue.FromInt32(1)) },
            new[] { Op("seats", MutationOp.Dec, BsonValue.FromInt32(1)) });

        Assert.True(r.Succeeded);
        Assert.Equal(2, _db.GetCollection("inventory")!.FindById(id)!["seats"].AsInt32);
        Assert.False(string.IsNullOrEmpty(r.Etag)); // a fresh CAS token was stamped
    }

    [Fact]
    public void DecrementIfAvailable_Fails_WhenSoldOut()
    {
        var id = _db.Insert("inventory", """{"sku":"A","seats":0}""");
        var r = _db.TryConditionalUpdate("inventory", id,
            new[] { Cond("seats", ConditionOp.Gte, BsonValue.FromInt32(1)) },
            new[] { Op("seats", MutationOp.Dec, BsonValue.FromInt32(1)) });

        Assert.Equal(ConditionalUpdateStatus.ConditionFailed, r.Status);
        Assert.Equal(0, _db.GetCollection("inventory")!.FindById(id)!["seats"].AsInt32); // untouched
    }

    [Fact]
    public void LastSeat_UnderConcurrency_GoesToExactlyOneWinner()
    {
        // The headline reservation guarantee: seats=1, N threads each try to
        // decrement-if-available. Exactly one must succeed; the rest see a
        // condition failure; the seat count never goes negative.
        var id = _db.Insert("inventory", """{"sku":"LASTSEAT","seats":1}""");

        const int racers = 32;
        int successes = 0;
        using var start = new Barrier(racers);
        var threads = new List<Thread>();
        for (int i = 0; i < racers; i++)
        {
            var t = new Thread(() =>
            {
                start.SignalAndWait();
                var r = _db.TryConditionalUpdate("inventory", id,
                    new[] { Cond("seats", ConditionOp.Gte, BsonValue.FromInt32(1)) },
                    new[] { Op("seats", MutationOp.Dec, BsonValue.FromInt32(1)) });
                if (r.Succeeded) Interlocked.Increment(ref successes);
            });
            threads.Add(t);
            t.Start();
        }
        foreach (var t in threads) t.Join();

        Assert.Equal(1, successes);
        Assert.Equal(0, _db.GetCollection("inventory")!.FindById(id)!["seats"].AsInt32);
    }

    [Fact]
    public void Cas_OnEtag_OnlyAppliesForTheMatchingPreImage()
    {
        var id = _db.Insert("orders", """{"pnr":"X","status":"held"}""");
        var etag = _db.GetCollection("orders")!.FindById(id)!.GetEtag();

        // Correct etag → applies.
        var ok = _db.TryConditionalUpdate("orders", id,
            new[] { Cond("_etag", ConditionOp.Eq, BsonValue.FromString(etag)) },
            new[] { Op("status", MutationOp.Set, BsonValue.FromString("booked")) });
        Assert.True(ok.Succeeded);
        Assert.Equal("booked", _db.GetCollection("orders")!.FindById(id)!["status"].AsString);

        // Stale etag (the one we captured, now changed) → conflict, no write.
        var stale = _db.TryConditionalUpdate("orders", id,
            new[] { Cond("_etag", ConditionOp.Eq, BsonValue.FromString(etag)) },
            new[] { Op("status", MutationOp.Set, BsonValue.FromString("cancelled")) });
        Assert.Equal(ConditionalUpdateStatus.ConditionFailed, stale.Status);
        Assert.Equal("booked", _db.GetCollection("orders")!.FindById(id)!["status"].AsString);
    }

    [Fact]
    public void MissingDocument_ReturnsNotFound()
    {
        var r = _db.TryConditionalUpdate("orders", DocumentId.NewId(),
            Array.Empty<UpdateCondition>(),
            new[] { Op("x", MutationOp.Set, BsonValue.FromInt32(1)) });
        Assert.Equal(ConditionalUpdateStatus.NotFound, r.Status);
    }

    [Fact]
    public void GteOnAbsentField_FailsSafely_DoesNotCreateNegative()
    {
        // Guard against decrementing a field that isn't there.
        var id = _db.Insert("inventory", """{"sku":"NOSEATS"}""");
        var r = _db.TryConditionalUpdate("inventory", id,
            new[] { Cond("seats", ConditionOp.Gte, BsonValue.FromInt32(1)) },
            new[] { Op("seats", MutationOp.Dec, BsonValue.FromInt32(1)) });
        Assert.Equal(ConditionalUpdateStatus.ConditionFailed, r.Status);
        Assert.False(_db.GetCollection("inventory")!.FindById(id)!.ContainsKey("seats"));
    }

    [Fact]
    public void MultipleOps_AllApplyAtomically()
    {
        var id = _db.Insert("inventory", """{"seats":5,"version":1,"status":"open"}""");
        var r = _db.TryConditionalUpdate("inventory", id,
            new[] { Cond("seats", ConditionOp.Gte, BsonValue.FromInt32(1)) },
            new[]
            {
                Op("seats", MutationOp.Dec, BsonValue.FromInt32(1)),
                Op("version", MutationOp.Inc, BsonValue.FromInt32(1)),
                Op("status", MutationOp.Set, BsonValue.FromString("booked")),
            });
        Assert.True(r.Succeeded);
        var doc = _db.GetCollection("inventory")!.FindById(id)!;
        Assert.Equal(4, doc["seats"].AsInt32);
        Assert.Equal(2, doc["version"].AsInt32);
        Assert.Equal("booked", doc["status"].AsString);
    }

    [Fact]
    public void SqlUpdate_RefreshesEtag_Issue103()
    {
        var id = _db.Insert("orders", """{"pnr":"E","status":"held"}""");
        var before = _db.GetCollection("orders")!.FindById(id)!.GetEtag();

        var r = _db.Execute("UPDATE orders SET status = 'booked'");
        Assert.True(r.Success);

        var after = _db.GetCollection("orders")!.FindById(id)!.GetEtag();
        Assert.NotEqual(before, after); // etag re-stamped so stale If-Match is caught
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var ext in new[] { "", ".wal", ".recovery", ".lock" })
            try { File.Delete(_dbPath + ext); } catch { }
    }
}
