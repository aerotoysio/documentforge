using DocumentForge.Core;
using DocumentForge.Document;
using DocumentForge.Storage;

namespace DocumentForge.Index;

/// <summary>
/// Manages all indexes across all collections. Intercepts document writes
/// to maintain index consistency. Persists index data to disk when a catalog
/// and storage infrastructure are provided.
/// </summary>
public sealed class IndexManager
{
    private readonly Dictionary<string, List<BTreeIndex>> _indexesByCollection = new();
    private readonly Dictionary<string, BTreeIndex> _indexesByName = new();

    // Persistence infrastructure (may be null for in-memory-only use in tests)
    private readonly IPageCache? _cache;
    private readonly IPageAllocator? _allocator;
    private readonly IndexCatalog? _catalog;

    public IndexManager() { }

    public IndexManager(IPageCache cache, IPageAllocator allocator, IndexCatalog catalog)
    {
        _cache = cache;
        _allocator = allocator;
        _catalog = catalog;
    }

    /// <summary>
    /// Create a new index. Allocates storage pages, builds in-memory, persists to disk.
    /// </summary>
    public BTreeIndex CreateIndex(IndexDefinition definition)
    {
        var key = definition.CollectionName.Value;
        if (!_indexesByCollection.TryGetValue(key, out var list))
        {
            list = new List<BTreeIndex>();
            _indexesByCollection[key] = list;
        }

        if (_indexesByName.ContainsKey(definition.Name))
            throw new DocumentForgeException($"Index '{definition.Name}' already exists.");

        var index = new BTreeIndex(definition);

        // Allocate persistent storage if we have it
        if (_cache is not null && _allocator is not null)
        {
            var storage = IndexStorage.CreateNew(_cache, _allocator);
            definition.RootPage = storage.FirstPage;
            index.AttachStorage(storage);
        }

        list.Add(index);
        _indexesByName[definition.Name] = index;

        // Save catalog
        _catalog?.Save(_indexesByName.Values.Select(i => i.Definition));

        return index;
    }

    /// <summary>
    /// Load indexes from disk based on the catalog's definitions.
    /// Caller provides a function to get the collection for rebuilding if needed.
    /// </summary>
    public void LoadFromCatalog()
    {
        if (_catalog is null || _cache is null || _allocator is null) return;

        _catalog.Load();
        foreach (var def in _catalog.Definitions)
        {
            var index = new BTreeIndex(def);

            if (def.RootPage.IsValid)
            {
                var storage = new IndexStorage(_cache, _allocator, def.RootPage);
                // Load entries from disk into in-memory structure
                index.LoadFromStorage(storage.ReadAllEntries());
                // Attach storage AFTER loading so we don't re-append during load
                index.AttachStorage(storage);
            }

            var collKey = def.CollectionName.Value;
            if (!_indexesByCollection.TryGetValue(collKey, out var list))
            {
                list = new List<BTreeIndex>();
                _indexesByCollection[collKey] = list;
            }
            list.Add(index);
            _indexesByName[def.Name] = index;
        }
    }

    public void DropIndex(string indexName)
    {
        if (!_indexesByName.TryGetValue(indexName, out var index))
            throw new DocumentForgeException($"Index '{indexName}' not found.");

        var key = index.Definition.CollectionName.Value;
        if (_indexesByCollection.TryGetValue(key, out var list))
            list.Remove(index);

        _indexesByName.Remove(indexName);

        _catalog?.Save(_indexesByName.Values.Select(i => i.Definition));
    }

    public IReadOnlyList<BTreeIndex> GetIndexes(string collectionName)
    {
        var key = new CollectionName(collectionName).Value;
        if (_indexesByCollection.TryGetValue(key, out var list))
            return list;
        return Array.Empty<BTreeIndex>();
    }

    public BTreeIndex? GetIndex(string indexName)
    {
        _indexesByName.TryGetValue(indexName, out var index);
        return index;
    }

    public BTreeIndex? FindIndexForPath(string collectionName, string jsonPath)
    {
        var key = new CollectionName(collectionName).Value;
        if (!_indexesByCollection.TryGetValue(key, out var list))
            return null;

        return list.FirstOrDefault(idx => idx.Definition.JsonPath == jsonPath);
    }

    public void OnDocumentInserted(string collectionName, DocumentId docId, BsonDocument doc)
    {
        var key = new CollectionName(collectionName).Value;
        if (!_indexesByCollection.TryGetValue(key, out var indexes))
            return;

        foreach (var index in indexes)
            InsertDocIntoIndex(index, doc, docId);
    }

    public void OnDocumentDeleted(string collectionName, DocumentId docId, BsonDocument doc)
    {
        var key = new CollectionName(collectionName).Value;
        if (!_indexesByCollection.TryGetValue(key, out var indexes))
            return;

        foreach (var index in indexes)
            DeleteDocFromIndex(index, doc, docId);
    }

