namespace DocumentForge.Studio.Core.Models;

/// <summary>A scoped API key as reported by the config API — description,
/// scopes, and a short fingerprint. The key itself never leaves the server.</summary>
public sealed record ServiceScopedKeyInfo(string? Description, IReadOnlyList<string> Scopes, string? KeyFingerprint);

/// <summary>The redacted effective node configuration from GET /admin/config
/// (engine #111). Secrets are reduced to presence + fingerprint. RestartRequired
/// and LiveEditable name the config fields in each class so the UI can label
/// them honestly.</summary>
public sealed record ServiceConfigInfo(
    // node
    string? NodeName,
    int Port,
    string? DataDir,
    bool BindAllInterfaces,
    bool InsecureDevMode,
    string? HttpEndpoint,
    // security (redacted)
    bool AdminKeyConfigured,
    string? AdminKeyFingerprint,
    bool ReplicationSecretConfigured,
    string? ReplicationSecretFingerprint,
    bool TlsConfigured,
    string? TlsCertPath,
    bool TlsCertPasswordConfigured,
    IReadOnlyList<ServiceScopedKeyInfo> ScopedKeys,
    // replication
    string? ReplicationRole,
    int? ReplicationPort,
    string? LeaderHost,
    int? LeaderPort,
    int MinSyncReplicas,
    double SyncTimeoutSeconds,
    // network
    string? PublicBaseUrl,
    IReadOnlyList<string> RestartRequired,
    IReadOnlyList<string> LiveEditable);
