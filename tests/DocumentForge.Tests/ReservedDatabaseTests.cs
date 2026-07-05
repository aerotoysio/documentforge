using DocumentForge.Cli.Auth;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// Issue #105 — the '_'-prefixed system databases (e.g. _system, the API-key
/// hash store) must be unreachable through the document data plane. The block
/// lives in ScopeCheck and is unconditional: no scope, not even admin, grants
/// data-plane access.
/// </summary>
public sealed class ReservedDatabaseTests
{
    [Theory]
    [InlineData("_system")]
    [InlineData("_")]
    [InlineData("_keys")]
    [InlineData("_anything")]
    public void ReservedNames_AreDetected(string name)
    {
        Assert.True(ScopeCheck.IsReservedDatabase(name));
    }

    [Theory]
    [InlineData("system")]
    [InlineData("orders")]
    [InlineData("default")]
    [InlineData("a_b")]   // underscore not at the START is fine
    [InlineData("")]
    public void NonReservedNames_AreAllowed(string name)
    {
        Assert.False(ScopeCheck.IsReservedDatabase(name));
    }

    [Fact]
    public void DenyIfReserved_Returns403_ForReserved()
    {
        var result = ScopeCheck.DenyIfReserved("_system");
        Assert.NotNull(result);
        Assert.Equal(403, StatusOf(result!));
    }

    [Fact]
    public void DenyIfReserved_ReturnsNull_ForNormal()
    {
        Assert.Null(ScopeCheck.DenyIfReserved("orders"));
    }

    [Fact]
    public void RequireDbRead_BlocksReserved_EvenWithAdminAllDbScope()
    {
        // An admin/all-DB context can read every REAL database, but a reserved
        // one is still refused — the block short-circuits before the scope test.
        var ctx = ContextWith(AuthContext.DevMode()); // dev mode = admin + all DBs
        Assert.Null(ScopeCheck.RequireDbRead(ctx, "orders"));           // normal DB allowed
        var denied = ScopeCheck.RequireDbRead(ctx, "_system");
        Assert.NotNull(denied);
        Assert.Equal(403, StatusOf(denied!));
    }

    [Fact]
    public void RequireDbWrite_BlocksReserved_EvenWithAdminAllDbScope()
    {
        var ctx = ContextWith(AuthContext.DevMode());
        Assert.Null(ScopeCheck.RequireDbWrite(ctx, "orders"));
        Assert.NotNull(ScopeCheck.RequireDbWrite(ctx, "_system"));
    }

    private static HttpContext ContextWith(AuthContext auth)
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[AuthContext.ContextKey] = auth;
        return ctx;
    }

    private static int StatusOf(IResult result)
    {
        // Results.Json(..., statusCode: 403) surfaces its status via the
        // StatusCode property on the concrete result type.
        var prop = result.GetType().GetProperty("StatusCode");
        return prop?.GetValue(result) is int s ? s : 0;
    }
}
