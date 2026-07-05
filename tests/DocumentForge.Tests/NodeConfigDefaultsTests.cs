using DocumentForge.Cli;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// Issue #110 — DocumentForge's standard default HTTP port is 4300. These lock
/// the convention so a future refactor can't silently drift it back to :5000.
/// </summary>
public sealed class NodeConfigDefaultsTests
{
    [Fact]
    public void DefaultPort_Is4300()
    {
        Assert.Equal(4300, NodeConfig.DefaultPort);
        Assert.Equal(4300, new NodeConfig().Port);
    }

    [Fact]
    public void Load_NoArgs_DefaultsToStandardPort()
    {
        var c = NodeConfig.Load(System.Array.Empty<string>());
        Assert.Equal(4300, c.Port);
    }

    [Fact]
    public void Load_PortFlag_OverridesDefault()
    {
        var c = NodeConfig.Load(new[] { "--port", "9999" });
        Assert.Equal(9999, c.Port);
    }

    [Fact]
    public void ResolveHttpEndpoint_UsesDefaultPort()
    {
        Assert.Equal("http://localhost:4300", new NodeConfig().ResolveHttpEndpoint());
    }
}
