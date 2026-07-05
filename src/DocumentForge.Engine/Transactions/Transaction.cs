using DocumentForge.Core;
using DocumentForge.Document;
using DocumentForge.Index;

namespace DocumentForge.Transactions;

public enum TransactionState
{
    Active,
    Committed,
    RolledBack
}

/// <summary>
/// A multi-document, multi-collection transaction. Stages writes in memory
/// until <see cref="Commit"/>, at which point the engine takes a single
/// write lock and applies them atomically (or throws and applies nothing).
///
/// Reads through the transaction are read-your-writes: staged inserts,
/// replaces, and deletes from this transaction are visible, layered over
/// the live committed state. Reads outside the transaction don't see any
/// staged work until commit succeeds.
///
/// <para>
/// Concurrency: multiple transactions can be open at once; only commits
/// serialize. The first to commit wins. A second commit that targets a doc
/// the first deleted, or that violates a unique index after the first's
/// inserts, throws on commit and changes nothing — callers retry with a
/// fresh transaction.
/// </para>
///
/// <para>
/// Single-node only in this release. <see cref="Engine.DocumentForgeDb"/>
/// rejects <c>BeginTransaction</c> when a cluster wrapper is in play (see
/// the cross-shard 2PC tracking issue for the roadmap).
/// </para>
/// </summary>
public sealed class Transaction : IDisposable
{
    // Engine-side helper that knows how to read collections/indexes and
    // how to apply a fully-validated batch under the write lock. The
    // interface lives in Engine; the Transaction holds it as an opaque
    // token. Tests reach in via internal accessors.
    private readonly ITransactionScope _scope;
    private readonly TransactionManager _manager;

    // Per-collection working set. Empty until the first staging call hits
    // a given collection; built lazily so a no-op transaction allocates
    // nothing past the handle itself.
    private readonly Dictionary<string, CollectionWorkingSet> _workingSets =
        new(StringComparer.OrdinalIgnoreCase);

    public ulong Id { get; }
    public TransactionState State { get; private set; } = TransactionState.Active;

    internal Transaction(ulong id, TransactionManager manager, ITransactionScope scope)
    {
        Id = id;
        _manager = manager;
        _scope = scope;
    }

    private CollectionWorkingSet GetOrCreate(string collection)
    {
        if (!_workingSets.TryGetValue(collection, out var ws))
        {
            ws = new CollectionWorkingSet(collection);
            _workingSets[collection] = ws;
        }
        return ws;
    }

    private void EnsureActive()
    {
        if (State != TransactionState.Active)
            throw new TransactionException($"Transaction {Id} is {State}; no further operations allowed.");
    }

    /// <summary>
    /// Stage an insert. Generates a fresh _id (or honours one in the body).
    /// The doc isn't visible to readers outside the transaction until
    /// <see cref="Commit"/> succeeds.
    /// </summary>
    public DocumentId Insert(string collection, BsonDocument doc)
    {
        EnsureActive();
        doc.EnsureId();
        var id = doc.GetId();
        GetOrCreate(collection).Inserts[id] = doc;
        return id;
    }

    public DocumentId Insert(string collection, string json) =>
        Insert(collection, BsonDocument.FromJson(json));

    /// <summary>
    /// Stage a replace by document _id. Returns false if the doc isn't visible
    /// to this transaction (committed, or staged-inserted in this txn). On
    /// false the working set is unchanged.
    /// </summary>
    public bool Replace(string collection, DocumentId id, BsonDocument newDoc)
    {
        EnsureActive();
        newDoc.SetId(id);
        var ws = GetOrCreate(collection);

        // Replacing a doc this txn just staged for insert: just rewrite the
        // staged doc; nothing to do at commit beyond the original insert.
        if (ws.Inserts.ContainsKey(id))
        {
            ws.Inserts[id] = newDoc;
            return true;
        }

        // If we already staged a delete for this id and now the caller wants
        // to keep it after all, flip back to a replace.
        ws.Deletes.Remove(id);

        // Confirm the doc actually exists in committed state — replacing a
        // ghost is a caller error, not a silent no-op.
        if (_scope.FindById(collection, id) is null)
            return false;

        ws.Replaces[id] = newDoc;
        return true;
    }

    public bool Replace(string collection, DocumentId id, string json) =>
        Replace(collection, id, BsonDocument.FromJson(json));

    /// <summary>
    /// Stage a delete by document _id. Drops a same-id staged insert if there
    /// is one (so the row never existed from the outside). Returns false only
    /// if the id is not visible to this transaction.
    /// </summary>
    public bool Delete(string collection, DocumentId id)
    {
        EnsureActive();
        var ws = GetOrCreate(collection);

        if (ws.Inserts.Remove(id))
            return true;

        if (ws.Replaces.Remove(id))
        {
            ws.Deletes.Add(id);
            return true;
        }

        if (_scope.FindById(collection, id) is null)
            return false;

        ws.Deletes.Add(id);
        return true;
    }

