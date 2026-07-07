namespace DocumentForge.Studio.Core.Models;

/// <summary>A child process managed by a service's ServiceManager, as reported
/// by GET /services. Kind is "serve" or "router".</summary>
public sealed record ManagedServiceEntry(
    int Port,
    string? NodeName,
    string BaseUrl,
    string DataDir,
    string? StartedAtUtc,
    bool Running,
    int? ExitCode,
    string Kind);

/// <summary>The result of POST /services — a freshly spawned child.</summary>
public sealed record SpawnedServiceInfo(
    int Port,
    string BaseUrl,
    string? NodeName,
    string DataDir,
    bool Ready);
