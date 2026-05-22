using DocumentForge.Cli.Auth;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// Issue #72 — scope-parsing + permission tests for
/// <see cref="AuthContext"/>. The HTTP-level enforcement lives in
/// <c>AuthEnforcementHttpTests</c>; these are pure unit tests so a
/// failure points cleanly at the parsing/permission logic.
/// </summary>
public sealed class AuthContextTests
{
    [Fact]
    public void DevMode_GrantsEverything()
    {
        var auth = AuthContext.DevMode();
        Assert.True(auth.CanAdmin);
        Assert.True(auth.CanAccessAllDbs);
        Assert.True(auth.CanReadDb("anything"));
        Assert.True(auth.CanWriteDb("anything"));
    }

    [Fact]
    public void Anonymous_GrantsNothing()
    {
        var auth = AuthContext.Anonymous();
        Assert.False(auth.CanAdmin);
        Assert.False(auth.CanAccessAllDbs);
        Assert.False(auth.CanReadDb("any"));
        Assert.False(auth.CanWriteDb("any"));
    }

    [Fact]
    public void AdminScope_GrantsAllAccessLevels()
    {
        // The grand "*" scope means every database, every operation.
        var auth = AuthContext.FromScopes(new[] { "*" }, "admin");
        Assert.True(auth.CanAdmin);
        Assert.True(auth.CanAccessAllDbs);
        Assert.True(auth.CanReadDb("foo"));
        Assert.True(auth.CanWriteDb("bar"));
    }

    [Fact]
    public void DbStarScope_GrantsDataPlaneEveryDb_ButNotAdmin()
    {
        // "db:*" is for the metrics-reader / migration-runner case:
        // touch any database's data plane, but no /databases CRUD,
        // no /services, no /admin/*.
        var auth = AuthContext.FromScopes(new[] { "db:*" }, "data-svc");
        Assert.False(auth.CanAdmin);
        Assert.True(auth.CanAccessAllDbs);
        Assert.True(auth.CanReadDb("foo"));
        Assert.True(auth.CanWriteDb("bar"));
    }

    [Fact]
    public void DbScope_BoundedToNamedDb_ReadAndWrite()
    {
        // Plain "db:tenant_a" is full read+write on tenant_a only.
        var auth = AuthContext.FromScopes(new[] { "db:tenant_a" }, "tenant-a-key");
        Assert.False(auth.CanAdmin);
        Assert.False(auth.CanAccessAllDbs);
        Assert.True(auth.CanReadDb("tenant_a"));
        Assert.True(auth.CanWriteDb("tenant_a"));
        Assert.False(auth.CanReadDb("tenant_b"));
        Assert.False(auth.CanWriteDb("tenant_b"));
    }

    [Fact]
    public void DbReadScope_GrantsReadOnly()
    {
        var auth = AuthContext.FromScopes(new[] { "db:reports:read" }, "metrics");
        Assert.True(auth.CanReadDb("reports"));
        Assert.False(auth.CanWriteDb("reports"));
    }

    [Fact]
    public void MultipleScopes_Combine()
    {
        // Real-world key carrying mixed access: write on tenant_a,
        // read on lookup_global, no admin.
        var auth = AuthContext.FromScopes(
            new[] { "db:tenant_a", "db:lookup_global:read" }, "combined");
        Assert.False(auth.CanAdmin);
        Assert.True(auth.CanWriteDb("tenant_a"));
        Assert.True(auth.CanReadDb("lookup_global"));
        Assert.False(auth.CanWriteDb("lookup_global"));
        Assert.False(auth.CanReadDb("anything_else"));
    }

    [Fact]
    public void DbAccess_IsCaseInsensitive()
    {
        // Database names are case-insensitive (the registry uses
        // OrdinalIgnoreCase). Scope checks have to follow suit or
        // operators get confused why "Tenant_A" doesn't work on a
        // "tenant_a" scoped key.
        var auth = AuthContext.FromScopes(new[] { "db:tenant_a" }, "key");
        Assert.True(auth.CanReadDb("TENANT_A"));
        Assert.True(auth.CanReadDb("Tenant_A"));
    }

    [Fact]
    public void UnknownScopeStrings_AreIgnored_NotErrors()
    {
        // Forward-compat: future scope grammar additions ("op:flush",
        // "shard:ring-a") shouldn't blow up an older binary that
        // doesn't know about them. They simply grant nothing.
        var auth = AuthContext.FromScopes(new[] { "weird:thing", "db:foo" }, "k");
        Assert.True(auth.CanReadDb("foo"));
        Assert.False(auth.CanAdmin);
    }

    [Fact]
    public void EmptyAndWhitespaceScopes_AreIgnored()
    {
        var auth = AuthContext.FromScopes(new[] { "", "   ", "db:bar" }, "k");
        Assert.True(auth.CanReadDb("bar"));
        Assert.False(auth.CanAdmin);
    }

    [Fact]
    public void ConstantTimeEquals_Matches()
    {
        // Spot-checks for the timing-safe compare. Equal strings →
        // true; differing strings → false; null inputs → false.
        Assert.True(AuthContextExtensions.ConstantTimeEquals("abc", "abc"));
        Assert.False(AuthContextExtensions.ConstantTimeEquals("abc", "abd"));
        Assert.False(AuthContextExtensions.ConstantTimeEquals("abc", "ab"));
        Assert.False(AuthContextExtensions.ConstantTimeEquals(null!, "abc"));
        Assert.False(AuthContextExtensions.ConstantTimeEquals("abc", null!));
    }
}
