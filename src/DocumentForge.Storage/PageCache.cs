using DocumentForge.Core;

namespace DocumentForge.Storage;

public interface IPageCache
{
    byte[] GetPage(PageId pageId);
    void PutPage(PageId pageId, byte[] data, bool isDirty = false);
    void MarkDirty(PageId pageId);
    void FlushAll();
    void FlushCommit();
    void Evict(PageId pageId);
    int Count { get; }
    int DirtyCount { get; }
}

/// <summary>
/// Callback fired for each page about to be written to the data file.
/// The callback's implementation (e.g. a WAL writer) can log the page before
/// we commit it to disk, so crashes can be recovered from the WAL.
/// </summary>
public interface IPreFlushHook
{
    void OnBeforeFlush(PageId pageId, byte[] pageData);
    void OnAfterFlushComplete(); // called after all dirty pages written to data file

    /// <summary>
    /// Force the recovery log to fsync without truncating it. Called by Evict
    /// after an unscheduled mid-cache page write so the WAL is durable even
    /// if the data-file write only made it into the OS buffer (crashed before
    /// the next FlushAll). Issue #24 parts 2-3 — pre-fix evictions could lose
    /// committed pages on power loss.
    /// </summary>
    void EnsureLogDurable();

    /// <summary>
    /// Issues #89/#90 — the commit point. Called after a batch of
    /// OnBeforeFlush page captures that together form one committed
    /// operation; the recovery-log hook seals them with a commit marker
    /// and fsyncs. Recovery replay applies only marker-sealed batches, so
    /// this is what makes an acknowledged write durable and a multi-page
    /// commit atomic. Hooks with no durability role (replication,
    /// archiver) treat it as a no-op.
    /// </summary>
    void OnCommitDurable();
}

public sealed class PageCache : IPageCache
{
    private readonly IDataFile _dataFile;
    private readonly int _maxSize;
    private readonly Dictionary<uint, CacheEntry> _entries = new();
    private readonly LinkedList<uint> _lruList = new();
    private readonly object _lock = new();
    private IPreFlushHook? _preFlushHook;

    // Pages dirtied since their images were last captured into the
    // recovery log (issues #89/#90). FlushCommit drains this delta —
    // logging only what the current commit touched keeps write
    // amplification at "pages changed by this op", not "all dirty pages".
    private readonly HashSet<uint> _dirtySinceLog = new();

    public int Count { get { lock (_lock) return _entries.Count; } }
    public int DirtyCount { get { lock (_lock) return _entries.Values.Count(e => e.IsDirty); } }

    /// <summary>
    /// Attach a hook that gets notified before pages are written to the data file.
    /// Used by the recovery log to persist pages to the WAL before the data file.
    /// </summary>
    public void SetPreFlushHook(IPreFlushHook? hook) => _preFlushHook = hook;

    private sealed class CacheEntry
    {
        public byte[] Data;
        public bool IsDirty;
        public LinkedListNode<uint> LruNode;

        public CacheEntry(byte[] data, bool isDirty, LinkedListNode<uint> lruNode)
        {
            Data = data;
            IsDirty = isDirty;
            LruNode = lruNode;
        }
    }

    public PageCache(IDataFile dataFile, int maxSize = 0)
    {
        _dataFile = dataFile;
        _maxSize = maxSize > 0 ? maxSize : Constants.DefaultCacheSize;
    }

