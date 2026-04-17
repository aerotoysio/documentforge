using DocumentForge.Core;

namespace DocumentForge.Index;

/// <summary>
/// In-memory B-tree index. Provides O(log n) lookups and range scans.
/// Rebuilt from collection data on database open. Phase 2 will persist to pages.
/// </summary>
public sealed class BTreeIndex
{
    private readonly SortedList<IndexKey, List<DocumentId>> _tree;
    private IndexStorage? _storage; // null during bulk rebuild, set for normal operations

    public IndexDefinition Definition { get; }

    public int Count => _tree.Count;
    public int TotalEntries => _tree.Values.Sum(v => v.Count);

    public BTreeIndex(IndexDefinition definition)
    {
        Definition = definition;
        _tree = new SortedList<IndexKey, List<DocumentId>>(Comparer<IndexKey>.Default);
    }

    /// <summary>
    /// Attach persistent storage. All subsequent Insert/Delete calls will be written to disk.
    /// </summary>
    public void AttachStorage(IndexStorage storage) => _storage = storage;

    public void Insert(IndexKey key, DocumentId docId)
    {
        if (key.Value.IsNull) return; // don't index null values

        if (Definition.IsUnique && _tree.TryGetValue(key, out var existing) && existing.Count > 0)
        {
            throw new DuplicateKeyException(Definition.Name, key.ToString());
        }

        if (!_tree.TryGetValue(key, out var list))
        {
            list = new List<DocumentId>(1);
            _tree[key] = list;
        }
        list.Add(docId);

        // Persist to disk if storage is attached
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

        // Persist deletion tombstone
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

    public void Clear()
    {
        _tree.Clear();
    }
}
