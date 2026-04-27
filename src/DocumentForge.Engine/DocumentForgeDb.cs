using DocumentForge.Core;
using DocumentForge.Document;
using DocumentForge.Index;
using DocumentForge.Query;
using DocumentForge.Storage;
using DocumentForge.Transactions;

namespace DocumentForge.Engine;

public sealed class DocumentForgeDb : IDisposable
{
    private readonly DataFile _dataFile;
    private readonly PageCache _pageCache;
    private readonly PageAllocator _allocator;
    private readonly CollectionCatalog _catalog;
    private readonly IndexCatalog _indexCatalog;
    private readonly IndexManager _indexManager;
    private readonly QueryExecutor _queryExecutor;
    private readonly TransactionManager _transactionManager;
    private readonly WalWriter? _walWriter;
    private readonly RecoveryLog? _recoveryLog;
    private ReplicationServer? _replicationServer;
    private ReplicationFollower? _replicationFollower;
    private LogicalReplicationServer? _logicalServer;
    private LogicalReplicationFollower? _logicalFollower;
    private CombinedPreFlushHook? _combinedHook;
    private bool _disposed;

    public string FilePath { get; }

    private DocumentForgeDb(string filePath, DataFile dataFile, DatabaseOptions options)
    {
        FilePath = filePath;
        _dataFile = dataFile;
        _pageCache = new PageCache(dataFile, options.CacheSizeInPages);
        _allocator = new PageAllocator(dataFile, _pageCache);
        _catalog = new CollectionCatalog(_pageCache, _allocator);

        // Use index catalog page from file header (or Invalid if new DB)
        var indexCatalogPage = _dataFile.GetIndexCatalogPage();
        _indexCatalog = new IndexCatalog(_pageCache, _allocator, indexCatalogPage);
        _indexManager = new IndexManager(_pageCache, _allocator, _indexCatalog);

        if (options.EnableWal)
        {
            var walPath = filePath + ".wal";
            _walWriter = new WalWriter(walPath);

            // Recovery log: logs page writes before they hit the data file
            var recoveryPath = filePath + ".recovery";
            _recoveryLog = new RecoveryLog(recoveryPath);
            _combinedHook = new CombinedPreFlushHook(new RecoveryLogHook(_recoveryLog));
            _pageCache.SetPreFlushHook(_combinedHook);
        }

        _transactionManager = new TransactionManager(_walWriter);
        _queryExecutor = new QueryExecutor(_catalog, _indexManager);
    }

    /// <summary>
    /// Adapter that lets the PageCache call into the RecoveryLog.
    /// </summary>
    private sealed class RecoveryLogHook : IPreFlushHook
    {
        private readonly RecoveryLog _log;
        public RecoveryLogHook(RecoveryLog log) => _log = log;
        public void OnBeforeFlush(PageId pageId, byte[] pageData) => _log.LogPageWrite(pageId, pageData);
        public void OnAfterFlushComplete() { _log.Flush(); _log.Truncate(); }
    }

    /// <summary>
    /// A PreFlushHook that forwards to multiple sub-hooks. Used so a single cache can
    /// feed both the recovery log and the replication broadcaster.
    /// </summary>
    private sealed class CombinedPreFlushHook : IPreFlushHook
    {
        private readonly List<IPreFlushHook> _hooks = new();
        public CombinedPreFlushHook(params IPreFlushHook[] initial) => _hooks.AddRange(initial);
        public void Add(IPreFlushHook hook) => _hooks.Add(hook);
        public void Remove(IPreFlushHook hook) => _hooks.Remove(hook);
        public void OnBeforeFlush(PageId pageId, byte[] pageData)
        {
            foreach (var h in _hooks) h.OnBeforeFlush(pageId, pageData);
        }
        public void OnAfterFlushComplete()
        {
            foreach (var h in _hooks) h.OnAfterFlushComplete();
        }
    }

    /// <summary>
    /// Forwards page writes to a ReplicationServer for broadcast to followers.
    /// </summary>
    private sealed class ReplicationHook : IPreFlushHook
    {
        private readonly ReplicationServer _server;
        public ReplicationHook(ReplicationServer server) => _server = server;
        public void OnBeforeFlush(PageId pageId, byte[] pageData) => _server.BroadcastPageWrite(pageId, pageData);
        public void OnAfterFlushComplete() { }
    }