    /// <summary>
    /// Stage deletes for every doc whose <paramref name="field"/> equals
    /// <paramref name="value"/> — both committed docs and same-txn pending
    /// inserts. Mirrors <c>DELETE /collections/{c}/by/{f}/{v}</c> but inside
    /// the transaction. Returns the number of docs scheduled for removal.
    /// </summary>
    public int DeleteByField(string collection, string field, string value)
    {
        EnsureActive();
        var ws = GetOrCreate(collection);
        var target = BsonValue.FromString(value);
        int matched = 0;

        // Drop same-txn staged inserts that match.
        var insertsToRemove = ws.Inserts
            .Where(kvp => MatchesField(kvp.Value, field, target))
            .Select(kvp => kvp.Key)
            .ToList();
        foreach (var id in insertsToRemove)
        {
            ws.Inserts.Remove(id);
            matched++;
        }

        // Walk committed state, accounting for any pending replaces that may
        // have changed the indexed value.
        foreach (var doc in _scope.FindAll(collection))
        {
            var id = doc.GetId();
            if (ws.Deletes.Contains(id)) continue;

            BsonDocument effective = ws.Replaces.TryGetValue(id, out var replaced)
                ? replaced : doc;

            if (!MatchesField(effective, field, target)) continue;

            // Drop the replace if any — the doc is going away regardless.
            ws.Replaces.Remove(id);
            ws.Deletes.Add(id);
            matched++;
        }

        return matched;
    }

    private static bool MatchesField(BsonDocument doc, string field, BsonValue target)
    {
        var actual = JsonPathExtractor.Extract(doc, field);
        return !actual.IsNull && actual.CompareTo(target) == 0;
    }

    /// <summary>
    /// Read by _id, with the transaction's pending state layered over the
    /// committed state. Returns null if the doc has been staged for delete,
    /// doesn't exist, or never existed.
    /// </summary>
    public BsonDocument? Find(string collection, DocumentId id)
    {
        EnsureActive();
        if (_workingSets.TryGetValue(collection, out var ws))
        {
            if (ws.Deletes.Contains(id)) return null;
            if (ws.Replaces.TryGetValue(id, out var rep)) return rep;
            if (ws.Inserts.TryGetValue(id, out var ins)) return ins;
        }
        return _scope.FindById(collection, id);
    }

    public void Commit()
    {
        EnsureActive();
        try
        {
            // ApplyCommit applies the working set AND seals it in the
            // recovery log with a fsync'd commit marker (issues #89/#91) —
            // when it returns, the transaction is durable and crash-atomic.
            _scope.ApplyCommit(this);
            State = TransactionState.Committed;
        }
        catch
        {
            // Apply may throw mid-validation. Nothing has been written yet
            // (validation runs before any storage mutation), so the txn just
            // becomes RolledBack and the caller retries.
            State = TransactionState.RolledBack;
            throw;
        }
    }

    public void Rollback()
    {
        if (State != TransactionState.Active)
            throw new TransactionException($"Cannot rollback transaction {Id}: state is {State}");
        State = TransactionState.RolledBack;
    }

    public void Dispose()
    {
        if (State == TransactionState.Active) Rollback();
    }

    // Engine-internal access. Keeps the public surface narrow.
    internal IReadOnlyDictionary<string, CollectionWorkingSet> WorkingSets => _workingSets;

    /// <summary>Total writes staged across all collections. 0 means a no-op commit.</summary>
    public int StagedOperationCount =>
        _workingSets.Values.Sum(ws => ws.Inserts.Count + ws.Replaces.Count + ws.Deletes.Count);
}

/// <summary>
/// Per-collection slice of a transaction's working set.
/// </summary>
internal sealed class CollectionWorkingSet
{
    public string CollectionName { get; }

    // _id → doc. Insert overrides nothing committed; if an _id is generated
    // here it's guaranteed unique against the committed ID space (NewId()).
    public Dictionary<DocumentId, BsonDocument> Inserts { get; } = new();

    // _id → new doc body. The id must already exist in committed state
    // (Replace() validates before staging). A doc cannot simultaneously be
    // a pending Insert and a pending Replace — Insert wins by virtue of the
    // doc not yet existing in committed state.
    public Dictionary<DocumentId, BsonDocument> Replaces { get; } = new();

    public HashSet<DocumentId> Deletes { get; } = new();

    public CollectionWorkingSet(string name) => CollectionName = name;
}

/// <summary>
/// Engine-side hook the transaction uses to read live state (for
/// FindById/FindAll/DeleteByField staging) and to apply a fully-staged
/// transaction under the engine's write lock. Implemented by the engine;
/// the transaction stays oblivious to PageCache, IndexManager, etc.
/// </summary>
internal interface ITransactionScope
{
    BsonDocument? FindById(string collection, DocumentId id);
    IEnumerable<BsonDocument> FindAll(string collection);
    void ApplyCommit(Transaction tx);
}