    private static void InsertDocIntoIndex(BTreeIndex index, BsonDocument doc, DocumentId docId)
    {
        if (index.Definition.IsComposite)
        {
            var indexKey = BuildCompositeKey(doc, index.Definition.Paths);
            if (indexKey is not null) index.Insert(indexKey, docId);
        }
        else
        {
            var values = JsonPathExtractor.ExtractAll(doc, index.Definition.JsonPath);
            foreach (var val in values)
            {
                if (!val.IsNull)
                    index.Insert(new IndexKey(val), docId);
            }
        }
    }

    private static void DeleteDocFromIndex(BTreeIndex index, BsonDocument doc, DocumentId docId)
    {
        if (index.Definition.IsComposite)
        {
            var indexKey = BuildCompositeKey(doc, index.Definition.Paths);
            if (indexKey is not null) index.Delete(indexKey, docId);
        }
        else
        {
            var values = JsonPathExtractor.ExtractAll(doc, index.Definition.JsonPath);
            foreach (var val in values)
            {
                if (!val.IsNull)
                    index.Delete(new IndexKey(val), docId);
            }
        }
    }

    /// <summary>
    /// Build a composite IndexKey by extracting each path from the document.
    /// Returns null if any component is null (we don't index partial composite keys).
    /// </summary>
    private static IndexKey? BuildCompositeKey(BsonDocument doc, IReadOnlyList<string> paths)
    {
        var components = new BsonValue[paths.Count];
        for (int i = 0; i < paths.Count; i++)
        {
            var v = JsonPathExtractor.Extract(doc, paths[i]);
            if (v.IsNull) return null;
            components[i] = v;
        }
        return new IndexKey(components);
    }

    public void OnDocumentUpdated(string collectionName, DocumentId docId,
        BsonDocument oldDoc, BsonDocument newDoc)
    {
        var key = new CollectionName(collectionName).Value;
        if (!_indexesByCollection.TryGetValue(key, out var indexes)) return;

        // Validate first - no mutations until every unique index is happy.
        // For each unique index, simulate the new state (after deleting the old
        // entry) and check whether the new value would collide with a different
        // doc. If yes, throw before any side effects so the caller can roll back.
        foreach (var index in indexes)
        {
            if (!index.Definition.IsUnique) continue;
            ValidateUniqueIndexUpdate(index, docId, oldDoc, newDoc);
        }

        // All validations passed - apply.
        foreach (var index in indexes)
        {
            DeleteDocFromIndex(index, oldDoc, docId);
            InsertDocIntoIndex(index, newDoc, docId);
        }
    }

    /// <summary>
    /// Throws DuplicateKeyException if the new doc's indexed value already exists
    /// in the unique index for a DIFFERENT docId. Same-docId collisions are fine
    /// (handled by BTreeIndex.Insert as a no-op self-collision).
    /// </summary>
    private static void ValidateUniqueIndexUpdate(
        BTreeIndex index, DocumentId docId, BsonDocument oldDoc, BsonDocument newDoc)
    {
        IEnumerable<IndexKey> newKeys;
        if (index.Definition.IsComposite)
        {
            var k = BuildCompositeKey(newDoc, index.Definition.Paths);
            newKeys = k is null ? Array.Empty<IndexKey>() : new[] { k };
        }
        else
        {
            newKeys = JsonPathExtractor
                .ExtractAll(newDoc, index.Definition.JsonPath)
                .Where(v => !v.IsNull)
                .Select(v => new IndexKey(v));
        }

        // Old keys we're about to remove - so any "collision" with one of these
        // is actually self-cleanup, not a real conflict.
        var oldKeys = new HashSet<IndexKey>();
        if (index.Definition.IsComposite)
        {
            var k = BuildCompositeKey(oldDoc, index.Definition.Paths);
            if (k is not null) oldKeys.Add(k);
        }
        else
        {
            foreach (var v in JsonPathExtractor.ExtractAll(oldDoc, index.Definition.JsonPath))
                if (!v.IsNull) oldKeys.Add(new IndexKey(v));
        }

        foreach (var newKey in newKeys)
        {
            // After the delete phase, this key will only have entries that
            // weren't part of the old doc. Look up current entries.
            var entries = index.Search(newKey);
            foreach (var existingDocId in entries)
            {
                // Entry that's about to be removed by the delete phase - ignore.
                if (existingDocId.Equals(docId) && oldKeys.Contains(newKey)) continue;
                // Entry pointing at the same doc we're updating - self-collision, ignore.
                if (existingDocId.Equals(docId)) continue;
                // A genuinely different doc has this key - real conflict.
                throw new DuplicateKeyException(index.Definition.Name, newKey.ToString());
            }
        }
    }

    public void RebuildIndex(BTreeIndex index, Collection collection)
    {
        index.Clear();
        // Bulk mode skips per-entry disk writes; we write everything at once at the end.
        // This is the difference between 50M small I/Os and one sequential pass.
        index.BeginBulkLoad();
        try
        {
            foreach (var (doc, _, _) in collection.IterateDocuments())
            {
                var docId = doc.GetId();
                InsertDocIntoIndex(index, doc, docId);
            }
        }
        finally
        {
            index.EndBulkLoad();
        }
    }
}