    public static DocumentForgeDb Create(string filePath, DatabaseOptions? options = null)
    {
        options ??= new DatabaseOptions();
        var dataFile = DataFile.Create(filePath);
        var db = new DocumentForgeDb(filePath, dataFile, options);
        db._catalog.Load();
        // New DB has no indexes yet
        return db;
    }

    public static DocumentForgeDb Open(string filePath, DatabaseOptions? options = null)
    {
        options ??= new DatabaseOptions();

        // CRASH RECOVERY: before opening the data file, check for an unfinished recovery log.
        // If it exists and has records, it means we crashed mid-flush.
        // Replay the log entries directly onto the data file to restore durability.
        var recoveryPath = filePath + ".recovery";
        int recoveredPages = ReplayRecoveryLog(filePath, recoveryPath);

        var dataFile = DataFile.Open(filePath);
        var db = new DocumentForgeDb(filePath, dataFile, options);
        db._catalog.Load();
        // Load persistent indexes (no rebuild from scratch!)
        db._indexManager.LoadFromCatalog();
        // Eagerly build location maps for all collections - avoids 1s lag on first query
        foreach (var collName in db._catalog.GetCollectionNames())
        {
            db._catalog.GetCollection(collName)?.BuildLocationMap();
        }

        if (recoveredPages > 0)
            Console.WriteLine($"[DocumentForge] Recovered {recoveredPages} page(s) from crash recovery log.");
        return db;
    }

    public static DocumentForgeDb OpenOrCreate(string filePath, DatabaseOptions? options = null)
    {
        if (File.Exists(filePath))
            return Open(filePath, options);
        return Create(filePath, options);
    }

