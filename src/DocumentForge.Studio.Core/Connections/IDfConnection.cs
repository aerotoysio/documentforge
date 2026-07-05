using DocumentForge.Studio.Core.Models;

namespace DocumentForge.Studio.Core.Connections;

/// <summary>
/// Transport-neutral view of a DocumentForge instance. Every Studio screen
/// works against this interface so a local .dfdb file, a local service and a
/// remote endpoint all behave identically; <see cref="Capabilities"/> tells
/// the UI which server-side features to grey out.
/// </summary>
public interface IDfConnection : IAsyncDisposable
{
    ConnectionDescriptor Descriptor { get; }
    ConnectionCapabilities Capabilities { get; }
    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken ct = default);

    Task<IReadOnlyList<DatabaseInfo>> GetDatabasesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetCollectionNamesAsync(string database, CancellationToken ct = default);
    Task<IReadOnlyList<IndexInfo>> GetIndexesAsync(string database, string collection, CancellationToken ct = default);
    Task<StudioQueryResult> ExecuteAsync(string database, string sql, CancellationToken ct = default);
    Task<DatabaseStats> GetStatsAsync(string database, CancellationToken ct = default);
    Task<ServerHealth> GetHealthAsync(CancellationToken ct = default);

    /// <summary>Requires <see cref="ConnectionCapabilities.CreateDatabase"/>.</summary>
    Task<DatabaseInfo> CreateDatabaseAsync(string name, CancellationToken ct = default);

    /// <summary>Requires <see cref="ConnectionCapabilities.DropDatabase"/>.
    /// With deleteFiles=false the database is only detached from the server.</summary>
    Task DropDatabaseAsync(string name, bool deleteFiles, CancellationToken ct = default);
}
