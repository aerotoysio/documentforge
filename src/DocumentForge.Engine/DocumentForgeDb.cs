using DocumentForge.Core;
using DocumentForge.Document;
using DocumentForge.Engine.Cluster;
using DocumentForge.Index;
using DocumentForge.Query;
using DocumentForge.Storage;
using DocumentForge.Transactions;

namespace DocumentForge.Engine;

public sealed class DocumentForgeDb : IDisposable, DocumentForge.Transactions.ITransactionScope
{
    private readonly DatabaseLock _lock;
    private readonly IDataFile _dataFile;
    private readonly PageCache _pageCache;
    private readonly PageAllocator _allocator;
    private readonly CollectionCatalog _catalog;
    private readonly IndexCatalog _indexCatalog;
    private readonly IndexManager _indexManager;
    private readonly QueryExecutor _queryExecutor;
    private readonly TransactionManager _transactionManager;
    private readonly RecoveryLog? _recoveryLog;
    private readonly bool _durableCommits;
    private ReplicationServer? _replicationServer;
    private ReplicationFollower? _replicationFollower;
    private LogicalReplicationServer? _logicalServer;
    private LogicalReplicationFollower? _logicalFollower;
    private CombinedPreFlushHook? _combinedHook;

    /// <summary>Issue #88 — attach an external pre-flush hook so the PITR
    /// archiver can capture every page write at the moment it's about to
    /// be flushed to the data file. Used by <c>WalArchiver</c> in
    /// DocumentForge.Cli; symmetric with the way the replication
    /// subsystem attaches its own hook internally. Safe to call after
    /// Open; the hook is invoked synchronously on the flush path, so
    /// implementations must be fast and non-blocking.</summary>
    public void AddPreFlushHook(IPreFlushHook hook) => _combinedHook?.Add(hook);

    /// <summary>Detach a previously-added hook. No-op if not present.</summary>
    public void RemovePreFlushHook(IPreFlushHook hook) => _combinedHook?.Remove(hook);
    private bool _disposed;

    public string FilePath { get; }

    private DocumentForgeDb(string filePath, IDataFile dataFile, DatabaseLock lockHandle, DatabaseOptions options)
    {
        FilePath = filePath;
        _lock = lockHandle;
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
            // Recovery log = the engine's real write-ahead log (issues
            // #89/#90): commit paths append page images + a fsync'd commit
            // marker; Open replays sealed batches. The old ".wal"
            // (WalWriter) logged payload-free markers nothing ever read —
            // deleted as dead code; clean up its stale sidecar.
            try { File.Delete(filePath + ".wal"); } catch { }

            var recoveryPath = filePath + ".recovery";
            _recoveryLog = new RecoveryLog(recoveryPath);
            _combinedHook = new CombinedPreFlushHook(new RecoveryLogHook(_recoveryLog));
            _pageCache.SetPreFlushHook(_combinedHook);
        }
        _durableCommits = options.DurableCommits && options.EnableWal;

        _transactionManager = new TransactionManager();
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
        // Eviction path: fsync the log so recovery replay can resurrect the
        // evicted page if the data-file write was lost. Don't truncate —
        // we still need the entries for the next FlushAll's truncate cycle.
        public void EnsureLogDurable() => _log.Flush();
        // Issues #89/#90: seal the pages logged since the last marker as one
        // committed batch and fsync. This is the durability point of every
        // acknowledged write.
        public void OnCommitDurable() => _log.Commit();
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
        public void EnsureLogDurable()
        {
            foreach (var h in _hooks) h.EnsureLogDurable();
        }
        public void OnCommitDurable()
        {
            foreach (var h in _hooks) h.OnCommitDurable();
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
        // Replication has no durability concept — broadcast is fire-and-forget;
        // followers reconnect on disconnect. Nothing to fsync here.
        public void EnsureLogDurable() { }
        // Side benefit of commit-time capture: followers now receive pages at
        // commit rather than at the next flush, shrinking replication lag.
        public void OnCommitDurable() { }
    }

    public static DocumentForgeDb Create(string filePath, DatabaseOptions? options = null)
    {
        options ??= new DatabaseOptions();
        // Acquire the on-disk lock BEFORE creating the data file so two
        // concurrent Create calls don't both succeed and end up with
        // overlapping writes. If the data file exists already and is locked
        // by someone else, the FileMode.CreateNew below would fail anyway —
        // checking the lock first gives a clearer error.
        var lockHandle = DatabaseLock.Acquire(filePath, options.ForceUnlock);
        try
        {
            var dataFile = DataFile.Create(filePath);
            var db = new DocumentForgeDb(filePath, dataFile, lockHandle, options);
            db._catalog.Load();
            // New DB has no indexes yet
            return db;
        }
        catch
        {
            lockHandle.Dispose();
            throw;
        }
    }

    public static DocumentForgeDb Open(string filePath, DatabaseOptions? options = null)
    {
        options ??= new DatabaseOptions();

        // Acquire the on-disk lock BEFORE recovery replay or any data-file
        // mutation. The recovery log is rewritten by replay, so two processes
        // racing on Open could each clobber the other's recovery progress.
        var lockHandle = DatabaseLock.Acquire(filePath, options.ForceUnlock);
        try
        {
            // Issue #20: integrate a snapshot delivered by the replication
            // follower path before the data file opens. The marker file
            // (`.snapshot.incoming.seq`) is dropped after the snapshot bytes
            // are durable; if it exists, swap the snapshot in place of the
            // current data file. The data file goes away with whatever
            // crashed-but-recoverable state it had — the snapshot supersedes.
            IntegratePendingSnapshot(filePath);
            // CRASH RECOVERY: before opening the data file, check for an unfinished recovery log.
            // If it exists and has records, it means we crashed mid-flush.
            // Replay the log entries directly onto the data file to restore durability.
            var recoveryPath = filePath + ".recovery";
            int recoveredPages = ReplayRecoveryLog(filePath, recoveryPath);

            var dataFile = DataFile.Open(filePath);
            var db = new DocumentForgeDb(filePath, dataFile, lockHandle, options);
            db._catalog.Load();
            // Load persistent indexes (no rebuild from scratch!)
            // Issue #64: a structurally corrupt index catalog (cycle on disk
            // surviving recovery) must not block the database from opening.
            // The document data is in a separate page chain and is intact;
            // we surface a warning and open with no indexes. Operators can
            // re-create indexes via CreateIndex.
            if (!db._indexManager.TryLoadFromCatalog(out var indexCorruption))
            {
                Console.Error.WriteLine(
                    $"[DocumentForge] WARNING: index catalog corrupted ({indexCorruption}); " +
                    $"opening database with no indexes. Document data is intact. " +
                    $"Re-create indexes via CreateIndex. (Issue #64)");
                db._indexManager.ResetCatalog();
            }
            // Eagerly build location maps for all collections - avoids 1s lag on first query
            foreach (var collName in db._catalog.GetCollectionNames())
            {
                db._catalog.GetCollection(collName)?.BuildLocationMap();
            }

            if (recoveredPages > 0)
                Console.WriteLine($"[DocumentForge] Recovered {recoveredPages} page(s) from crash recovery log.");
            return db;
        }
        catch
        {
            lockHandle.Dispose();
            throw;
        }
    }

    public static DocumentForgeDb OpenOrCreate(string filePath, DatabaseOptions? options = null)
    {
        if (File.Exists(filePath))
            return Open(filePath, options);
        return Create(filePath, options);
    }

