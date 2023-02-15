using DocumentForge.Core;

namespace DocumentForge.Storage;

public interface IPageCache
{
    byte[] GetPage(PageId pageId);
    void PutPage(PageId pageId, byte[] data, bool isDirty = false);
    void MarkDirty(PageId pageId);
    void FlushAll();
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
}

public sealed class PageCache : IPageCache
{
    private readonly IDataFile _dataFile;
    private readonly int _maxSize;
    private readonly Dictionary<uint, CacheEntry> _entries = new();
    private readonly LinkedList<uint> _lruList = new();
    private readonly object _lock = new();
    private IPreFlushHook? _preFlushHook;

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
                entry.IsDirty = true;
        }
    }

    public void FlushAll()
    {
        lock (_lock)
        {
            // Phase 1: write all dirty pages to the WAL (if attached) and fsync
            if (_preFlushHook is not null)
            {
                foreach (var (pageIdValue, entry) in _entries)
                {
                    if (entry.IsDirty)
                        _preFlushHook.OnBeforeFlush(new PageId(pageIdValue), entry.Data);
                }
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
                    _preFlushHook?.OnBeforeFlush(pageId, entry.Data);
                    _dataFile.WritePage(pageId, entry.Data);
                }
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
                _preFlushHook?.OnBeforeFlush(new PageId(pageId), entry.Data);
                _dataFile.WritePage(new PageId(pageId), entry.Data);
            }
            _entries.Remove(pageId);
        }
        _lruList.RemoveLast();
    }
}