    /// <summary>
    /// On startup, if a recovery log exists, replay its records onto the data file.
    /// This handles the case where we crashed between WAL-write and data-file-write.
    /// Returns number of pages recovered.
    /// </summary>
    private static int ReplayRecoveryLog(string dataFilePath, string recoveryPath)
    {
        if (!File.Exists(recoveryPath)) return 0;
        var info = new FileInfo(recoveryPath);
        if (info.Length == 0)
        {
            try { File.Delete(recoveryPath); } catch { }
            return 0;
        }

        int recovered = 0;
        using (var dataStream = new FileStream(dataFilePath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            foreach (var (pageId, pageData) in RecoveryLog.ReadAllRecords(recoveryPath))
            {
                dataStream.Seek(pageId.FileOffset, SeekOrigin.Begin);
                dataStream.Write(pageData, 0, Constants.PageSize);
                recovered++;
            }
            dataStream.Flush(true);
        }

        // Log replayed - safe to delete
        try { File.Delete(recoveryPath); } catch { }
        return recovered;
    }

    // --- LINQ API ---

    /// <summary>
    /// Get a strongly-typed LINQ view of a collection. Write queries in C# instead of SQL:
    /// <code>db.Collection&lt;Order&gt;("orders").Where(o => o.Pnr == "ABC123").FirstOrDefault();</code>
    /// </summary>
    public Linq.LinqCollection<T> Collection<T>(string name) where T : class =>
        new Linq.LinqCollection<T>(this, name);

    // --- Collection API ---

    public Collection GetOrCreateCollection(string name)
    {
        return _catalog.GetOrCreateCollection(name);
    }

    public Collection? GetCollection(string name)
    {
        return _catalog.GetCollection(name);
    }

    public IReadOnlyList<string> GetCollectionNames()
    {
        return _catalog.GetCollectionNames();
    }

    public bool DropCollection(string name)
    {
        return _catalog.DropCollection(name);
    }

    // --- Document API (convenience methods) ---

    /// <summary>When true, all write operations are rejected. Set via EnterReadOnlyMode().</summary>
    public bool IsReadOnly { get; private set; }

    /// <summary>Put this DB into read-only mode. Used during planned leader handoff.</summary>
    public void EnterReadOnlyMode() => IsReadOnly = true;

    /// <summary>Exit read-only mode. Normally called when promoting a follower to leader.</summary>
    public void ExitReadOnlyMode() => IsReadOnly = false;

    private void ThrowIfReadOnly()
    {
        if (IsReadOnly) throw new DocumentForgeException("Database is in read-only mode (planned handover in progress).");
    }

    public DocumentId Insert(string collectionName, BsonDocument doc)
    {
        ThrowIfReadOnly();
        _transactionManager.AcquireWriteLock();
        try
        {
            var collection = _catalog.GetOrCreateCollection(collectionName);
            doc.EnsureId(); // ensure _id is set BEFORE we broadcast, so followers get the same id
            var id = collection.Insert(doc);
            _indexManager.OnDocumentInserted(collectionName, id, doc);

            // Broadcast to read-only followers (with sequence number assignment)
            if (_logicalServer is not null)
            {
                var bytes = BsonSerializer.Serialize(doc);
                _logicalServer.BroadcastNewOp(LogicalOpType.Insert, collectionName, bytes);
            }
            return id;
        }
        finally
        {
            _transactionManager.ReleaseWriteLock();
        }
    }

    public DocumentId Insert(string collectionName, string json)
    {
        var doc = BsonDocument.FromJson(json);
        return Insert(collectionName, doc);
    }

    /// <summary>
    /// Bulk insert: single lock acquisition, no per-doc index updates.
    /// Call RebuildIndexes() after bulk loading to populate indexes.
    /// </summary>
    public long BulkInsert(string collectionName, IEnumerable<BsonDocument> documents)
    {
        ThrowIfReadOnly();
        _transactionManager.AcquireWriteLock();
        try
        {
            var collection = _catalog.GetOrCreateCollection(collectionName);
            long count = 0;
            foreach (var doc in documents)
            {
                collection.Insert(doc);
                count++;
            }
            return count;
        }
        finally
        {
            _transactionManager.ReleaseWriteLock();
        }
    }

    /// <summary>
    /// Replace an entire document by its DocumentId. The new document keeps the
    /// original _id (we always re-stamp it) so callers don't have to thread it through.
    /// Updates indexes; broadcasts to followers as a delete-then-insert pair.
    /// Returns true if the document was found and replaced, false if not found.
    /// </summary>
    public bool Replace(string collectionName, DocumentId id, BsonDocument newDoc)
    {
        ThrowIfReadOnly();
        _transactionManager.AcquireWriteLock();
        try
        {
            var collection = _catalog.GetOrCreateCollection(collectionName);
            var oldDoc = collection.FindById(id);
            if (oldDoc is null) return false;

            // Always preserve the original _id so the replacement is in place.
            newDoc["_id"] = oldDoc["_id"];

            // Validate + apply index changes FIRST. If validation throws (e.g. a
            // unique-index collision with a different doc), the page is untouched -
            // no half-commits. Only if the index transition succeeds do we commit
            // the page.
            _indexManager.OnDocumentUpdated(collectionName, id, oldDoc, newDoc);

            if (!collection.Update(id, newDoc))
            {
                // Page write failed for an unexpected reason - try to put the index
                // back where it was. Best-effort: validation already passed for the
                // forward direction, so the reverse should too.
                try { _indexManager.OnDocumentUpdated(collectionName, id, newDoc, oldDoc); } catch { }
                return false;
            }

            // Replicate as delete + insert (LogicalOpType.Update is on the roadmap).
            if (_logicalServer is not null)
            {
                var oldBytes = BsonSerializer.Serialize(oldDoc);
                _logicalServer.BroadcastNewOp(LogicalOpType.Delete, collectionName, oldBytes);
                var newBytes = BsonSerializer.Serialize(newDoc);
                _logicalServer.BroadcastNewOp(LogicalOpType.Insert, collectionName, newBytes);
            }
            return true;
        }
        finally
        {
            _transactionManager.ReleaseWriteLock();
        }
    }

    /// <summary>
    /// Convenience overload: parse JSON then replace.
    /// </summary>
    public bool Replace(string collectionName, DocumentId id, string json)
    {
        var doc = BsonDocument.FromJson(json);
        return Replace(collectionName, id, doc);
    }

    /// <summary>
    /// Bulk update: finds all docs matching the WHERE clause via SQL and applies SET clauses.
    /// Single lock acquisition for the entire operation.
    /// </summary>
    public long BulkUpdate(string collectionName, string jsonPath, object? oldValue, string setPath, object? newValue)
    {
        _transactionManager.AcquireWriteLock();
        try
        {
            var collection = _catalog.GetCollection(collectionName);
            if (collection is null) return 0;

            var toUpdate = new List<(DocumentId id, BsonDocument doc)>();
            var searchVal = oldValue is string s ? BsonValue.FromString(s)
                          : oldValue is int i ? BsonValue.FromInt32(i)
                          : oldValue is double d ? BsonValue.FromDouble(d)
                          : oldValue is bool b ? BsonValue.FromBool(b)
                          : BsonValue.Null;

            foreach (var doc in collection.FindAll())
            {
                var val = JsonPathExtractor.Extract(doc, jsonPath);
                if (val.CompareTo(searchVal) == 0)
                    toUpdate.Add((doc.GetId(), doc));
            }

            var setVal = newValue is string sv ? BsonValue.FromString(sv)
                       : newValue is int iv ? BsonValue.FromInt32(iv)
                       : newValue is double dv ? BsonValue.FromDouble(dv)
                       : newValue is bool bv ? BsonValue.FromBool(bv)
                       : BsonValue.Null;

            long updated = 0;
            foreach (var (id, oldDoc) in toUpdate)
            {
                var newDoc = BsonDocument.FromJson(oldDoc.ToJson());
                SetNestedValue(newDoc, setPath, setVal);
                if (collection.Update(id, newDoc))
                {
                    _indexManager.OnDocumentUpdated(collectionName, id, oldDoc, newDoc);
                    updated++;
                }
            }
            return updated;
        }
        finally
        {
            _transactionManager.ReleaseWriteLock();
        }
    }

    private static void SetNestedValue(BsonDocument doc, string path, BsonValue value)
    {
        var parts = path.Split('.');
        if (parts.Length == 1) { doc[path] = value; return; }

        var current = doc;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var val = current[parts[i]];
            if (val.Type == BsonType.Document) current = val.AsDocument;
            else return;
        }
        current[parts[^1]] = value;
    }