    public byte[] GetPage(PageId pageId)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(pageId.Value, out var entry))
            {
                // Move to front of LRU
                _lruList.Remove(entry.LruNode);
                _lruList.AddFirst(entry.LruNode);
                return entry.Data;
            }

            // Cache miss - read from disk
            var data = _dataFile.ReadPage(pageId);
            PutPageInternal(pageId.Value, data, false);
            return data;
        }
    }

    public void PutPage(PageId pageId, byte[] data, bool isDirty = false)
    {
        lock (_lock) PutPageInternal(pageId.Value, data, isDirty);
    }

    private void PutPageInternal(uint pageId, byte[] data, bool isDirty)
    {
        if (isDirty) _dirtySinceLog.Add(pageId);
        if (_entries.TryGetValue(pageId, out var existing))
        {
            existing.Data = data;
            existing.IsDirty = existing.IsDirty || isDirty;
            _lruList.Remove(existing.LruNode);
            _lruList.AddFirst(existing.LruNode);
            return;
        }

        // Evict if needed
        while (_entries.Count >= _maxSize)
            EvictLru();

        var node = _lruList.AddFirst(pageId);
        _entries[pageId] = new CacheEntry(data, isDirty, node);
    }

    public void MarkDirty(PageId pageId)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(pageId.Value, out var entry))
            {
                entry.IsDirty = true;
                _dirtySinceLog.Add(pageId.Value);
            }
        }
    }

    /// <summary>
    /// Issues #89/#90 — the durable commit point for one operation. Captures
    /// the pages dirtied since the last capture into the recovery log via
    /// the hook, then seals them with a fsync'd commit marker
    /// (OnCommitDurable). When this returns, the operation survives a crash:
    /// replay on the next Open applies the sealed batch. The dirty pages
    /// themselves stay in cache for the next FlushAll/eviction — the log is
    /// the durability source, the data file catches up lazily.
    /// Caller must hold the database write lock.
    /// </summary>
    public void FlushCommit()
    {
        lock (_lock)
        {
            if (_preFlushHook is null)
            {
                // No WAL attached (EnableWal=false): nothing to capture into,
                // and the delta must not accumulate forever. FlushAll still
                // persists via the data file.
                _dirtySinceLog.Clear();
                return;
            }
            if (_dirtySinceLog.Count == 0) return;

            bool captured = false;
            foreach (var pageIdValue in _dirtySinceLog)
            {
                // A page can leave the cache between dirtying and commit
                // (eviction) — the eviction path already logged its bytes,
                // and our marker below seals them.
                if (_entries.TryGetValue(pageIdValue, out var entry) && entry.IsDirty)
                {
                    _preFlushHook.OnBeforeFlush(new PageId(pageIdValue), entry.Data);
                    captured = true;
                }
            }
            _dirtySinceLog.Clear();
            if (captured) _preFlushHook.OnCommitDurable();
        }
    }

    public void FlushAll()
    {
        lock (_lock)
        {
            // Phase 1: capture not-yet-logged dirty pages into the WAL and
            // seal them with a commit marker. Pages already logged by a
            // FlushCommit since the last truncate are NOT re-logged — their
            // sealed records are still in the log.
            if (_preFlushHook is not null)
            {
                bool captured = false;
                foreach (var pageIdValue in _dirtySinceLog)
                {
                    if (_entries.TryGetValue(pageIdValue, out var entry) && entry.IsDirty)
                    {
                        _preFlushHook.OnBeforeFlush(new PageId(pageIdValue), entry.Data);
                        captured = true;
                    }
                }
                _dirtySinceLog.Clear();
                if (captured) _preFlushHook.OnCommitDurable();
            }

            // Phase 2: write dirty pages to the data file
            foreach (var (pageIdValue, entry) in _entries)
            {
                if (entry.IsDirty)
                {
                    _dataFile.WritePage(new PageId(pageIdValue), entry.Data);
                    entry.IsDirty = false;
                }
            }
            _dataFile.Flush();

            // Phase 3: data file is durable, we can safely truncate the WAL
            _preFlushHook?.OnAfterFlushComplete();
        }
    }

    public void Evict(PageId pageId)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(pageId.Value, out var entry))
            {
                if (entry.IsDirty)
                {
                    // Issue #24 parts 2-3: fsync the WAL after the data-file
                    // write so a crash before the next FlushAll doesn't lose
                    // the page. The data-file write itself is unfsynced
                    // (intentional — that's still amortised at FlushAll
                    // time), but the WAL holds the canonical bytes; the
                    // owning commit's marker (FlushCommit) seals them for
                    // replay. The page leaves the delta set — its bytes are
                    // in the log already.
                    _preFlushHook?.OnBeforeFlush(pageId, entry.Data);
                    _dataFile.WritePage(pageId, entry.Data);
                    _preFlushHook?.EnsureLogDurable();
                }
                _dirtySinceLog.Remove(pageId.Value);
                _lruList.Remove(entry.LruNode);
                _entries.Remove(pageId.Value);
            }
        }
    }

    private void EvictLru()
    {
        var last = _lruList.Last;
        if (last == null) return;

        var pageId = last.Value;
        if (_entries.TryGetValue(pageId, out var entry))
        {
            if (entry.IsDirty)
            {
                // Same WAL-fsync discipline as Evict — see comment there.
                _preFlushHook?.OnBeforeFlush(new PageId(pageId), entry.Data);
                _dataFile.WritePage(new PageId(pageId), entry.Data);
                _preFlushHook?.EnsureLogDurable();
            }
            _dirtySinceLog.Remove(pageId);
            _entries.Remove(pageId);
        }
        _lruList.RemoveLast();
    }
}
