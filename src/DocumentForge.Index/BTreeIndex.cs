using DocumentForge.Core;

namespace DocumentForge.Index;

/// <summary>
/// In-memory B-tree index. Provides O(log n) lookups and range scans.
/// Backed by SortedDictionary (red-black tree) so Insert/Delete are O(log n)
/// at any scale — SortedList would be O(n) because of array shifts.
/// </summary>
public sealed class BTreeIndex
{
    private readonly SortedDictionary<IndexKey, List<DocumentId>> _tree;
    private IndexStorage? _storage;
    private bool _bulkMode;  // when true, Insert/Delete skip disk writes

    public IndexDefinition Definition { get; }

    public int Count => _tree.Count;
    public int TotalEntries => _tree.Values.Sum(v => v.Count);

    public BTreeIndex(IndexDefinition definition)
    {
        Definition = definition;
        _tree = new SortedDictionary<IndexKey, List<DocumentId>>(Comparer<IndexKey>.Default);
    }

    public void AttachStorage(IndexStorage storage) => _storage = storage;

    /// <summary>
    /// Put the index in bulk-load mode. Insert/Delete skip per-entry disk writes.
    /// Call PersistBulk() after the bulk load completes to write everything at once.
    /// </summary>
    public void BeginBulkLoad() => _bulkMode = true;

    /// <summary>
    /// Ends bulk-load mode and writes ALL current in-memory entries to storage in one pass.
    /// </summary>
    public void EndBulkLoad()
    {
        _bulkMode = false;
        if (_storage is null) return;
        // Persist every entry currently in memory
        foreach (var (key, docIds) in _tree)
        {
            foreach (var docId in docIds)
                _storage.AppendEntry(key, docId, isDeleted: false);
        }
    }

    public void Insert(IndexKey key, DocumentId docId)
    {
        if (key.Value.IsNull) return;

        if (Definition.IsUnique && _tree.TryGetValue(key, out var existing) && existing.Count > 0)
        {
            // Self-collision: the sole existing entry already points at the same
            // document. This happens during update flows where the index value is
            // re-asserted (PUT same doc with same indexed value). The right answer
            // is "already correctly indexed, nothing to do" - not an error.
            if (existing.Count == 1 && existing[0].Equals(docId))
                return;

            throw new DuplicateKeyException(Definition.Name, key.ToString());
        }

        if (!_tree.TryGetValue(key, out var list))
        {
            list = new List<DocumentId>(1);
            _tree[key] = list;
        }
        // Idempotency: don't double-insert the same docId at the same key.
        if (list.Contains(docId)) return;
        list.Add(docId);

        if (!_bulkMode)
            _storage?.AppendEntry(key, docId, isDeleted: false);
    }

    public void Delete(IndexKey key, DocumentId docId)
    {
        if (_tree.TryGetValue(key, out var list))
        {
            list.Remove(docId);
            if (list.Count == 0)
                _tree.Remove(key);
        }

        if (!_bulkMode)
            _storage?.AppendEntry(key, docId, isDeleted: true);
    }

    /// <summary>
    /// Load entries directly into the in-memory structure, bypassing disk writes.
    /// Used during initial load from persistent storage.
    /// </summary>
    public void LoadFromStorage(IEnumerable<(IndexKey Key, DocumentId DocId)> entries)
    {
        foreach (var (key, docId) in entries)
        {
            if (key.Value.IsNull) continue;
            if (!_tree.TryGetValue(key, out var list))
            {
                list = new List<DocumentId>(1);
                _tree[key] = list;
            }
            list.Add(docId);
        }
    }

    public IEnumerable<DocumentId> Search(IndexKey key)
    {
        if (_tree.TryGetValue(key, out var list))
            return list;
        return Enumerable.Empty<DocumentId>();
    }

    public IEnumerable<DocumentId> RangeScan(IndexKey? low, IndexKey? high,
        bool inclusiveLow = true, bool inclusiveHigh = true)
    {
        foreach (var kvp in _tree)
        {
            if (low is not null)
            {
                int cmp = kvp.Key.CompareTo(low);
                if (inclusiveLow ? cmp < 0 : cmp <= 0) continue;
            }
            if (high is not null)
            {
                int cmp = kvp.Key.CompareTo(high);
                if (inclusiveHigh ? cmp > 0 : cmp >= 0) break;
            }
            foreach (var docId in kvp.Value)
                yield return docId;
        }
    }

    public IEnumerable<(IndexKey Key, DocumentId DocId)> ScanAll()
    {
        foreach (var kvp in _tree)
        {
            foreach (var docId in kvp.Value)
                yield return (kvp.Key, docId);
        }
    }

    public void Clear() => _tree.Clear();
}