    /// <summary>
    /// Rebuild all indexes for a collection from scratch. Use after BulkInsert
    /// or when you suspect index drift across the whole collection.
    /// </summary>
    public void RebuildIndexes(string collectionName)
    {
        var collection = _catalog.GetCollection(collectionName);
        if (collection is null) return;

        foreach (var index in _indexManager.GetIndexes(collectionName))
        {
            _indexManager.RebuildIndex(index, collection);
        }
    }

    /// <summary>
    /// Rebuild a single named index from scratch. Surgical recovery when one
    /// specific index has drifted (e.g. a unique-index half-commit corrupted it).
    /// Returns true if the index was found and rebuilt; false if not found.
    /// </summary>
    public bool RebuildIndex(string collectionName, string indexName)
    {
        var collection = _catalog.GetCollection(collectionName);
        if (collection is null) return false;

        var index = _indexManager.GetIndex(indexName);
        if (index is null) return false;

        _indexManager.RebuildIndex(index, collection);
        return true;
    }

    /// <summary>
    /// Compact a collection: defragment pages to reclaim space from deleted documents.
    /// Rebuilds the location map and all indexes after compaction.
    /// </summary>
    public CompactionResult Compact(string collectionName)
    {
        _transactionManager.AcquireWriteLock();
        try
        {
            var collection = _catalog.GetCollection(collectionName);
            if (collection is null)
                return new CompactionResult();

            long pagesCompacted = 0;
            long spaceReclaimed = 0;

            foreach (var (_, pageId, _) in collection.IterateDocuments())
            {
                // Just iterate to touch pages - we compact below
            }

            // Walk all pages and compact each one
            var currentPageId = collection.FirstDataPage;
            while (currentPageId.IsValid)
            {
                var pageData = _pageCache.GetPage(currentPageId);
                var page = new DataPage(pageData);
                var nextPageId = page.Header.NextPageId;

                if (page.DeadSpace > 0)
                {
                    int spaceBefore = page.Header.FreeSpace;
                    page.Compact();
                    int spaceAfter = page.Header.FreeSpace;
                    spaceReclaimed += (spaceAfter - spaceBefore);
                    _pageCache.PutPage(currentPageId, page.RawData, isDirty: true);
                    pagesCompacted++;
                }

                currentPageId = nextPageId;
            }

            // Rebuild location map and indexes since slot positions changed
            collection.BuildLocationMap(force: true);
            RebuildIndexes(collectionName);
            _pageCache.FlushAll();

            return new CompactionResult
            {
                PagesCompacted = pagesCompacted,
                BytesReclaimed = spaceReclaimed
            };
        }
        finally
        {
            _transactionManager.ReleaseWriteLock();
        }
    }

