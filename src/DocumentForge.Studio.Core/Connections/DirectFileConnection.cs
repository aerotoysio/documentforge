using DocumentForge.Core;
using DocumentForge.Engine;
using DocumentForge.Studio.Core.Models;
using DatabaseInfo = DocumentForge.Studio.Core.Models.DatabaseInfo;

namespace DocumentForge.Studio.Core.Connections;

/// <summary>
/// Opens a .dfdb file in-process via the embedded engine. Studio takes the
/// OS-level single-writer lock like any other embedder, so this mode is full
/// read/write — but fails with <see cref="DatabaseLockedException"/> (which
/// names the holder pid/host) when a service already owns the file.
/// The engine is synchronous; calls are marshalled off the UI thread here.
/// </summary>
public sealed class DirectFileConnection : IDfConnection
{
    private DocumentForgeDb? _db;
    private readonly string _databaseName;

    public DirectFileConnection(ConnectionDescriptor descriptor)
    {
        if (descriptor.Kind != ConnectionKind.File || string.IsNullOrWhiteSpace(descriptor.FilePath))
            throw new ArgumentException("Descriptor must be a File connection with a FilePath.", nameof(descriptor));
        Descriptor = descriptor;
        _databaseName = Path.GetFileNameWithoutExtension(descriptor.FilePath);
    }

    public ConnectionDescriptor Descriptor { get; }

    // A single file has no server-side registry, keys, or replication control.
    public ConnectionCapabilities Capabilities => ConnectionCapabilities.None;

    public bool IsConnected => _db is not null;

    public Task ConnectAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        if (_db is not null) return;
        var path = Descriptor.FilePath!;
        if (!File.Exists(path))
            throw new FileNotFoundException($"Database file not found: {path}", path);
        _db = DocumentForgeDb.Open(path);
    }, ct);

    /// <summary>Creates a new empty .dfdb file and immediately releases it.
    /// Used by the "New Database → local file" flow before connecting.</summary>
    public static void CreateDatabaseFile(string filePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        using var db = DocumentForgeDb.Create(filePath);
    }

    public Task<IReadOnlyList<DatabaseInfo>> GetDatabasesAsync(CancellationToken ct = default)
    {
        EnsureConnected();
        IReadOnlyList<DatabaseInfo> one = [new DatabaseInfo(_databaseName, Descriptor.FilePath, IsDefault: true)];
        return Task.FromResult(one);
    }

    public Task<IReadOnlyList<string>> GetCollectionNamesAsync(string database, CancellationToken ct = default) =>
        Task.Run(() => EnsureConnected().GetCollectionNames(), ct);

    public Task<IReadOnlyList<IndexInfo>> GetIndexesAsync(string database, string collection, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<IndexInfo>>(() =>
            EnsureConnected().GetIndexes(collection)
                .Select(i => new IndexInfo(i.Name, i.JsonPath, i.IsUnique, EntryCount: -1))
                .ToList(), ct);

    public Task<StudioQueryResult> ExecuteAsync(string database, string sql, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var result = EnsureConnected().Execute(sql);
            return new StudioQueryResult(
                result.Success,
                result.Documents.Select(d => d.ToJson()).ToList(),
                result.AffectedCount,
                result.QueryPlan,
                result.ExecutionTime.TotalMilliseconds,
                result.Message);
        }, ct);

    public Task<DatabaseStats> GetStatsAsync(string database, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var s = EnsureConnected().GetStatistics();
            return new DatabaseStats(
                s.FileSize,
                s.PageCount,
                s.CachedPages,
                s.DirtyPages,
                s.Collections.Select(c => new CollectionStats(c.Name, c.DocumentCount, c.IndexCount)).ToList());
        }, ct);

    public Task<ServerHealth> GetHealthAsync(CancellationToken ct = default)
    {
        var db = EnsureConnected();
        var healthy = db.HealthStatus == DatabaseHealthStatus.Healthy;
        return Task.FromResult(new ServerHealth(
            healthy,
            healthy ? "ok" : "failed",
            Version: null,
            Detail: db.LastHealthFailure?.Message));
    }

    public Task<string> UpdateDocumentAsync(string database, string collection, string id, string json, string expectedEtag, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var db = EnsureConnected();
            var docId = new DocumentId(Guid.Parse(id));
            try
            {
                var newEtag = db.ReplaceIfEtag(collection, docId, json, expectedEtag);
                if (newEtag is null) throw new KeyNotFoundException($"Document '{id}' not found in '{collection}'.");
                return newEtag;
            }
            catch (EtagMismatchException ex)
            {
                throw new EtagConflictException(ex.ExpectedEtag, ex.ActualEtag, ex.Message);
            }
        }, ct);

    public Task DeleteDocumentAsync(string database, string collection, string id, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var db = EnsureConnected();
            var coll = db.GetCollection(collection)
                       ?? throw new KeyNotFoundException($"Collection '{collection}' not found.");
            var docId = new DocumentId(Guid.Parse(id));
            var doc = coll.FindById(docId) ?? throw new KeyNotFoundException($"Document '{id}' not found.");
            if (coll.Delete(docId)) db.NotifyDocDeleted(collection, docId, doc);
        }, ct);

    public Task<string> InsertDocumentAsync(string database, string collection, string json, CancellationToken ct = default) =>
        Task.Run(() => EnsureConnected().Insert(collection, json).ToString(), ct);

    public Task<bool> DropCollectionAsync(string database, string collection, CancellationToken ct = default) =>
        Task.Run(() => EnsureConnected().DropCollection(collection), ct);

    public Task<CompactionInfo> CompactCollectionAsync(string database, string collection, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = EnsureConnected().Compact(collection);
            sw.Stop();
            return new CompactionInfo(result.PagesCompacted, result.BytesReclaimed, sw.Elapsed.TotalMilliseconds);
        }, ct);

    private static Task ReplicationNotSupported() =>
        throw new NotSupportedException("Replication is only available through a DocumentForge service, not a direct-file connection.");

    public Task<ReplicationStatus> GetReplicationStatusAsync(string database, CancellationToken ct = default) =>
        throw new NotSupportedException("Replication is only available through a DocumentForge service, not a direct-file connection.");
    public Task StartReplicationLeaderAsync(string database, int port, CancellationToken ct = default) => ReplicationNotSupported();
    public Task StartReplicationFollowerAsync(string database, string leaderHost, int leaderPort, CancellationToken ct = default) => ReplicationNotSupported();
    public Task PromoteReplicaAsync(string database, int port, CancellationToken ct = default) => ReplicationNotSupported();

    public Task<DatabaseInfo> CreateDatabaseAsync(string name, CancellationToken ct = default) =>
        throw new NotSupportedException("A direct-file connection is a single database. Use File > New Database to create a new file.");

    public Task DropDatabaseAsync(string name, bool deleteFiles, CancellationToken ct = default) =>
        throw new NotSupportedException("A direct-file connection cannot drop databases. Disconnect and delete the file instead.");

    private DocumentForgeDb EnsureConnected() =>
        _db ?? throw new InvalidOperationException($"Connection '{Descriptor.Name}' is not open.");

    public async ValueTask DisposeAsync()
    {
        var db = _db;
        _db = null;
        if (db is not null)
            await Task.Run(db.Dispose).ConfigureAwait(false);
    }
}
