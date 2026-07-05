using System.Text.Json.Serialization;

namespace DocumentForge.Studio.Core.Connections;

public enum ConnectionKind
{
    /// <summary>Open a .dfdb file in-process via the embedded engine.</summary>
    File,

    /// <summary>Talk to a dfdb serve node (local service or remote endpoint) over HTTP.</summary>
    Http,
}

[Flags]
public enum ConnectionCapabilities
{
    None = 0,
    MultiDatabase = 1 << 0,
    CreateDatabase = 1 << 1,
    DropDatabase = 1 << 2,
    ServerAdmin = 1 << 3,
}

/// <summary>A saved connection as persisted in connections.json. The API key
/// itself never lives here — only a reference into the DPAPI secret store.</summary>
public sealed class ConnectionDescriptor
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public ConnectionKind Kind { get; set; }

    /// <summary>Path to the .dfdb file when <see cref="Kind"/> is File.</summary>
    public string? FilePath { get; set; }

    /// <summary>Base URL (http(s)://host:port) when <see cref="Kind"/> is Http.</summary>
    public string? Url { get; set; }

    /// <summary>Id of the Bearer key in the secret store, if the endpoint needs one.</summary>
    public string? ApiKeySecretId { get; set; }

    public DateTime? LastConnectedUtc { get; set; }

    [JsonIgnore]
    public string Target => Kind == ConnectionKind.File ? FilePath ?? "" : Url ?? "";
}