    // --- Query API ---

    public QueryResult Execute(string query)
    {
        // Determine if this is a read or write query
        var trimmed = query.TrimStart();
        bool isWrite = trimmed.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) ||
                       trimmed.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) ||
                       trimmed.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase) ||
                       trimmed.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase) ||
                       trimmed.StartsWith("DROP", StringComparison.OrdinalIgnoreCase);

        if (isWrite)
        {
            ThrowIfReadOnly();
            _transactionManager.AcquireWriteLock();
            try { return _queryExecutor.Execute(query); }
            finally { _transactionManager.ReleaseWriteLock(); }
        }
        else
        {
            _transactionManager.AcquireReadLock();
            try { return _queryExecutor.Execute(query); }
            finally { _transactionManager.ReleaseReadLock(); }
        }
    }

    // --- Index API ---

    public void CreateIndex(string collectionName, string jsonPath, string? indexName = null, bool unique = false)
    {
        indexName ??= $"idx_{collectionName}_{jsonPath.Replace('.', '_').Replace('[', '_').Replace("]", "")}";

        var collection = _catalog.GetCollection(collectionName);
        if (collection is null)
            throw new CollectionNotFoundException(collectionName);

        var definition = new IndexDefinition
        {
            Name = indexName,
            CollectionName = new CollectionName(collectionName),
            JsonPath = jsonPath,
            IsUnique = unique
        };

        var index = _indexManager.CreateIndex(definition);
        _indexManager.RebuildIndex(index, collection);

        if (_indexCatalog.CatalogPage.IsValid)
        {
            _dataFile.SetIndexCatalogPage(_indexCatalog.CatalogPage);
        }

        // Replicate index creation to read-only followers
        if (_logicalServer is not null)
        {
            var data = SerializeIndexDefinition(indexName, jsonPath, unique);
            _logicalServer.BroadcastNewOp(LogicalOpType.CreateIndex, collectionName, data);
        }
    }

    private static byte[] SerializeIndexDefinition(string name, string path, bool unique)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(name);
        w.Write(path);
        w.Write(unique);
        return ms.ToArray();
    }

    private static (string Name, string Path, bool Unique) DeserializeIndexDefinition(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);
        return (r.ReadString(), r.ReadString(), r.ReadBoolean());
    }

    public IReadOnlyList<IndexDefinition> GetIndexes(string collectionName)
    {
        return _indexManager.GetIndexes(collectionName)
            .Select(idx => idx.Definition)
            .ToList();
    }

    // --- Transaction API ---

    public Transaction BeginTransaction()
    {
        return _transactionManager.BeginTransaction();
    }

    // --- Replication API ---

    /// <summary>
    /// Start this DB as a replication leader - streams page writes to connected followers.
    /// </summary>
    public void StartReplicationServer(int port)
    {
        if (_replicationServer is not null)
            throw new DocumentForgeException("Replication server already running.");
        if (_combinedHook is null)
            throw new DocumentForgeException("Replication requires WAL to be enabled.");

        _replicationServer = new ReplicationServer(port);
        _replicationServer.Start();
        _combinedHook.Add(new ReplicationHook(_replicationServer));
    }

    public int GetFollowerCount() => _replicationServer?.FollowerCount ?? 0;

    /// <summary>
    /// Start this DB as a replication follower - connects to a leader and applies streamed writes.
    /// The local data file is updated directly; call Reopen() to see changes in the engine.
    /// </summary>
    public void StartReplicationFollower(string host, int port)
    {
        if (_replicationFollower is not null)
            throw new DocumentForgeException("Replication follower already running.");

        // When a page arrives from the leader, write it through the data file
        // and evict from our cache so the next read picks up the new version.
        _replicationFollower = new ReplicationFollower(host, port, (pageId, pageData) =>
        {
            _dataFile.WritePage(pageId, pageData);
            _pageCache.Evict(pageId);
        });
        _replicationFollower.Start();
    }

    public long ReplicatedPageCount() => _replicationFollower?.PagesApplied ?? 0;

    /// <summary>
    /// Start this DB as a logical replication leader - streams operations (inserts/deletes/index ops)
    /// to connected read-only followers. Unlike physical replication, followers that connect here
    /// can SERVE QUERIES correctly because their indexes stay coherent.
    /// </summary>
    public void StartLogicalReplicationServer(int port, string? sharedSecret = null)
    {
        if (_logicalServer is not null)
            throw new DocumentForgeException("Logical replication server already running.");
        _logicalServer = new LogicalReplicationServer(port, opLogCapacity: 10_000, secret: sharedSecret);
        _logicalServer.Start();
    }

    public int GetLogicalFollowerCount() => _logicalServer?.FollowerCount ?? 0;

    /// <summary>
    /// Start this DB as a logical replication follower (read-only replica).
    /// Applies incoming ops through the engine's own Insert/Delete/CreateIndex so indexes
    /// and location maps stay coherent. Queries on this instance will see replicated data.
    /// </summary>
    public void StartLogicalReplicationFollower(string host, int port, string? sharedSecret = null)
    {
        if (_logicalFollower is not null)
            throw new DocumentForgeException("Logical replication follower already running.");

        var seqFilePath = FilePath + ".followerseq";
        _logicalFollower = new LogicalReplicationFollower(host, port, seqFilePath, op =>
        {
            switch (op.OpType)
            {
                case LogicalOpType.Insert:
                    var doc = BsonSerializer.Deserialize(op.Data);
                    // Use Insert directly; it acquires write lock internally.
                    // The doc's _id is already set, so EnsureId is a no-op and we preserve the leader's id.
                    InsertOnFollower(op.Collection, doc);
                    break;
                case LogicalOpType.Delete:
                    var docId = DocumentId.FromBytes(op.Data);
                    DeleteOnFollower(op.Collection, docId);
                    break;
                case LogicalOpType.CreateIndex:
                    var (name, path, unique) = DeserializeIndexDefinition(op.Data);
                    try { CreateIndex(op.Collection, path, name, unique); } catch { /* already exists */ }
                    break;
            }
        }, secret: sharedSecret);
        _logicalFollower.Start();
    }

    public long LogicallyReplicatedOps() => _logicalFollower?.OpsApplied ?? 0;

    /// <summary>
    /// Promote this follower to a leader. Disconnects from the previous leader, starts a
    /// replication server on the given port, and exits read-only mode (if set).
    /// Use this in a planned-handover flow after the old leader has gone read-only and
    /// this follower has caught up to the old leader's final seq.
    /// </summary>
    public void PromoteToLeader(int serverPort, int opLogCapacity = 10_000)
    {
        // Stop being a follower
        _logicalFollower?.Dispose();
        _logicalFollower = null;

        // Start a replication server of our own
        _logicalServer = new LogicalReplicationServer(serverPort, opLogCapacity);
        _logicalServer.Start();

        // Accept writes
        ExitReadOnlyMode();

        Console.WriteLine($"[Handover] Promoted to leader on port {serverPort}");
    }

    /// <summary>
    /// Orchestrate a planned handover to a new leader.
    /// Steps: enter read-only mode, wait for the new leader (currently a follower) to catch up,
    /// return the final seq. After this returns, you can promote the follower to leader.
    /// Throws if catchup doesn't complete within the timeout.
    /// </summary>
    public ulong BeginPlannedHandover(Func<ulong> followerLastSeqProbe, TimeSpan timeout)
    {
        if (_logicalServer is null)
            throw new DocumentForgeException("Not a leader - nothing to hand over from.");

        // Step 1: stop accepting writes on this DB
        EnterReadOnlyMode();
        var finalSeq = _logicalServer.CurrentSeq;
        Console.WriteLine($"[Handover] Read-only mode entered. Final seq = {finalSeq}");

        // Step 2: wait for the follower to catch up
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var followerSeq = followerLastSeqProbe();
            if (followerSeq >= finalSeq)
            {
                Console.WriteLine($"[Handover] Follower caught up to seq {followerSeq} - handover can proceed.");
                return finalSeq;
            }
            Thread.Sleep(50);
        }

        // Timeout - abort, re-enable writes
        ExitReadOnlyMode();
        throw new DocumentForgeException(
            $"Handover timed out waiting for follower to catch up. Leader seq={finalSeq}, last-seen follower seq too old.");
    }
    public long GapsDetected => _logicalFollower?.GapsDetected ?? 0;
    public ulong FollowerLastSeq => _logicalFollower?.LastAppliedSeq ?? 0;
    public ulong LeaderCurrentSeq => _logicalServer?.CurrentSeq ?? 0;
    public DateTimeOffset? LastLeaderMessage =>
        _logicalFollower?.LastMessageAt == DateTimeOffset.MinValue ? null : _logicalFollower?.LastMessageAt;

    // --- Auto-failover ---

    private CancellationTokenSource? _autoFailoverCts;
    private bool _autoFailoverPromoted;

    /// <summary>
    /// Enable automatic promotion to leader if the leader goes silent.
    /// Watches the follower's last-message timestamp. If no heartbeat or op arrives
    /// within <paramref name="silenceTimeout"/>, disconnects from the leader and
    /// promotes this node to leader on <paramref name="newLeaderPort"/>.
    /// </summary>
    public void EnableAutoFailover(int newLeaderPort, TimeSpan silenceTimeout, Action<int>? onPromoted = null)
    {
        if (_logicalFollower is null)
            throw new DocumentForgeException("Auto-failover requires the node to be a logical replication follower.");
        if (_autoFailoverCts is not null)
            throw new DocumentForgeException("Auto-failover already enabled.");

        _autoFailoverCts = new CancellationTokenSource();
        var ct = _autoFailoverCts.Token;

        _ = Task.Run(async () =>
        {
            // Grace period: let the initial connection settle
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch { return; }

            while (!ct.IsCancellationRequested && !_autoFailoverPromoted)
            {
                try { await Task.Delay(TimeSpan.FromMilliseconds(500), ct); }
                catch { break; }

                var lastMsg = _logicalFollower?.LastMessageAt ?? DateTimeOffset.MinValue;
                if (lastMsg == DateTimeOffset.MinValue) continue; // no messages yet

                var silent = DateTimeOffset.UtcNow - lastMsg;
                if (silent > silenceTimeout)
                {
                    Console.WriteLine($"[AutoFailover] Leader silent for {silent.TotalSeconds:F1}s (threshold: {silenceTimeout.TotalSeconds:F1}s) - promoting.");
                    _autoFailoverPromoted = true;
                    try
                    {
                        PromoteToLeader(newLeaderPort);
                        onPromoted?.Invoke(newLeaderPort);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[AutoFailover] Promotion failed: {ex.Message}");
                    }
                    break;
                }
            }
        }, ct);
    }

    /// <summary>Stop watching for leader silence. Safe to call even if never enabled.</summary>
    public void DisableAutoFailover()
    {
        _autoFailoverCts?.Cancel();
        _autoFailoverCts?.Dispose();
        _autoFailoverCts = null;
    }

    /// <summary>True if this node was auto-promoted via the failover mechanism.</summary>
    public bool WasAutoFailoverPromoted => _autoFailoverPromoted;

    private void InsertOnFollower(string collectionName, BsonDocument doc)
    {
        _transactionManager.AcquireWriteLock();
        try
        {
            var collection = _catalog.GetOrCreateCollection(collectionName);
            var id = collection.Insert(doc);
            _indexManager.OnDocumentInserted(collectionName, id, doc);
            // DO NOT re-broadcast - followers don't replicate to other followers
        }
        finally { _transactionManager.ReleaseWriteLock(); }
    }

    /// <summary>
    /// Hook so transports and the rebalancer can notify the index manager after a
    /// direct delete that bypassed SQL.
    /// </summary>
    public void NotifyDocDeleted(string collectionName, DocumentId docId, BsonDocument doc)
    {
        _indexManager.OnDocumentDeleted(collectionName, docId, doc);
    }

    private void DeleteOnFollower(string collectionName, DocumentId docId)
    {
        _transactionManager.AcquireWriteLock();
        try
        {
            var collection = _catalog.GetCollection(collectionName);
            if (collection is null) return;
            var doc = collection.FindById(docId);
            if (doc is null) return;
            collection.Delete(docId);
            _indexManager.OnDocumentDeleted(collectionName, docId, doc);
        }
        finally { _transactionManager.ReleaseWriteLock(); }
    }

    // --- Database management ---

    public DatabaseStatistics GetStatistics()
    {
        var stats = new DatabaseStatistics
        {
            FilePath = FilePath,
            FileSize = new FileInfo(FilePath).Length,
            PageCount = _dataFile.PageCount,
            CachedPages = _pageCache.Count,
            DirtyPages = _pageCache.DirtyCount,
        };

        foreach (var collName in _catalog.GetCollectionNames())
        {
            var coll = _catalog.GetCollection(collName);
            if (coll is null) continue;
            var docCount = coll.FindAll().Count();
            var indexes = _indexManager.GetIndexes(collName);

            stats.Collections.Add(new CollectionStatistics
            {
                Name = collName,
                DocumentCount = docCount,
                IndexCount = indexes.Count,
                Indexes = indexes.Select(i => new IndexStatistics
                {
                    Name = i.Definition.Name,
                    JsonPath = i.Definition.JsonPath,
                    EntryCount = i.TotalEntries,
                    IsUnique = i.Definition.IsUnique
                }).ToList()
            });
        }

        return stats;
    }

    public void Checkpoint()
    {
        _transactionManager.AcquireWriteLock();
        try
        {
            _pageCache.FlushAll();
            _walWriter?.Truncate();
        }
        finally
        {
            _transactionManager.ReleaseWriteLock();
        }
    }

    public void Flush()
    {
        _pageCache.FlushAll();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            DisableAutoFailover();
            _replicationServer?.Dispose();
            _replicationFollower?.Dispose();
            _logicalServer?.Dispose();
            _logicalFollower?.Dispose();
            _pageCache.FlushAll(); // this also truncates the recovery log
            _walWriter?.Dispose();
            _recoveryLog?.Dispose();
            _dataFile.Dispose();
            var recoveryPath = FilePath + ".recovery";
            try { if (File.Exists(recoveryPath) && new FileInfo(recoveryPath).Length == 0) File.Delete(recoveryPath); } catch { }
            _disposed = true;
        }
    }
}

public sealed class DatabaseStatistics
{
    public string FilePath { get; set; } = "";
    public long FileSize { get; set; }
    public uint PageCount { get; set; }
    public int CachedPages { get; set; }
    public int DirtyPages { get; set; }
    public List<CollectionStatistics> Collections { get; set; } = new();
}

public sealed class CollectionStatistics
{
    public string Name { get; set; } = "";
    public long DocumentCount { get; set; }
    public int IndexCount { get; set; }
    public List<IndexStatistics> Indexes { get; set; } = new();
}

public sealed class IndexStatistics
{
    public string Name { get; set; } = "";
    public string JsonPath { get; set; } = "";
    public int EntryCount { get; set; }
    public bool IsUnique { get; set; }
}

public sealed class CompactionResult
{
    public long PagesCompacted { get; set; }
    public long BytesReclaimed { get; set; }
}
