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

    /// <summary>Per-database health + diagnostics (recommendation states, file
    /// sizes, lock holder).</summary>
    Task<DatabaseHealthReport> GetDatabaseHealthAsync(string database, CancellationToken ct = default);

    /// <summary>Replaces a document by its internal <c>_id</c>, guarded by its
    /// <c>_etag</c> (optimistic concurrency). Returns the new ETag. Throws
    /// <see cref="EtagConflictException"/> if the document changed since it was
    /// read, or <see cref="KeyNotFoundException"/> if it no longer exists.</summary>
    Task<string> UpdateDocumentAsync(string database, string collection, string id, string json, string expectedEtag, CancellationToken ct = default);

    /// <summary>Deletes a document by its internal <c>_id</c>.</summary>
    Task DeleteDocumentAsync(string database, string collection, string id, CancellationToken ct = default);

    /// <summary>Inserts a new document (raw JSON) into a collection, returning its
    /// assigned internal <c>_id</c>. The collection is created if it doesn't exist.</summary>
    Task<string> InsertDocumentAsync(string database, string collection, string json, CancellationToken ct = default);

    /// <summary>Drops a collection and its indexes. Returns false if it didn't exist.</summary>
    Task<bool> DropCollectionAsync(string database, string collection, CancellationToken ct = default);

    /// <summary>Defragments a collection, reclaiming space from deleted documents.</summary>
    Task<CompactionInfo> CompactCollectionAsync(string database, string collection, CancellationToken ct = default);

    // --- Replication (server connections only; requires ServerAdmin) ---

    Task<ReplicationStatus> GetReplicationStatusAsync(string database, CancellationToken ct = default);
    Task StartReplicationLeaderAsync(string database, int port, CancellationToken ct = default);
    Task StartReplicationFollowerAsync(string database, string leaderHost, int leaderPort, CancellationToken ct = default);
    Task PromoteReplicaAsync(string database, int port, CancellationToken ct = default);

    /// <summary>Requires <see cref="ConnectionCapabilities.CreateDatabase"/>.</summary>
    Task<DatabaseInfo> CreateDatabaseAsync(string name, CancellationToken ct = default);

    /// <summary>Requires <see cref="ConnectionCapabilities.DropDatabase"/>.
    /// With deleteFiles=false the database is only detached from the server.</summary>
    Task DropDatabaseAsync(string name, bool deleteFiles, CancellationToken ct = default);
}