    /// <summary>
    /// Test seam: open a DB backed by a caller-supplied <see cref="IDataFile"/>
    /// instead of the standard <c>DataFile.Open(filePath)</c>. Used by the
    /// crash-injection harness (issue #28) to wrap the real data file in a
    /// fault-injecting decorator. Skips recovery-log replay so the test can
    /// drive its own state.
    ///
    /// <para>
    /// <c>internal</c> on purpose — this surface is for verifying engine
    /// invariants under simulated I/O failure, not a public extension point.
    /// Production callers go through <see cref="Open"/> / <see cref="Create"/>.
    /// </para>
    /// </summary>
    internal static DocumentForgeDb OpenWithDataFile(string filePath, IDataFile dataFile, DatabaseOptions? options = null)
    {
        options ??= new DatabaseOptions();
        var lockHandle = DatabaseLock.Acquire(filePath, options.ForceUnlock);
        try
        {
            var db = new DocumentForgeDb(filePath, dataFile, lockHandle, options);
            db._catalog.Load();
            // Mirror Open's #64 self-heal behaviour so crash-injection tests
            // exercise the same code path as production.
            if (!db._indexManager.TryLoadFromCatalog(out var indexCorruption))
            {
                Console.Error.WriteLine(
                    $"[DocumentForge] WARNING: index catalog corrupted ({indexCorruption}); " +
                    $"opening database with no indexes. Document data is intact. " +
                    $"Re-create indexes via CreateIndex. (Issue #64)");
                db._indexManager.ResetCatalog();
            }
            foreach (var collName in db._catalog.GetCollectionNames())
                db._catalog.GetCollection(collName)?.BuildLocationMap();
            return db;
        }
        catch
        {
            lockHandle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Issue #20: if the replication follower has dropped a snapshot in our
    /// data directory and marked it ready (sibling <c>.seq</c> file), atomically
    /// swap it over the live data file before Open proceeds. The recovery log,
    /// WAL, and follower-seq are all reset because the snapshot represents a
    /// consistent point-in-time that supersedes any prior in-flight writes.
    /// </summary>
    private static void IntegratePendingSnapshot(string filePath)
    {
        var snapshotPath = filePath + ".snapshot.incoming";
        var markerPath = snapshotPath + ".seq";
        if (!File.Exists(markerPath) || !File.Exists(snapshotPath)) return;

        ulong snapshotSeq;
        try { snapshotSeq = BitConverter.ToUInt64(File.ReadAllBytes(markerPath)); }
        catch { return; /* corrupt marker — leave both files for operator inspection */ }

        Console.WriteLine($"[DocumentForge] Integrating snapshot from {snapshotPath} at seq {snapshotSeq}");

        // Move the snapshot over the live data file. File.Move with overwrite
        // is a single rename on most filesystems — atomic vs concurrent
        // readers (which we don't have at this stage of Open anyway). Then
        // delete the recovery log + WAL since the snapshot subsumes them.
        try
        {
            File.Move(snapshotPath, filePath, overwrite: true);
            try { File.Delete(filePath + ".recovery"); } catch { }
            try { File.Delete(filePath + ".wal"); } catch { }
            // Persist the seq the snapshot represents into the follower-seq
            // file so the next replication catchup picks up from there.
            try { File.WriteAllBytes(filePath + ".followerseq", BitConverter.GetBytes(snapshotSeq)); } catch { }
            File.Delete(markerPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DocumentForge] Snapshot integration failed: {ex.Message}");
        }
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
        if (info.Length <= 8) // empty, or just the v2 file header
        {
            try { File.Delete(recoveryPath); } catch { }
            return 0;
        }

        int recovered = 0;
        using (var dataStream = new FileStream(dataFilePath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            // Issues #89/#90/#91: v2 logs replay only marker-sealed commit
            // batches — a trailing unsealed batch is a crash mid-commit and
            // is discarded, so partially-logged operations never surface.
            // Legacy logs (incl. PITR-synthesized ones) replay every valid
            // record, the pre-#89 behaviour.
            foreach (var (pageId, pageData) in RecoveryLog.ReplayRecords(recoveryPath))
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
        ThrowIfReadOnly();
        _transactionManager.AcquireWriteLock();
        try
        {
            // Drop the indexes BEFORE the collection, so even if something
            // throws mid-way the registry never lists indexes for a vanished
            // collection. Both halves run inside the same write lock so
            // concurrent readers never see the partial state.
            _indexManager.DropAllIndexesForCollection(name);
            var dropped = _catalog.DropCollection(name);
            CommitDurableLocked();
            return dropped;
        }
        finally
        {
            _transactionManager.ReleaseWriteLock();
        }
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

    // --- Engine health (issue #25) ---
    //
    // Pre-fix an IOException out of WritePage / FlushAll propagated unhandled
    // through the public Insert/Replace/Delete/Execute surface. The engine
    // happily kept accepting more writes against a possibly-corrupt page
    // cache, hoping subsequent writes would succeed — instead they could
    // silently overwrite earlier-failed pages with new data, masking the
    // original failure and producing inconsistent state.
    //
    // Now: any IOException out of the storage layer flips _health to Failed.
    // Subsequent writes throw DatabaseHealthException immediately. The only
    // recovery is Dispose + Open, which runs the recovery-log replay and
    // restores the on-disk state to a consistent point.

    private DatabaseHealthStatus _health = DatabaseHealthStatus.Healthy;

    /// <summary>Current engine health. <see cref="DatabaseHealthStatus.Failed"/>
    /// after any IOException out of the storage layer; new writes are rejected
    /// until Dispose + Open. Surfaces as <c>"degraded"</c> in <c>/health</c>.</summary>
    public DatabaseHealthStatus HealthStatus => _health;

    /// <summary>The IOException that flipped <see cref="HealthStatus"/> to
    /// Failed, or null if still healthy. Useful for telemetry / operator
    /// diagnostics.</summary>
    public Exception? LastHealthFailure { get; private set; }

    private void EnsureHealthy()
    {
        if (_health == DatabaseHealthStatus.Failed)
            throw new DatabaseHealthException(
                $"Database '{FilePath}' is in Failed state from an earlier I/O error " +
                $"({LastHealthFailure?.GetType().Name}: {LastHealthFailure?.Message}). " +
                "Dispose and Open the database to recover (the recovery log will replay).");
    }

    /// <summary>Run a write under health tracking: any IOException flips the
    /// engine to Failed and the original exception re-throws. Read-only ops
    /// don't go through this — they're allowed to fail without poisoning the
    /// engine state.</summary>
    private T TrackHealth<T>(Func<T> op)
    {
        try { return op(); }
        catch (IOException ex)
        {
            _health = DatabaseHealthStatus.Failed;
            LastHealthFailure = ex;
            throw;
        }
    }

    private void TrackHealth(Action op)
    {
        try { op(); }
        catch (IOException ex)
        {
            _health = DatabaseHealthStatus.Failed;
            LastHealthFailure = ex;
            throw;
        }
    }

    public DocumentId Insert(string collectionName, BsonDocument doc)
    {
        ThrowIfReadOnly();
        EnsureHealthy();
        _transactionManager.AcquireWriteLock();
        try
        {
            return TrackHealth(() =>
            {
                var collection = _catalog.GetOrCreateCollection(collectionName);
                doc.EnsureId(); // ensure _id is set BEFORE we broadcast, so followers get the same id
                // Stamp the optimistic-concurrency token. Issue #18: every doc the
                // engine writes carries an `_etag`; clients use it for If-Match
                // PUTs without inventing their own version field. Caller-supplied
                // _etag values are intentionally overwritten — clients shouldn't
                // forge them.
                doc.StampFreshEtag();

                // Pre-validate uniqueness. If any unique index would reject the doc,
                // throw BEFORE the page write so the on-disk state is untouched.
                // Pre-fix this happened in the wrong order: page wrote, index threw,
                // doc stranded on disk. (issue #9)
                _indexManager.ValidateUniqueInsert(collectionName, doc);

                var id = collection.Insert(doc);
                _indexManager.OnDocumentInserted(collectionName, id, doc);

                // Broadcast to read-only followers (with sequence number assignment)
                if (_logicalServer is not null)
                {
                    var bytes = BsonSerializer.Serialize(doc);
                    _logicalServer.BroadcastNewOp(LogicalOpType.Insert, collectionName, bytes);
                }
                CommitDurableLocked();
                return id;
            });
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
            CommitDurableLocked();
            return count;
        }
        finally
        {
            _transactionManager.ReleaseWriteLock();
        }
    }

    /// <summary>
    /// Bulk insert with per-document tracking, index maintenance, and replication.
    /// Holds the write lock once for the whole batch, so it's still bulk-fast,
    /// but each insert is independently tried/caught - we get IDs for successes
    /// and structured errors for failures.
    ///
    /// When <paramref name="atomic"/> is true, the first failure rolls back every
    /// previously-inserted doc in the same lock window before returning. The lock
    /// guarantees no other writer sees a partial state.
    /// </summary>
    public BulkInsertResult BulkInsertTracked(
        string collectionName,
        IReadOnlyList<BsonDocument> documents,
        bool atomic = false)
    {
        ThrowIfReadOnly();
        _transactionManager.AcquireWriteLock();
        try
        {
            var collection = _catalog.GetOrCreateCollection(collectionName);
            var insertedIds = new List<DocumentId>(documents.Count);
            var errors = new List<BulkInsertError>();

            for (int i = 0; i < documents.Count; i++)
            {
                try
                {
                    var doc = documents[i];
                    doc.EnsureId();
                    doc.StampFreshEtag(); // issue #18 — same discipline as single Insert
                    // Validate before the page write so a unique-index conflict
                    // doesn't leave a stranded doc behind (issue #9).
                    _indexManager.ValidateUniqueInsert(collectionName, doc);

                    var id = collection.Insert(doc);
                    _indexManager.OnDocumentInserted(collectionName, id, doc);
                    insertedIds.Add(id);

                    if (_logicalServer is not null)
                    {
                        var bytes = BsonSerializer.Serialize(doc);
                        _logicalServer.BroadcastNewOp(LogicalOpType.Insert, collectionName, bytes);
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(new BulkInsertError(i, ex.Message));

                    if (atomic)
                    {
                        // Roll back every successful insert from this batch.
                        // Same write-lock window so concurrent readers never observe
                        // the partially-applied state.
                        foreach (var rollbackId in insertedIds)
                        {
                            var existing = collection.FindById(rollbackId);
                            if (existing is not null && collection.Delete(rollbackId))
                            {
                                _indexManager.OnDocumentDeleted(collectionName, rollbackId, existing);
                                if (_logicalServer is not null)
                                {
                                    var bytes = BsonSerializer.Serialize(existing);
                                    _logicalServer.BroadcastNewOp(LogicalOpType.Delete, collectionName, bytes);
                                }
                            }
                        }
                        // Seal the reverse-applied pages too: the rollback's
                        // net-zero state must be what survives a crash, not
                        // whatever an unrelated later commit happens to seal.
                        CommitDurableLocked();
                        return new BulkInsertResult(Array.Empty<DocumentId>(), errors, RolledBack: true);
                    }
                }
            }

            CommitDurableLocked();
            return new BulkInsertResult(insertedIds, errors, RolledBack: false);
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
        EnsureHealthy();
        _transactionManager.AcquireWriteLock();
        try
        {
            return TrackHealth(() =>
            {
                var collection = _catalog.GetOrCreateCollection(collectionName);
                var oldDoc = collection.FindById(id);
                if (oldDoc is null) return false;

                // Always preserve the original _id so the replacement is in place.
                newDoc["_id"] = oldDoc["_id"];
                // Re-stamp the optimistic-concurrency token. Issue #18: every Replace
                // mints a fresh _etag so subsequent If-Match clients see the change.
                newDoc.StampFreshEtag();

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
                CommitDurableLocked();
                return true;
            });
        }
        finally
        {
            _transactionManager.ReleaseWriteLock();
        }
    }

    /// <summary>
    /// Optimistic-concurrency replace. Reads the current document, compares
    /// its <c>_etag</c> against <paramref name="expectedEtag"/>, and only
    /// replaces if they match. Returns the new <c>_etag</c> on success;
    /// throws <see cref="EtagMismatchException"/> on mismatch (the doc
    /// changed since the caller GET'd it). Returns null on not-found —
    /// distinguishable from mismatch because not-found doesn't throw.
    ///
    /// <para>
    /// The check + write happens under a single write-lock window, so a
    /// concurrent writer racing in between cannot create a TOCTOU window
    /// that lets two If-Match clients both succeed against the same
    /// pre-image. Issue #18.
    /// </para>
    /// </summary>
    public string? ReplaceIfEtag(string collectionName, DocumentId id, BsonDocument newDoc, string expectedEtag)
    {
        ThrowIfReadOnly();
        EnsureHealthy();
        _transactionManager.AcquireWriteLock();
        try
        {
            var collection = _catalog.GetCollection(collectionName);
            if (collection is null) return null;
            var oldDoc = collection.FindById(id);
            if (oldDoc is null) return null;

            var actualEtag = oldDoc.GetEtag();
            if (!string.Equals(actualEtag, expectedEtag, StringComparison.Ordinal))
                throw new EtagMismatchException(expectedEtag, actualEtag);

            // Same as the unguarded Replace from here — preserve _id, stamp
            // a fresh _etag, validate index transitions, write the page,
            // broadcast.
            newDoc["_id"] = oldDoc["_id"];
            newDoc.StampFreshEtag();
            _indexManager.OnDocumentUpdated(collectionName, id, oldDoc, newDoc);
            if (!collection.Update(id, newDoc))
            {
                try { _indexManager.OnDocumentUpdated(collectionName, id, newDoc, oldDoc); } catch { }
                return null;
            }

            if (_logicalServer is not null)
            {
                var oldBytes = BsonSerializer.Serialize(oldDoc);
                _logicalServer.BroadcastNewOp(LogicalOpType.Delete, collectionName, oldBytes);
                var newBytes = BsonSerializer.Serialize(newDoc);
                _logicalServer.BroadcastNewOp(LogicalOpType.Insert, collectionName, newBytes);
            }
            CommitDurableLocked();
            return newDoc.GetEtag();
        }
        finally
        {
            _transactionManager.ReleaseWriteLock();
        }
    }

    /// <summary>JSON convenience overload of <see cref="ReplaceIfEtag(string, DocumentId, BsonDocument, string)"/>.</summary>
    public string? ReplaceIfEtag(string collectionName, DocumentId id, string json, string expectedEtag) =>
        ReplaceIfEtag(collectionName, id, BsonDocument.FromJson(json), expectedEtag);

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
            CommitDurableLocked();
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
            EnsureHealthy();
            _transactionManager.AcquireWriteLock();
            try
            {
                return TrackHealth(() =>
                {
                    var result = _queryExecutor.Execute(query);
                    if (result.Success) CommitDurableLocked();
                    return result;
                });
            }
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

        // Issues #89/#90 brought this under the write lock: index DDL dirties
        // catalog + index pages and must be sealed as its own WAL commit, not
        // interleaved with a concurrent writer's pages. (It was previously the
        // only mutating public API that ran unlocked.)
        _transactionManager.AcquireWriteLock();
        try
        {
            // Auto-create the collection if it doesn't exist yet — matches Insert /
            // BulkInsert / BulkInsertTracked, which all go through GetOrCreateCollection.
            // Issue #59: pre-fix CreateIndex was the odd one out, forcing the bootstrap
            // pattern to call GetOrCreateCollection on every collection just to satisfy
            // the index DDL that follows.
            var collection = _catalog.GetOrCreateCollection(collectionName);

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
            CommitDurableLocked();
        }
        finally
        {
            _transactionManager.ReleaseWriteLock();
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

    /// <summary>
    /// Open a multi-document transaction. Stage writes via the returned handle's
    /// Insert/Replace/Delete/DeleteByField methods, then call Commit() to apply
    /// them atomically (or Dispose without committing to roll back).
    /// </summary>
    public Transaction BeginTransaction()
    {
        ThrowIfReadOnly();
        var id = _transactionManager.NextTransactionId();
        return new Transaction(id, _transactionManager, this);
    }

    // --- 2PC participant API (issue #14 Phase B) ---
    //
    // These are the wire-level operations the cluster coordinator drives:
    // Prepare validates + stages + holds the write lock; CommitPrepared
    // applies + releases; RollbackPrepared releases without applying. The
    // hot non-tx paths above don't go through any of this — the worker
    // thread that owns the lock during PREPARED state isn't even running
    // unless something has called Prepare.

    private PreparedTxLog? _preparedTxLog;
    private PreparedTxCoordinator? _preparedTxCoordinator;
    private readonly object _preparedTxInitLock = new();
    private readonly PreparedTxStats _preparedTxStats = new();

    // Phase C.1 — coordinator decision log. Only non-null on a shard that's
    // actually been asked to coordinate a cluster tx; lazy-initialized on
    // first decision write. The same DB can be both a participant (uses
    // _preparedTxLog) and a coordinator (uses _coordinatorTxLog) for
    // different transactions.
    private CoordinatorTxLog? _coordinatorTxLog;
    private readonly object _coordinatorTxInitLock = new();

    private PreparedTxCoordinator EnsurePreparedTxCoordinator()
    {
        if (_preparedTxCoordinator is not null) return _preparedTxCoordinator;
        lock (_preparedTxInitLock)
        {
            if (_preparedTxCoordinator is not null) return _preparedTxCoordinator;
            ThrowIfReadOnly();
            _preparedTxLog = new PreparedTxLog(FilePath + ".prepared.log");
            _preparedTxCoordinator = new PreparedTxCoordinator(this, _transactionManager, _preparedTxLog, _preparedTxStats);
            return _preparedTxCoordinator;
        }
    }

    /// <summary>
    /// Phase 1 of 2PC. Validate the staged ops against the current state,
    /// persist them to <c>{db}.prepared.log</c>, and hold the write lock
    /// until <see cref="CommitPreparedTransaction"/> or
    /// <see cref="RollbackPreparedTransaction"/> arrives. Returns
    /// <see cref="PrepareVote.Prepared"/> on success or
    /// <see cref="PrepareVote.Aborted"/> with a reason on conflict.
    ///
    /// <para>
    /// If <paramref name="timeout"/> elapses before the resolve arrives,
    /// the participant self-aborts (Phase D). Default overload uses 30s.
    /// </para>
    /// </summary>
    public PrepareResult PrepareTransaction(string txId, string coordinatorShardId, IReadOnlyList<ShardTxOp> ops, TimeSpan timeout)
    {
        ThrowIfReadOnly();
        return EnsurePreparedTxCoordinator().Prepare(txId, coordinatorShardId, ops, timeout);
    }

    /// <summary>Convenience: prepare with the default 30-second timeout.</summary>
    public PrepareResult PrepareTransaction(string txId, string coordinatorShardId, IReadOnlyList<ShardTxOp> ops) =>
        PrepareTransaction(txId, coordinatorShardId, ops, TimeSpan.FromSeconds(30));

    /// <summary>Phase 2 commit: apply the prepared ops and release the lock.</summary>
    public void CommitPreparedTransaction(string txId)
    {
        ThrowIfReadOnly();
        EnsurePreparedTxCoordinator().CommitPrepared(txId);
    }

    /// <summary>Phase 2 rollback: drop the prepared ops and release the lock.</summary>
    public void RollbackPreparedTransaction(string txId)
    {
        ThrowIfReadOnly();
        EnsurePreparedTxCoordinator().RollbackPrepared(txId);
    }

    private CoordinatorTxLog EnsureCoordinatorTxLog()
    {
        if (_coordinatorTxLog is not null) return _coordinatorTxLog;
        lock (_coordinatorTxInitLock)
        {
            if (_coordinatorTxLog is not null) return _coordinatorTxLog;
            ThrowIfReadOnly();
            _coordinatorTxLog = new CoordinatorTxLog(FilePath + ".coord.log");
            return _coordinatorTxLog;
        }
    }

    /// <summary>
    /// Coordinator-shard call: durably record the COMMIT decision (point of
    /// no return). Issued by the cluster after every participant voted
    /// PREPARED; recovery (Phase C.2) reads this on coordinator restart to
    /// know it must replay the broadcast.
    /// </summary>
    public void RecordCoordinatorDecision(string txId, bool commit)
    {
        ThrowIfReadOnly();
        // Phase C.1 only persists COMMIT — ABORT is implicit (no record). If
        // we wanted to recover and finalize aborts deterministically (e.g.
        // an operator-driven abort), we'd add an ABORT record. Out of scope.
        if (commit)
            EnsureCoordinatorTxLog().AppendCommitDecision(txId);
    }

    /// <summary>Coordinator-shard call: every participant ACK'd COMMIT — record DONE.</summary>
    public void RecordCoordinatorDone(string txId)
    {
        ThrowIfReadOnly();
        EnsureCoordinatorTxLog().AppendDone(txId);
    }

    /// <summary>
    /// Snapshot of the coordinator decision log. txId → state. Empty if this
    /// shard never coordinated a cluster tx. Phase C.2 reads this on
    /// recovery to learn whether a tx was decided.
    /// </summary>
    public IReadOnlyDictionary<string, CoordinatorTxState> ScanCoordinatorTransactions()
    {
        if (_coordinatorTxLog is null) return new Dictionary<string, CoordinatorTxState>();
        return _coordinatorTxLog.Scan();
    }

    /// <summary>
    /// Snapshot of the participant prepared-tx log: every PREPARED record
    /// without a matching RESOLVED record. Phase C.2 calls this on each
    /// shard at recovery time to find tx slices that need resolution.
    /// </summary>
    public IReadOnlyList<PreparedTxRecord> ScanInFlightPreparedTransactions()
    {
        // The log is created lazily on first PREPARE. If it doesn't exist
        // yet, we open it transiently to scan whatever's on disk from a
        // previous process — without taking ownership for the lifetime of
        // this DB instance (recovery shouldn't accidentally start a worker
        // thread). On a freshly-created DB the file just doesn't exist
        // and we return empty.
        var path = FilePath + ".prepared.log";
        if (_preparedTxLog is not null) return _preparedTxLog.ScanInFlight();
        if (!File.Exists(path)) return Array.Empty<PreparedTxRecord>();
        using var transient = new PreparedTxLog(path);
        return transient.ScanInFlight();
    }

    /// <summary>
    /// Snapshot of the participant-side 2PC counters for this shard.
    /// Returns zero-valued stats on a DB that has never participated in
    /// a prepared tx. Phase E.2 — fed by the HTTP <c>GET /tx/stats</c>
    /// endpoint for operator dashboards.
    /// </summary>
    public PreparedTxStats GetPreparedTxStats() => _preparedTxStats.Snapshot();

    /// <summary>
    /// Coordinator-side: what does this shard's coord log say about
    /// <paramref name="txId"/>? Returns null if absent (which the cluster
    /// recovery treats as "abort").
    /// </summary>
    public CoordinatorTxState? GetCoordinatorTxState(string txId)
    {
        var path = FilePath + ".coord.log";
        if (_coordinatorTxLog is not null)
        {
            var states = _coordinatorTxLog.Scan();
            return states.TryGetValue(txId, out var s) ? s : null;
        }
        if (!File.Exists(path)) return null;
        using var transient = new CoordinatorTxLog(path);
        var transientStates = transient.Scan();
        return transientStates.TryGetValue(txId, out var ts) ? ts : null;
    }

    /// <summary>
    /// Convert wire-level <see cref="ShardTxOp"/>s into the working-set
    /// shape the engine's apply/validate path consumes. Visible only to
    /// <see cref="PreparedTxCoordinator"/>; called while it holds the
    /// write lock so the per-collection scans for DeleteByField are safe.
    /// </summary>
    internal Dictionary<string, CollectionWorkingSet> BuildWorkingSetsFromShardTxOps(IReadOnlyList<ShardTxOp> ops)
    {
        var result = new Dictionary<string, CollectionWorkingSet>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in ops)
        {
            if (!result.TryGetValue(op.Collection, out var ws))
            {
                ws = new CollectionWorkingSet(op.Collection);
                result[op.Collection] = ws;
            }
            switch (op.Kind)
            {
                case ShardTxOpKind.Insert:
                    op.Doc!.EnsureId();
                    ws.Inserts[op.Doc.GetId()] = op.Doc;
                    break;
                case ShardTxOpKind.Replace:
                    op.Doc!.SetId(op.DocId);
                    ws.Replaces[op.DocId] = op.Doc;
                    break;
                case ShardTxOpKind.DeleteByField:
                    var coll = _catalog.GetCollection(op.Collection);
                    if (coll is null) break;
                    var target = BsonValue.FromString(op.Value!);
                    foreach (var doc in coll.FindAll())
                    {
                        var actual = JsonPathExtractor.Extract(doc, op.Field!);
                        if (!actual.IsNull && actual.CompareTo(target) == 0)
                            ws.Deletes.Add(doc.GetId());
                    }
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported ShardTxOpKind {op.Kind}");
            }
        }
        return result;
    }

    // --- ITransactionScope ---
    // Callbacks the Transaction handle uses to read live state and commit
    // a fully-staged batch under the engine's write lock.

    BsonDocument? ITransactionScope.FindById(string collection, DocumentId id)
    {
        var coll = _catalog.GetCollection(collection);
        return coll?.FindById(id);
    }

    IEnumerable<BsonDocument> ITransactionScope.FindAll(string collection)
    {
        var coll = _catalog.GetCollection(collection);
        return coll is null ? Array.Empty<BsonDocument>() : coll.FindAll();
    }

    void ITransactionScope.ApplyCommit(Transaction tx)
    {
        ThrowIfReadOnly();
        _transactionManager.AcquireWriteLock();
        try
        {
            ApplyTransactionLocked(tx);
            // Issue #91 — the whole working set was applied above; sealing it
            // as ONE marker-delimited batch makes the transaction crash-atomic:
            // replay either applies every page of the tx or none of them.
            CommitDurableLocked();
        }
        finally
        {
            _transactionManager.ReleaseWriteLock();
        }
    }

    /// <summary>
    /// Commits a staged transaction. The caller must hold the write lock.
    ///
    /// Two phases:
    /// <list type="number">
    ///   <item><description>Validate every unique-index constraint against the
    ///   simulated post-commit state — pending deletes already gone, pending
    ///   replaces using new values, pending inserts adding their keys. If any
    ///   conflict surfaces, throw before touching storage.</description></item>
    ///   <item><description>Apply the working set: deletes, then replaces, then
    ///   inserts. By construction nothing in step 2 can throw a conflict that
    ///   wasn't caught in step 1.</description></item>
    /// </list>
    ///
    /// A non-uniqueness failure during step 2 (e.g. a doc was concurrently
    /// removed between the txn's last read and the commit) leaves the state
    /// partially applied. We don't roll back from there in this release —
    /// callers see the throw and can re-check the state. See the Phase-2
    /// crash-atomicity tracking issue for the durable rollback story.
    /// </summary>
    private void ApplyTransactionLocked(Transaction tx) =>
        ApplyWorkingSetsLocked(tx.WorkingSets);

    /// <summary>
    /// Apply a fully-staged set of per-collection working sets under the
    /// caller-held write lock. Same atomicity contract as the public
    /// <see cref="Transaction"/> path — validation runs first, the apply is
    /// best-effort reversible on a non-uniqueness failure, and followers see
    /// a single TxBatch broadcast at the end.
    ///
    /// <para>
    /// Issue #14 Phase B factored this out of <c>ApplyTransactionLocked</c>
    /// so the participant-side prepared-tx flow can call it with working
    /// sets built from <see cref="ShardTxOp"/> instead of from a Transaction
    /// handle. Both callers expect the lock to be held.
    /// </para>
    /// </summary>
    internal void ApplyWorkingSetsLocked(IReadOnlyDictionary<string, CollectionWorkingSet> workingSets)
    {
        ValidateUniqueIndexesForWorkingSets(workingSets);

        // Issue #23: snapshot every doc we touch so a non-uniqueness failure
        // mid-Apply (IOException, an unexpected throw from Collection.Insert,
        // etc.) can be reverse-applied. Without this the partial state stays
        // committed and the caller has no clean way to recover short of
        // Dispose+Open. Best-effort by design: if the reverse itself throws
        // (engine probably already in Failed state via #25), we re-throw the
        // original error and let the caller see both.
        var applied = new List<AppliedOp>();

        try
        {
            foreach (var (collectionName, ws) in workingSets)
            {
                var collection = _catalog.GetCollection(collectionName);
                if (collection is null)
                {
                    // Pure-insert case: collection doesn't exist yet. Create it.
                    if (ws.Inserts.Count == 0 && ws.Replaces.Count == 0 && ws.Deletes.Count == 0)
                        continue;
                    collection = _catalog.GetOrCreateCollection(collectionName);
                }

                // 1. Deletes
                foreach (var id in ws.Deletes)
                {
                    var doc = collection.FindById(id);
                    if (doc is null) continue; // raced; nothing to undo
                    if (collection.Delete(id))
                    {
                        _indexManager.OnDocumentDeleted(collectionName, id, doc);
                        applied.Add(new AppliedOp(collectionName, AppliedOpKind.Delete, id, doc, null));
                    }
                }

                // 2. Replaces
                foreach (var (id, newDoc) in ws.Replaces)
                {
                    var oldDoc = collection.FindById(id);
                    if (oldDoc is null) continue;
                    newDoc["_id"] = oldDoc["_id"];
                    _indexManager.OnDocumentUpdated(collectionName, id, oldDoc, newDoc);
                    collection.Update(id, newDoc);
                    applied.Add(new AppliedOp(collectionName, AppliedOpKind.Replace, id, oldDoc, newDoc));
                }

                // 3. Inserts. Uniqueness was pre-validated for the whole batch;
                // ValidateUniqueInsert runs again here as a defence-in-depth check
                // since collection.Insert is shared with non-txn paths and we'd
                // rather throw than silently corrupt.
                foreach (var (_, doc) in ws.Inserts)
                {
                    _indexManager.ValidateUniqueInsert(collectionName, doc);
                    var newId = collection.Insert(doc);
                    _indexManager.OnDocumentInserted(collectionName, newId, doc);
                    applied.Add(new AppliedOp(collectionName, AppliedOpKind.Insert, newId, null, doc));
                }
            }
        }
        catch
        {
            // Best-effort reverse-apply in REVERSE order. Each reverse step is
            // independently try/catch'd so one failure doesn't prevent the
            // others from running. If everything reverses cleanly the engine
            // is back at the pre-tx state. If any reverse throws (e.g.
            // IOException — engine already Failed via #25), the partial state
            // remains; the caller sees the original throw and knows to
            // Dispose+Open for proper recovery.
            //
            // NOTE: this does NOT survive a process crash mid-Apply — true
            // crash atomicity needs a tx commit log (TODO file as Phase 3).
            // Within a running process, this is the difference between
            // "atomicity at the BsonDoc level" and "no atomicity at all".
            for (int i = applied.Count - 1; i >= 0; i--)
            {
                var op = applied[i];
                try
                {
                    var collection = _catalog.GetCollection(op.Collection);
                    if (collection is null) continue;
                    switch (op.Kind)
                    {
                        case AppliedOpKind.Delete:
                            // Re-insert the doc we deleted; Collection.Insert
                            // preserves the existing _id via EnsureId().
                            collection.Insert(op.OldDoc!);
                            _indexManager.OnDocumentInserted(op.Collection, op.Id, op.OldDoc!);
                            break;
                        case AppliedOpKind.Replace:
                            // Restore the old doc body.
                            _indexManager.OnDocumentUpdated(op.Collection, op.Id, op.NewDoc!, op.OldDoc!);
                            collection.Update(op.Id, op.OldDoc!);
                            break;
                        case AppliedOpKind.Insert:
                            // Delete the doc we inserted.
                            if (collection.Delete(op.Id))
                                _indexManager.OnDocumentDeleted(op.Collection, op.Id, op.NewDoc!);
                            break;
                    }
                }
                catch { /* best effort; on failure rest of reverse continues */ }
            }
            throw;
        }

        // Issue #13: replicate the transaction as a single TxBatch so
        // followers apply all sub-ops under one write lock and can never
        // observe a partial mid-tx state. Pre-fix this loop broadcast each
        // op individually — followers applied them with separate locks, so
        // a read on the follower between two ops of the same tx saw a
        // half-committed state (e.g. the delete of a delete-then-insert
        // upsert briefly visible).
        if (_logicalServer is not null)
        {
            var subOps = new List<TxBatchPayload.SubOp>();
            foreach (var (collectionName, ws) in workingSets)
            {
                // Order matches the apply order so followers see the same
                // semantics: deletes, replaces, inserts.
                foreach (var id in ws.Deletes)
                {
                    var idBytes = System.Text.Encoding.UTF8.GetBytes(id.ToString());
                    subOps.Add(new TxBatchPayload.SubOp(LogicalOpType.Delete, collectionName, idBytes));
                }
                foreach (var (_, newDoc) in ws.Replaces)
                {
                    var bytes = BsonSerializer.Serialize(newDoc);
                    subOps.Add(new TxBatchPayload.SubOp(LogicalOpType.Insert, collectionName, bytes));
                }
                foreach (var (_, doc) in ws.Inserts)
                {
                    var bytes = BsonSerializer.Serialize(doc);
                    subOps.Add(new TxBatchPayload.SubOp(LogicalOpType.Insert, collectionName, bytes));
                }
            }
            if (subOps.Count > 0)
            {
                var payload = TxBatchPayload.Serialize(subOps);
                // Empty collection field — TxBatch is engine-wide, not
                // per-collection. The follower routes each sub-op by its own
                // Collection field.
                _logicalServer.BroadcastNewOp(LogicalOpType.TxBatch, "", payload);
            }
        }
    }

    /// <summary>
    /// One step of work that <see cref="ApplyTransactionLocked"/> already
    /// applied; recorded so a subsequent failure can reverse it. Replace
    /// carries both old and new bodies; Delete carries only the old body
    /// (so we can re-insert it); Insert carries only the new body (so we
    /// can re-derive index entries to remove). Issue #23.
    /// </summary>
    private enum AppliedOpKind { Insert, Replace, Delete }

    private sealed record AppliedOp(
        string Collection,
        AppliedOpKind Kind,
        DocumentId Id,
        BsonDocument? OldDoc,
        BsonDocument? NewDoc);

    /// <summary>
    /// Pre-flight check that every unique index in every touched collection
    /// will be consistent after the txn applies. Builds the simulated
    /// post-commit set of (key → docId) for each unique index by walking the
    /// current persistent entries plus the txn's deltas, and throws a
    /// <see cref="DuplicateKeyException"/> on the first key that ends up
    /// pointing at two distinct doc ids.
    /// </summary>
    private void ValidateUniqueIndexesForTx(Transaction tx) =>
        ValidateUniqueIndexesForWorkingSets(tx.WorkingSets);

    /// <summary>
    /// Working-set-shaped overload so the Phase B prepared-tx path can
    /// validate without holding a Transaction handle. Identical semantics
    /// — just takes the working sets directly.
    /// </summary>
    internal void ValidateUniqueIndexesForWorkingSets(IReadOnlyDictionary<string, CollectionWorkingSet> workingSets)
    {
        foreach (var (collectionName, ws) in workingSets)
        {
            var collection = _catalog.GetCollection(collectionName);
            foreach (var index in _indexManager.GetIndexes(collectionName))
            {
                if (!index.Definition.IsUnique) continue;

                // Snapshot the live (key, docId) entries for this unique index.
                // Each unique index has at most one docId per key, so a Dictionary
                // is a faithful simulation surface.
                var simulated = new Dictionary<IndexKey, DocumentId>();
                foreach (var (key, docId) in index.ScanAll())
                    simulated[key] = docId;

                // Apply pending deletes: drop their keys from the simulation.
                foreach (var id in ws.Deletes)
                {
                    var doc = collection?.FindById(id);
                    if (doc is null) continue;
                    foreach (var k in KeysFor(doc, index))
                        if (simulated.TryGetValue(k, out var owner) && owner.Equals(id))
                            simulated.Remove(k);
                }

                // Apply pending replaces: drop old keys, add new keys.
                foreach (var (id, newDoc) in ws.Replaces)
                {
                    var oldDoc = collection?.FindById(id);
                    if (oldDoc is not null)
                    {
                        foreach (var k in KeysFor(oldDoc, index))
                            if (simulated.TryGetValue(k, out var owner) && owner.Equals(id))
                                simulated.Remove(k);
                    }
                    foreach (var k in KeysFor(newDoc, index))
                    {
                        if (simulated.TryGetValue(k, out var existing) && !existing.Equals(id))
                            throw new DuplicateKeyException(index.Definition.Name, k.ToString());
                        simulated[k] = id;
                    }
                }

                // Apply pending inserts: their keys must not already be claimed
                // by a doc the txn isn't replacing.
                foreach (var (id, doc) in ws.Inserts)
                {
                    foreach (var k in KeysFor(doc, index))
                    {
                        if (simulated.TryGetValue(k, out var existing) && !existing.Equals(id))
                            throw new DuplicateKeyException(index.Definition.Name, k.ToString());
                        simulated[k] = id;
                    }
                }
            }
        }
    }

    private static IEnumerable<IndexKey> KeysFor(BsonDocument doc, BTreeIndex index)
    {
        if (index.Definition.IsComposite)
        {
            var components = new BsonValue[index.Definition.Paths.Count];
            for (int i = 0; i < index.Definition.Paths.Count; i++)
            {
                var v = JsonPathExtractor.Extract(doc, index.Definition.Paths[i]);
                if (v.IsNull) yield break;
                components[i] = v;
            }
            yield return new IndexKey(components);
        }
        else
        {
            foreach (var v in JsonPathExtractor.ExtractAll(doc, index.Definition.JsonPath))
            {
                if (!v.IsNull) yield return new IndexKey(v);
            }
        }
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
        // Issue #20: provide a snapshot when a fresh follower needs to
        // bootstrap. We reuse db.Snapshot from #27 — same atomic semantics
        // (write lock, flush, copy). The snapshot path is a sibling of the
        // data file and gets cleaned up by the leader after streaming.
        _logicalServer.SnapshotProvider = () =>
        {
            var snapPath = Path.Combine(
                Path.GetDirectoryName(FilePath) ?? ".",
                $"{Path.GetFileName(FilePath)}.snapshot.{Guid.NewGuid():N}");
            // Snapshot under the engine's write lock; capture CurrentSeq
            // in the same lock window so the follower starts from a
            // consistent (snapshot, seq) pair.
            ulong seqAtSnapshot;
            _transactionManager.AcquireWriteLock();
            try
            {
                _pageCache.FlushAll();
                File.Copy(FilePath, snapPath, overwrite: true);
                seqAtSnapshot = _logicalServer?.CurrentSeq ?? 0;
            }
            finally { _transactionManager.ReleaseWriteLock(); }
            return (snapPath, seqAtSnapshot);
        };
        _logicalServer.Start();
    }

    public int GetLogicalFollowerCount() => _logicalServer?.FollowerCount ?? 0;

    /// <summary>
    /// Snapshot of currently-connected followers as the leader sees them.
    /// Surfaces in <c>/replication/status</c> so admin UIs can wire up topology
    /// without operators hand-typing shard membership. Empty list if this node
    /// isn't a leader (or has no followers).
    /// </summary>
    public IReadOnlyList<FollowerInfo> GetLogicalFollowers() =>
        _logicalServer?.GetFollowers() ?? Array.Empty<FollowerInfo>();

    /// <summary>The leader this follower is reading from as <c>"host:port"</c>,
    /// or null if this node isn't a follower.</summary>
    public string? LogicalFollowerLeaderEndpoint => _logicalFollower?.LeaderEndpoint;

    /// <summary>True if this DB is currently acting as a replication leader
    /// (TCP listener accepting follower connections). Distinct from
    /// <see cref="GetLogicalFollowerCount"/> which returns 0 both for
    /// "leader with no followers yet" and "not a leader at all".</summary>
    public bool IsLogicalLeader => _logicalServer is not null;

    /// <summary>TCP port this DB is listening on as a replication leader,
    /// or null when not a leader. Studio's topology view uses this to
    /// match a follower's leader-endpoint ("host:port") back to the DB
    /// that's leading them when both live in the same service.</summary>
    public int? LogicalLeaderPort => _logicalServer?.Port;

    /// <summary>True if this DB is currently acting as a replication follower
    /// (connected to a leader and applying incoming ops).</summary>
    public bool IsLogicalFollower => _logicalFollower is not null;

    /// <summary>"leader" | "follower" | "none" — derived from live engine state,
    /// not from startup config. Issue #66 Phase 2.5 needs this for the
    /// per-DB <c>/db/{name}/replication/status</c> endpoint, where each
    /// attached DB has its own dynamically-assignable role.</summary>
    public string LogicalReplicationRole =>
        IsLogicalLeader ? "leader" : IsLogicalFollower ? "follower" : "none";

    /// <summary>
    /// Start this DB as a logical replication follower (read-only replica).
    /// Applies incoming ops through the engine's own Insert/Delete/CreateIndex so indexes
    /// and location maps stay coherent. Queries on this instance will see replicated data.
    /// </summary>
    public void StartLogicalReplicationFollower(string host, int port, string? sharedSecret = null, string? ownHttpEndpoint = null)
    {
        if (_logicalFollower is not null)
            throw new DocumentForgeException("Logical replication follower already running.");

        var seqFilePath = FilePath + ".followerseq";
        var snapshotPath = FilePath + ".snapshot.incoming";
        _logicalFollower = new LogicalReplicationFollower(host, port, seqFilePath, op =>
        {
            ApplyFollowerOp(op);
        }, secret: sharedSecret, ownHttpEndpoint: ownHttpEndpoint)
        {
            // Issue #20: opt in to snapshot transfer. The leader streams a
            // full data file when this follower can't catch up via the OpLog
            // (fresh follower, or one that's been down longer than the buffer
            // capacity). Bytes land at this path; the next Open of the data
            // file integrates by detecting the sibling .snapshot.incoming.seq
            // marker.
            SnapshotTempPath = snapshotPath,
        };
        _logicalFollower.Start();
    }

    private void ApplyFollowerOp(LogicalOp op)
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
            case LogicalOpType.TxBatch:
                ApplyTxBatchOnFollower(op.Data);
                break;
        }
    }

    /// <summary>
    /// Apply a TxBatch's serialized sub-ops atomically — single write lock,
    /// all sub-ops, then release. Issue #13. Pre-fix the leader broadcast each
    /// sub-op as a separate LogicalOp, the follower applied each under its own
    /// lock, and a read on the follower between sub-ops saw a mid-tx state.
    /// </summary>
    private void ApplyTxBatchOnFollower(byte[] payload)
    {
        var subOps = TxBatchPayload.Deserialize(payload);
        _transactionManager.AcquireWriteLock();
        try
        {
            foreach (var sub in subOps)
            {
                switch (sub.OpType)
                {
                    case LogicalOpType.Insert:
                        var doc = BsonSerializer.Deserialize(sub.Data);
                        // Bypass the public Insert/Delete (which would re-acquire
                        // the lock); use the locked-internal helpers.
                        InsertOnFollowerLocked(sub.Collection, doc);
                        break;
                    case LogicalOpType.Delete:
                        var docId = DocumentId.FromBytes(sub.Data);
                        DeleteOnFollowerLocked(sub.Collection, docId);
                        break;
                    // CreateIndex / DropIndex aren't expected in tx batches today
                    // (transactions only stage Insert/Replace/Delete) but the
                    // dispatch is here for future symmetry.
                }
            }
            CommitDurableLocked();
        }
        finally
        {
            _transactionManager.ReleaseWriteLock();
        }
    }

    private void InsertOnFollowerLocked(string collectionName, BsonDocument doc)
    {
        var collection = _catalog.GetOrCreateCollection(collectionName);
        var id = collection.Insert(doc);
        _indexManager.OnDocumentInserted(collectionName, id, doc);
    }

    private void DeleteOnFollowerLocked(string collectionName, DocumentId docId)
    {
        var collection = _catalog.GetCollection(collectionName);
        if (collection is null) return;
        var doc = collection.FindById(docId);
        if (doc is null) return;
        collection.Delete(docId);
        _indexManager.OnDocumentDeleted(collectionName, docId, doc);
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
            CommitDurableLocked();
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
            CommitDurableLocked();
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
        }
        finally
        {
            _transactionManager.ReleaseWriteLock();
        }
    }

    public void Flush()
    {
        // Health check is intentionally NOT here: a Failed engine should still
        // be Flushable (the recovery log replay on next Open is the path back
        // to Healthy, but until then a manual Flush attempt is allowed and
        // simply re-throws if the underlying I/O is still broken). Wrap in
        // TrackHealth so a fresh failure flips the state.
        //
        // Issues #89/#90: taken under the write lock — FlushAll truncates the
        // recovery log, and a concurrent commit appending records across that
        // truncation would lose its durability record.
        _transactionManager.AcquireWriteLock();
        try
        {
            TrackHealth(() => _pageCache.FlushAll());
        }
        finally
        {
            _transactionManager.ReleaseWriteLock();
        }
    }

    /// <summary>
    /// Issues #89/#90 — the commit point of every write path. Seals the
    /// pages the operation dirtied into the recovery log with a fsync'd
    /// commit marker; on return the operation survives power loss (replay
    /// on the next Open applies it). Called as the LAST step of each write
    /// operation's success path, while the write lock is still held, so an
    /// operation is never acknowledged before it is durable. No-op when
    /// DurableCommits or EnableWal is off.
    /// </summary>
    // When the WAL passes this size, the next commit checkpoints (FlushAll)
    // to fold its records into the data file and truncate it. Without this a
    // low-write server with no manual Flush would grow the .recovery file
    // forever — the flip side of making every commit append to it (#89).
    private const long WalCheckpointThresholdBytes = 64L * 1024 * 1024;

    internal void CommitDurableLocked()
    {
        if (!_durableCommits) return;
        _pageCache.FlushCommit();
        // Bound WAL growth. FlushAll runs under the write lock we already
        // hold, writes dirty pages to the data file, fsyncs, and truncates
        // the log — a self-checkpoint that keeps recovery-replay time and
        // disk use bounded regardless of flush cadence.
        if ((_recoveryLog?.Length ?? 0) > WalCheckpointThresholdBytes)
            _pageCache.FlushAll();
    }

    /// <summary>
    /// Take a consistent on-disk snapshot of the database to <paramref name="targetPath"/>.
    /// Acquires the write lock briefly: flushes every dirty page, fsyncs, then
    /// copies the data file. New writes are blocked for the duration of the
    /// flush + copy and resume on return.
    ///
    /// <para>
    /// Result file is a self-contained <c>.dfdb</c> that <see cref="Open"/> can
    /// load directly. The recovery log and WAL are NOT copied — the snapshot
    /// is always taken at a checkpointed boundary, so the data file alone is
    /// authoritative. Operators restoring from the snapshot just point a new
    /// node at it; no recovery dance.
    /// </para>
    ///
    /// <para>
    /// For a multi-GB dataset the copy itself dominates the wall time. If the
    /// pause is unacceptable, run the snapshot against a follower node — the
    /// follower's pause doesn't affect the leader's write throughput.
    /// </para>
    /// </summary>
    public void Snapshot(string targetPath)
    {
        if (string.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(FilePath),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Snapshot targetPath must differ from the live data file.");

        _transactionManager.AcquireWriteLock();
        try
        {
            // FlushAll fsyncs the data file via OnAfterFlushComplete and
            // truncates the recovery log. After this returns, the on-disk
            // data file alone reflects the full committed state.
            _pageCache.FlushAll();

            // Copy under the write lock so no concurrent writer can race in
            // between flush and copy. File.Copy on .NET reads with FileShare
            // semantics that match a normal read — we still hold the data
            // file's own handle, but Copy goes through a separate read path.
            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);
            File.Copy(FilePath, targetPath, overwrite: true);
        }
        finally
        {
            _transactionManager.ReleaseWriteLock();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        // Critical-cleanup discipline: the on-disk lock and the data file
        // handle MUST be released even if FlushAll throws (full disk, fsync
        // failure, injected fault). Pre-fix a FlushAll throw skipped every
        // subsequent step including _lock.Dispose, leaving the lock file
        // held by this process — the next Open in the same process couldn't
        // reclaim it (FileShare.None) and threw DatabaseLockedException.
        // Discovered by the crash-injection harness (issue #28).
        //
        // Surfacing the FlushAll error to the caller is still the right
        // behaviour (silent durability loss is the worst outcome), so we
        // re-throw at the end. But cleanup runs unconditionally first.
        Exception? deferredFlushError = null;
        try
        {
            DisableAutoFailover();
            try { _replicationServer?.Dispose(); } catch { }
            try { _replicationFollower?.Dispose(); } catch { }
            try { _logicalServer?.Dispose(); } catch { }
            try { _logicalFollower?.Dispose(); } catch { }
            // Stop the prepared-tx worker thread + close its log before the
            // page cache flush. Any in-flight prepared tx that hasn't been
            // resolved gets its lock released by the worker on shutdown
            // (Phase C will replay it on next Open).
            try { _preparedTxCoordinator?.Dispose(); } catch { }
            try { _preparedTxLog?.Dispose(); } catch { }
            try { _coordinatorTxLog?.Dispose(); } catch { }
            try { _pageCache.FlushAll(); } // also truncates the recovery log
            catch (Exception ex) { deferredFlushError = ex; }
            try { _recoveryLog?.Dispose(); } catch { }
        }
        finally
        {
            // The handles below are load-bearing for the next Open. Always
            // close them, even when something earlier threw.
            try { _dataFile.Dispose(); } catch { }
            var recoveryPath = FilePath + ".recovery";
            // <= 8 bytes = just the v2 file header, i.e. no records — semantically empty.
            try { if (File.Exists(recoveryPath) && new FileInfo(recoveryPath).Length <= 8) File.Delete(recoveryPath); } catch { }
            // Release the on-disk lock LAST so a clean shutdown still has
            // the file visible up through the data-file close. Releasing
            // earlier would briefly let a second opener race in while we're
            // still closing.
            try { _lock.Dispose(); } catch { }
            _disposed = true;
        }

        if (deferredFlushError is not null)
            throw deferredFlushError;
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

/// <summary>
/// Result of <see cref="DocumentForgeDb.BulkInsertTracked"/>. Always reports
/// per-doc successes (their assigned <see cref="InsertedIds"/>) and per-doc
/// failures (<see cref="Errors"/>). When the call ran with <c>atomic=true</c>
/// and any error occurred, <see cref="RolledBack"/> is true and
/// <see cref="InsertedIds"/> is empty.
/// </summary>
public sealed record BulkInsertResult(
    IReadOnlyList<Core.DocumentId> InsertedIds,
    IReadOnlyList<BulkInsertError> Errors,
    bool RolledBack);

public sealed record BulkInsertError(int Index, string Error);
