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

    // --- Schemas / referential integrity (#106/#151) ---

    /// <summary>Every configured collection schema on a database — required
    /// fields, types, checks, and refs. Collections without a schema are
    /// simply absent.</summary>
    Task<IReadOnlyList<CollectionSchemaInfo>> GetSchemasAsync(string database, CancellationToken ct = default);

    /// <summary>Creates or replaces a collection's whole schema. The diagram
    /// designer round-trips the non-ref sections unchanged.</summary>
    Task PutSchemaAsync(string database, CollectionSchemaInfo schema, CancellationToken ct = default);

    /// <summary>Removes a collection's schema entirely (back to schemaless).
    /// No-op if none was configured.</summary>
    Task DeleteSchemaAsync(string database, string collection, CancellationToken ct = default);

    /// <summary>Defragments a collection, reclaiming space from deleted documents.</summary>
    Task<CompactionInfo> CompactCollectionAsync(string database, string collection, CancellationToken ct = default);

    // --- Backups (server connections only; requires ServerAdmin) ---

    Task<IReadOnlyList<BackupInfo>> GetBackupsAsync(CancellationToken ct = default);
    Task<BackupInfo> TakeBackupAsync(string database, CancellationToken ct = default);
    Task DeleteBackupAsync(string backupId, CancellationToken ct = default);
    /// <summary>Restores a backup as a new database. Returns the new database's file path.</summary>
    Task<string> RestoreBackupAsync(string backupId, string newDatabaseName, CancellationToken ct = default);

    // --- Backup admin: config, WAL archiving, PITR (server connections only) ---

    /// <summary>Backup settings (directory, retention, schedule).</summary>
    Task<BackupConfigInfo> GetBackupConfigAsync(CancellationToken ct = default);

    /// <summary>Saves backup settings. Null backupDir/scheduleCron clear the
    /// explicit setting back to the server default.</summary>
    Task SetBackupConfigAsync(string? backupDir, int retentionCount, string? scheduleCron, CancellationToken ct = default);

    /// <summary>Per-database WAL-archiving status.</summary>
    Task<ArchiveStatusInfo> GetArchiveStatusAsync(string database, CancellationToken ct = default);

    /// <summary>Turns continuous WAL archiving on/off for a database.</summary>
    Task SetArchiveEnabledAsync(string database, bool enabled, CancellationToken ct = default);

    /// <summary>Shipped WAL segments for a database, oldest first.</summary>
    Task<IReadOnlyList<ArchiveSegmentInfo>> GetArchiveSegmentsAsync(string database, CancellationToken ct = default);

    /// <summary>Dry-run a point-in-time restore: base backup + target time →
    /// feasibility, segments to replay, and any sequence gaps. No mutation.</summary>
    Task<PitrPreviewInfo> PreviewPitrRestoreAsync(string backupId, DateTime targetTimeUtc, CancellationToken ct = default);

    /// <summary>Executes a point-in-time restore into a NEW database.</summary>
    Task<PitrRestoreResult> RestorePitrAsync(string backupId, DateTime targetTimeUtc, string newDatabaseName, CancellationToken ct = default);

    // --- API keys (server connections only; requires ServerAdmin) ---

    Task<IReadOnlyList<ApiKeyInfo>> GetApiKeysAsync(CancellationToken ct = default);
    Task<CreatedApiKey> CreateApiKeyAsync(string? description, IReadOnlyList<string> scopes, CancellationToken ct = default);
    Task RevokeApiKeyAsync(string id, CancellationToken ct = default);

    // --- Managed child services (server connections only; requires ServerAdmin) ---

    /// <summary>Children spawned by this service's ServiceManager (GET /services).</summary>
    Task<IReadOnlyList<ManagedServiceEntry>> GetManagedServicesAsync(CancellationToken ct = default);

    /// <summary>Spawns a child dfdb serve process (POST /services). All fields
    /// optional: null port picks a free one, null apiKey inherits the parent's.
    /// Returns the child's endpoint so callers can connect to it.</summary>
    Task<SpawnedServiceInfo> SpawnServiceAsync(int? port, string? nodeName, string? dataDir, string? apiKey, CancellationToken ct = default);

    /// <summary>Stops and reaps a managed child (DELETE /services/{port}).</summary>
    Task StopManagedServiceAsync(int port, CancellationToken ct = default);

    /// <summary>Tails a managed child's combined stdout/stderr log.</summary>
    Task<string> GetManagedServiceLogAsync(int port, int maxBytes = 16384, CancellationToken ct = default);

    // --- Service settings (server connections only; requires ServerAdmin) ---

    /// <summary>The redacted effective node configuration (engine #111).
    /// Secrets come back as presence + fingerprint, never plaintext.</summary>
    Task<ServiceConfigInfo> GetServiceConfigAsync(CancellationToken ct = default);

    /// <summary>Applies the live-editable subset of the node configuration
    /// (currently the semi-sync replication knobs). Pass null to leave a field
    /// unchanged. Restart-required fields are rejected by the server. Returns
    /// the updated effective configuration.</summary>
    Task<ServiceConfigInfo> UpdateServiceConfigAsync(int? minSyncReplicas, double? syncTimeoutSeconds, CancellationToken ct = default);

    /// <summary>Asks the node to restart itself (POST /admin/restart): it flushes,
    /// acknowledges, and exits so its host (Windows service recovery / IIS) brings
    /// it back. Returns the server's acknowledgement message. The connection will
    /// be briefly unreachable afterwards — poll <see cref="GetHealthAsync"/>.</summary>
    Task<string> RestartServerAsync(CancellationToken ct = default);

    // --- Replication (server connections only; requires ServerAdmin) ---

    Task<ReplicationStatus> GetReplicationStatusAsync(string database, CancellationToken ct = default);
    Task StartReplicationLeaderAsync(string database, int port, CancellationToken ct = default);
    Task StartReplicationFollowerAsync(string database, string leaderHost, int leaderPort, CancellationToken ct = default);
    Task PromoteReplicaAsync(string database, int port, CancellationToken ct = default);

    /// <summary>Requires <see cref="ConnectionCapabilities.CreateDatabase"/>.</summary>
    Task<DatabaseInfo> CreateDatabaseAsync(string name, CancellationToken ct = default);

    /// <summary>Attaches an EXISTING .dfdb file to the server under the given
    /// name (its .wal/.recovery sidecars are replayed automatically by the
    /// engine on open). The path must be visible from the server's machine.
    /// Requires <see cref="ConnectionCapabilities.CreateDatabase"/>.</summary>
    Task<DatabaseInfo> AttachDatabaseAsync(string name, string filePath, CancellationToken ct = default);

    /// <summary>Writes the whole database as one plain-JSON document
    /// (<c>{ database, exportedAtUtc, collections: { name: [docs…] } }</c>)
    /// to <paramref name="destination"/> — the "explode to JSON" export.</summary>
    Task ExportDatabaseJsonAsync(string database, Stream destination, CancellationToken ct = default);

    /// <summary>Requires <see cref="ConnectionCapabilities.DropDatabase"/>.
    /// With deleteFiles=false the database is only detached from the server.</summary>
    Task DropDatabaseAsync(string name, bool deleteFiles, CancellationToken ct = default);
}
