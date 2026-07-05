using Xunit;
using DocumentForge.Cli;
using DocumentForge.Cli.Commands;

namespace DocumentForge.Tests;

/// <summary>
/// Issue #101 — deny-by-default startup policy and dev-mode CORS scoping.
/// The server must refuse to start with no authentication configured
/// unless --insecure-dev-mode is passed, and must never bind all
/// interfaces without auth. Dev-mode CORS is pinned to loopback origins.
/// </summary>
public class ServeSecurityPolicyTests
{
    private static NodeConfig Config(
        string? apiKey = null,
        bool bindAll = false,
        bool insecureDevMode = false,
        List<ScopedApiKey>? scopedKeys = null)
    {
        var c = new NodeConfig { BindAllInterfaces = bindAll, InsecureDevMode = insecureDevMode };
        if (apiKey is not null || scopedKeys is not null)
            c.Security = new SecurityConfig { ApiKey = apiKey, ScopedKeys = scopedKeys };
        return c;
    }

    // ---- startup gate ----

    [Fact]
    public void NoAuth_NoOptIn_RefusesToStart()
    {
        var error = ServeCommand.ValidateStartupSecurity(Config(), persistedKeyCount: 0);
        Assert.NotNull(error);
        Assert.Contains("no authentication", error);
    }

    [Fact]
    public void NoAuth_WithInsecureDevMode_OnLoopback_Starts()
    {
        Assert.Null(ServeCommand.ValidateStartupSecurity(
            Config(insecureDevMode: true), persistedKeyCount: 0));
    }

    [Fact]
    public void NoAuth_BindAll_RefusesToStart_EvenWithInsecureDevMode()
    {
        var error = ServeCommand.ValidateStartupSecurity(
            Config(bindAll: true, insecureDevMode: true), persistedKeyCount: 0);
        Assert.NotNull(error);
        Assert.Contains("bind all interfaces", error);
    }

    [Fact]
    public void NoAuth_BindAll_RefusesToStart()
    {
        Assert.NotNull(ServeCommand.ValidateStartupSecurity(
            Config(bindAll: true), persistedKeyCount: 0));
    }

    [Fact]
    public void AdminKey_Starts()
    {
        Assert.Null(ServeCommand.ValidateStartupSecurity(
            Config(apiKey: "secret"), persistedKeyCount: 0));
    }

    [Fact]
    public void AdminKey_BindAll_Starts()
    {
        Assert.Null(ServeCommand.ValidateStartupSecurity(
            Config(apiKey: "secret", bindAll: true), persistedKeyCount: 0));
    }

    [Fact]
    public void ScopedKeysOnly_Starts()
    {
        var keys = new List<ScopedApiKey> { new() { Key = "k1", Scopes = new List<string> { "db:app" } } };
        Assert.Null(ServeCommand.ValidateStartupSecurity(
            Config(scopedKeys: keys), persistedKeyCount: 0));
    }

    [Fact]
    public void PersistedKeysOnly_Starts()
    {
        Assert.Null(ServeCommand.ValidateStartupSecurity(Config(), persistedKeyCount: 3));
    }

    // ---- config plumbing for the new flag ----

    [Fact]
    public void InsecureDevModeFlag_ParsedFromCli()
    {
        var c = NodeConfig.Load(new[] { "--insecure-dev-mode" });
        Assert.True(c.InsecureDevMode);
    }

    [Fact]
    public void InsecureDevMode_DefaultsToFalse()
    {
        var c = NodeConfig.Load(Array.Empty<string>());
        Assert.False(c.InsecureDevMode);
    }

    // ---- dev-mode CORS origin pinning ----

    [Theory]
    [InlineData("http://localhost:3000")]
    [InlineData("https://localhost")]
    [InlineData("http://LOCALHOST:8080")]
    [InlineData("http://127.0.0.1:5173")]
    [InlineData("http://[::1]:3000")]
    public void LoopbackOrigins_AreAllowedInDevMode(string origin)
    {
        Assert.True(ServeCommand.IsLoopbackOrigin(origin));
    }

    [Theory]
    [InlineData("https://evil.example.com")]
    [InlineData("http://192.168.1.10:3000")]
    [InlineData("http://localhost.evil.com")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("not-a-url")]
    public void NonLoopbackOrigins_AreRejectedInDevMode(string origin)
    {
        Assert.False(ServeCommand.IsLoopbackOrigin(origin));
    }
}
