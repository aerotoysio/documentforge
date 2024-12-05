using DocumentForge.Core;
using DocumentForge.Document;
using DocumentForge.Index;
using DocumentForge.Query;

namespace DocumentForge.Engine.Cluster;

/// <summary>
/// Query router over multiple DocumentForge shards.
///
/// Each collection can be either:
///   - HASH-sharded by a key path (one shard owns each document)
///   - REPLICATED to every shard (small reference data — joins stay local)
///
/// Unknown collections default to hash sharding on "_id".
/// </summary>
public sealed class DocumentForgeCluster : IDisposable
{
    private readonly List<IShardTransport> _shards = new();
    private readonly List<string> _shardNames = new();
    private readonly Dictionary<string, CollectionShardingPolicy> _policies = new(StringComparer.OrdinalIgnoreCase);
    private ConsistentHashRing? _ring;
    private int _virtualNodesPerShard = 150;

    // During an online rebalance, the previous ring is still valid for READ fallback.
    // Set via EnableMigrationMode, cleared via DisableMigrationMode.
    private ConsistentHashRing? _previousRing;
    private List<IShardTransport>? _previousShards;
    private bool _disposed;

    public int ShardCount => _shards.Count;
    public IReadOnlyList<IShardTransport> Shards => _shards;
    public bool IsMigrating => _previousRing is not null;

    /// <summary>
    /// Build a cluster from a persisted ClusterConfig. The caller supplies a factory
    /// that knows how to create an IShardTransport from a ShardDescriptor (useful for
    /// choosing between in-process, HTTP, TCP transports).
    /// </summary>
    public static DocumentForgeCluster FromConfig(
        ClusterConfig config,
        Func<ShardDescriptor, IShardTransport> transportFactory)
    {
        var cluster = new DocumentForgeCluster();
        cluster._virtualNodesPerShard = config.VirtualNodesPerShard;
        foreach (var s in config.Shards)
            cluster.AddShard(transportFactory(s));
        foreach (var (name, policy) in config.Collections)
        {
            if (policy.Strategy == ShardingStrategy.Replicated)
                cluster.ReplicateCollection(name);
            else if (policy.Strategy == ShardingStrategy.Hash && policy.ShardKeyPath is not null)
                cluster.ShardCollection(name, policy.ShardKeyPath);
        }
        return cluster;
    }

    /// <summary>Export the current cluster config so it can be persisted and distributed to all nodes.</summary>
    public ClusterConfig ToConfig(Func<IShardTransport, string>? endpointResolver = null)
    {
        var cfg = new ClusterConfig { VirtualNodesPerShard = _virtualNodesPerShard };
        for (int i = 0; i < _shards.Count; i++)
            cfg.Shards.Add(new ShardDescriptor
            {
                Name = _shardNames[i],
                Endpoint = endpointResolver?.Invoke(_shards[i]) ?? ""
            });
        foreach (var (name, pol) in _policies)
            cfg.Collections[name] = new CollectionPolicyDescriptor
            {
                Strategy = pol.Strategy,
                ShardKeyPath = pol.ShardKeyPath
            };
        return cfg;
    }

    /// <summary>Add a shard to the cluster. The shard's <c>ShardName</c> is used as its stable
    /// identity on the consistent hash ring - changing it would re-route data.</summary>
    public DocumentForgeCluster AddShard(IShardTransport transport)
    {
        _shards.Add(transport);
        _shardNames.Add(transport.ShardName);
        _ring = new ConsistentHashRing(_shardNames, _virtualNodesPerShard);
        return this;
    }

    /// <summary>
    /// Enable online-migration mode. While active, read queries check the primary ring
    /// first and fall back to the previous ring when a keyed lookup returns zero rows.
    /// Writes still route to the primary ring only - callers should also ensure the
    /// rebalancer cleans up stale copies on the previous shards.
    /// </summary>
    public void EnableMigrationMode(List<IShardTransport> previousShards)
    {
        _previousShards = previousShards;
        _previousRing = new ConsistentHashRing(
            previousShards.Select(s => s.ShardName).ToList(),
            _virtualNodesPerShard);
    }

    public void DisableMigrationMode()
    {
        _previousRing = null;
        _previousShards = null;
    }

    /// <summary>
    /// Configure a collection to be hash-sharded across all shards by the given key path.
    /// Example: ShardCollection("orders", "pnr").
    /// </summary>
    public DocumentForgeCluster ShardCollection(string collectionName, string shardKeyPath)
    {
        _policies[collectionName] = new CollectionShardingPolicy
        {
            CollectionName = collectionName,
            Strategy = ShardingStrategy.Hash,
            ShardKeyPath = shardKeyPath
        };
        return this;
    }

    /// <summary>
    /// Configure a collection to be replicated to every shard (small reference data).
    /// Writes go to every shard; reads stay local.
    /// </summary>
    public DocumentForgeCluster ReplicateCollection(string collectionName)
    {
        _policies[collectionName] = new CollectionShardingPolicy
        {
            CollectionName = collectionName,
            Strategy = ShardingStrategy.Replicated
        };
        return this;
    }

    private CollectionShardingPolicy GetPolicy(string collectionName)
    {
        if (_policies.TryGetValue(collectionName, out var p)) return p;
        // Default: hash by _id
        return new CollectionShardingPolicy
        {
            CollectionName = collectionName,
            Strategy = ShardingStrategy.Hash,
            ShardKeyPath = "_id"
        };
    }

    /// <summary>
    /// Pick a shard for a given shard-key value via the consistent hash ring.
    /// Adding or removing a shard only re-routes ~1/N of the keys (the ones nearest
    /// the new/removed shard's ring positions), not all of them.
    /// </summary>
    private int PickShard(BsonValue keyValue)
    {
        if (_ring is null || _shards.Count == 0)
            throw new DocumentForgeException("Cluster has no shards configured.");
        var s = keyValue.ToString() ?? "";
        return _ring.PickShardIndex(s);
    }

    // --- Insert ---

    public DocumentId Insert(string collectionName, BsonDocument doc)
    {
        doc.EnsureId();
        var policy = GetPolicy(collectionName);

        if (policy.Strategy == ShardingStrategy.Replicated)
        {
            // Write to every shard
            DocumentId replicatedId = doc.GetId();
            foreach (var shard in _shards) shard.Insert(collectionName, doc);
            return replicatedId;
        }

        // Hash sharding - pick one shard
        var keyValue = JsonPathExtractor.Extract(doc, policy.ShardKeyPath!);
        if (keyValue.IsNull)
            throw new DocumentForgeException($"Document missing shard key '{policy.ShardKeyPath}' for collection '{collectionName}'.");

        int shardIdx = PickShard(keyValue);
        var insertedId = _shards[shardIdx].Insert(collectionName, doc);

        // Migration cleanup: if a previous ring is active and the key maps to a DIFFERENT
        // shard on the old ring, best-effort delete any stale copy there. This prevents
        // duplicates surfacing via dual-read fallback.
        if (_previousRing is not null && _previousShards is not null)
        {
            int prevIdx = _previousRing.PickShardIndex(keyValue.ToString() ?? "");
            var prevShardName = _previousShards[prevIdx].ShardName;
            if (!string.Equals(prevShardName, _shards[shardIdx].ShardName, StringComparison.OrdinalIgnoreCase))
            {
                try { _previousShards[prevIdx].DeleteById(collectionName, doc.GetId()); } catch { /* best effort */ }
            }
        }
        return insertedId;
    }

    public DocumentId Insert(string collectionName, string json) =>
        Insert(collectionName, BsonDocument.FromJson(json));

    // --- Execute ---

    public QueryResult Execute(string sql)
    {
        // Parse minimally so we know which collection + whether WHERE has shard key equality
        var tokens = new Lexer(sql).Tokenize();
        var stmt = new Parser(tokens).Parse();

        return stmt switch
        {
            SelectStatement sel => ExecuteSelect(sel, sql),
            InsertStatement ins => ExecuteInsert(ins),
            UpdateStatement upd => ExecuteWriteBroadcast(upd.Collection, sql, upd.Where),
            DeleteStatement del => ExecuteWriteBroadcast(del.Collection, sql, del.Where),
            CreateIndexStatement ci => ExecuteBroadcast(ci.Collection, sql),
            DropIndexStatement di => ExecuteBroadcast(di.Collection, sql),
            CountStatement cnt => ExecuteCount(cnt, sql),
            _ => QueryResult.Error($"Cluster does not support {stmt.GetType().Name} yet")
        };
    }

    private QueryResult ExecuteInsert(InsertStatement stmt)
    {
        var doc = BsonDocument.FromJson(stmt.JsonDocument);
        var id = Insert(stmt.Collection, doc);
        return QueryResult.Affected(1, $"Inserted {id} into {stmt.Collection}");
    }

    private QueryResult ExecuteSelect(SelectStatement stmt, string sql)
    {
        var policy = GetPolicy(stmt.Collection);

        // Replicated: any shard works
        if (policy.Strategy == ShardingStrategy.Replicated)
            return _shards[0].Execute(sql);

        // Hash: try to route to a single shard if WHERE has shardKey = value
        var targetShard = TryRouteByShardKey(policy, stmt.Where);
        if (targetShard.HasValue)
        {
            var r = _shards[targetShard.Value].Execute(sql);
            // During online migration, if the primary shard misses, fall back to the
            // previous ring location - the doc may not have been migrated yet.
            if (r.Documents.Count == 0 && _previousRing is not null && _previousShards is not null && policy.ShardKeyPath is not null)
            {
                var keyVal = GetShardKeyFromWhere(policy, stmt.Where);
                if (keyVal is not null)
                {
                    int prevIdx = _previousRing.PickShardIndex(keyVal);
                    var fallback = _previousShards[prevIdx].Execute(sql);
                    if (fallback.Documents.Count > 0)
                        return fallback with { QueryPlan = $"SINGLE_SHARD_FALLBACK({_previousShards[prevIdx].ShardName}) + {fallback.QueryPlan}" };
                }
            }
            return r with { QueryPlan = $"SINGLE_SHARD({_shards[targetShard.Value].ShardName}) + {r.QueryPlan}" };
        }

        // Scatter-gather: run on every shard and merge. During migration we also query
        // the previous shards to catch docs not yet migrated.
        if (_previousShards is not null)
            return ScatterGatherWithFallback(stmt, sql);
        return ScatterGather(stmt, sql);
    }

    private string? GetShardKeyFromWhere(CollectionShardingPolicy policy, Expression? where)
    {
        if (where is null || policy.ShardKeyPath is null) return null;
        var found = FindEqualityOnPath(where, policy.ShardKeyPath);
        return found?.ToString();
    }

    private QueryResult ScatterGatherWithFallback(SelectStatement stmt, string sql)
    {
        // Union scatter across CURRENT and PREVIOUS shards, dedup by _id.
        // This is the read path during a live migration.
        var seen = new HashSet<string>();
        var merged = new List<BsonDocument>();

        void AddFromShards(List<IShardTransport> shards)
        {
            foreach (var shard in shards)
            {
                var r = shard.Execute(sql);
                foreach (var doc in r.Documents)
                {
                    var id = doc["_id"].ToString();
                    if (seen.Add(id)) merged.Add(doc);
                }
            }
        }

        AddFromShards(_shards);
        if (_previousShards is not null) AddFromShards(_previousShards);

        // Apply ORDER BY / LIMIT / OFFSET globally (same logic as ScatterGather)
        if (stmt.OrderByPath is not null)
        {
            merged.Sort((a, b) =>
            {
                var va = JsonPathExtractor.Extract(a, stmt.OrderByPath);
                var vb = JsonPathExtractor.Extract(b, stmt.OrderByPath);
                var cmp = va.CompareTo(vb);
                return stmt.OrderDescending ? -cmp : cmp;
            });
        }
        if (stmt.Offset.HasValue) merged = merged.Skip(stmt.Offset.Value).ToList();
        if (stmt.Limit.HasValue) merged = merged.Take(stmt.Limit.Value).ToList();

        var totalShards = _shards.Count + (_previousShards?.Count ?? 0);
        return QueryResult.Ok(merged, $"SCATTER_GATHER_DUAL({totalShards} shards, dedup)");
    }

    private int? TryRouteByShardKey(CollectionShardingPolicy policy, Expression? where)
    {
        if (where is null || policy.ShardKeyPath is null) return null;
        // Look for a direct equality comparison on the shard key (or within an AND tree)
        var found = FindEqualityOnPath(where, policy.ShardKeyPath);
        if (found is null) return null;
        var bsonVal = found switch
        {
            string s => BsonValue.FromString(s),
            double d => BsonValue.FromDouble(d),
            int i => BsonValue.FromInt32(i),
            bool b => BsonValue.FromBool(b),
            _ => BsonValue.Null
        };
        if (bsonVal.IsNull) return null;
        return PickShard(bsonVal);
    }

    private static object? FindEqualityOnPath(Expression expr, string path)
    {
        if (expr is ComparisonExpression c && c.Operator == TokenType.Equals &&
            string.Equals(c.JsonPath, path, StringComparison.OrdinalIgnoreCase))
            return c.Value;
        if (expr is LogicalExpression l && l.Operator == TokenType.And)
            return FindEqualityOnPath(l.Left, path) ?? FindEqualityOnPath(l.Right, path);
        return null;
    }

    private QueryResult ScatterGather(SelectStatement stmt, string sql)
    {
        // Run the same SQL on every shard
        var shardResults = new List<QueryResult>(_shards.Count);
        foreach (var shard in _shards)
            shardResults.Add(shard.Execute(sql));

        // Non-aggregated: concat + apply ORDER BY / LIMIT / OFFSET globally
        if (!stmt.HasAggregates && stmt.GroupByPaths.Count == 0)
        {
            var merged = new List<BsonDocument>();
            foreach (var r in shardResults) merged.AddRange(r.Documents);

            if (stmt.OrderByPath is not null)
            {
                merged.Sort((a, b) =>
                {
                    var va = JsonPathExtractor.Extract(a, stmt.OrderByPath);
                    var vb = JsonPathExtractor.Extract(b, stmt.OrderByPath);
                    var cmp = va.CompareTo(vb);
                    return stmt.OrderDescending ? -cmp : cmp;
                });
            }
            if (stmt.Offset.HasValue) merged = merged.Skip(stmt.Offset.Value).ToList();
            if (stmt.Limit.HasValue) merged = merged.Take(stmt.Limit.Value).ToList();

            return QueryResult.Ok(merged, $"SCATTER_GATHER({_shards.Count} shards)");
        }

        // Aggregated: merge per-shard aggregates into a global result
        return MergeAggregates(stmt, shardResults);
    }

    private QueryResult MergeAggregates(SelectStatement stmt, List<QueryResult> shardResults)
    {
        // Group results across shards. For each group-key combination, merge aggregate values.
        var buckets = new Dictionary<string, (Dictionary<string, BsonValue> Keys, Dictionary<string, (double Sum, long Count, double Min, double Max)> Stats)>();

        foreach (var shardResult in shardResults)
        {
            foreach (var row in shardResult.Documents)
            {
                // Build the group key from the group-by columns in this row
                var keyParts = new List<string>();
                var keyValues = new Dictionary<string, BsonValue>();
                foreach (var gbPath in stmt.GroupByPaths)
                {
                    var v = JsonPathExtractor.Extract(row, gbPath);
                    keyValues[gbPath] = v;
                    keyParts.Add(v.ToString());
                }
                var groupKey = string.Join("\u0001", keyParts);

                if (!buckets.TryGetValue(groupKey, out var bucket))
                {
                    bucket = (keyValues, new Dictionary<string, (double, long, double, double)>());
                    buckets[groupKey] = bucket;
                }

                foreach (var agg in stmt.Aggregates)
                {
                    var val = row[agg.Alias];
                    if (val.IsNull) continue;
                    var n = val.IsNumeric ? val.ToDouble() : 0;
                    if (!bucket.Stats.TryGetValue(agg.Alias, out var s))
                        s = (0, 0, double.MaxValue, double.MinValue);
                    s = agg.Function switch
                    {
                        AggregateFunction.Count => (s.Sum + n, s.Count + 1, s.Min, s.Max),
                        AggregateFunction.Sum => (s.Sum + n, s.Count + 1, s.Min, s.Max),
                        AggregateFunction.Min => (s.Sum, s.Count + 1, Math.Min(s.Min, n), s.Max),
                        AggregateFunction.Max => (s.Sum, s.Count + 1, s.Min, Math.Max(s.Max, n)),
                        // AVG: accumulate sum across shards, we'll divide by count below.
                        // But note: shard-level AVG loses the per-shard count. This is a known limit.
                        AggregateFunction.Avg => (s.Sum + n, s.Count + 1, s.Min, s.Max),
                        _ => s
                    };
                    bucket.Stats[agg.Alias] = s;
                }
            }
        }

        // Build final rows
        var results = new List<BsonDocument>(buckets.Count);
        foreach (var (_, bucket) in buckets)
        {
            var row = new BsonDocument();
            foreach (var (path, value) in bucket.Keys) row[path] = value;
            foreach (var agg in stmt.Aggregates)
            {
                if (!bucket.Stats.TryGetValue(agg.Alias, out var s)) { row[agg.Alias] = BsonValue.Null; continue; }
                row[agg.Alias] = agg.Function switch
                {
                    AggregateFunction.Count => BsonValue.FromInt64((long)s.Sum),
                    AggregateFunction.Sum => BsonValue.FromDouble(s.Sum),
                    AggregateFunction.Min => BsonValue.FromDouble(s.Min),
                    AggregateFunction.Max => BsonValue.FromDouble(s.Max),
                    AggregateFunction.Avg => BsonValue.FromDouble(s.Count == 0 ? 0 : s.Sum / s.Count),
                    _ => BsonValue.Null
                };
            }
            results.Add(row);
        }
        return QueryResult.Ok(results, $"SCATTER_GATHER_AGGREGATE({_shards.Count} shards)");
    }

    private QueryResult ExecuteCount(CountStatement stmt, string sql)
    {
        var policy = GetPolicy(stmt.Collection);
        if (policy.Strategy == ShardingStrategy.Replicated)
            return _shards[0].Execute(sql);

        long total = 0;
        foreach (var shard in _shards)
        {
            var r = shard.Execute(sql);
            if (r.Documents.Count > 0) total += r.Documents[0]["count"].AsInt64;
        }
        var doc = new BsonDocument();
        doc["count"] = BsonValue.FromInt64(total);
        return QueryResult.Ok(new List<BsonDocument> { doc }, $"SCATTER_GATHER_COUNT({_shards.Count} shards)");
    }

    private QueryResult ExecuteWriteBroadcast(string collection, string sql, Expression? where)
    {
        var policy = GetPolicy(collection);
        if (policy.Strategy == ShardingStrategy.Replicated)
        {
            // Send to all shards
            return ExecuteBroadcast(collection, sql);
        }

        // Hash: try to route to a single shard
        var target = TryRouteByShardKey(policy, where);
        if (target.HasValue)
            return _shards[target.Value].Execute(sql);
        return ExecuteBroadcast(collection, sql);
    }

    private QueryResult ExecuteBroadcast(string collection, string sql)
    {
        long total = 0;
        foreach (var shard in _shards)
        {
            var r = shard.Execute(sql);
            total += r.AffectedCount;
        }
        return QueryResult.Affected(total, $"BROADCAST to {_shards.Count} shards");
    }

    // --- Transactions (issue #14, Phase A: single-shard fast path) ---

    /// <summary>
    /// Open a multi-document transaction over the cluster. Stage writes via
    /// the returned handle; commit applies them atomically on the participating
    /// shard(s).
    ///
    /// <para>
    /// Phase A: only single-shard transactions commit successfully. If staged
    /// ops span more than one shard, <see cref="ClusterTransaction.Commit"/>
    /// throws <see cref="NotImplementedException"/>. Cross-shard 2PC ships in
    /// Phase B of issue #14.
    /// </para>
    /// </summary>
    public ClusterTransaction BeginTransaction()
    {
        if (_shards.Count == 0)
            throw new DocumentForgeException("Cluster has no shards configured; cannot open a transaction.");
        return new ClusterTransaction(this);
    }

    // ClusterTransaction-only hooks. Kept internal so the public surface
    // stays focused on the routing API.
    internal CollectionShardingPolicy GetPolicyForTx(string collection) => GetPolicy(collection);
    internal int PickShardForTx(BsonValue keyValue) => PickShard(keyValue);
    internal void ExecuteOnShardForTx(int shardIndex, IReadOnlyList<ShardTxOp> ops) =>
        _shards[shardIndex].ExecuteTransaction(ops);

    // 2PC participant calls (Phase B.2). The cluster relays these straight
    // to the relevant shard's transport.
    internal string GetShardNameForTx(int shardIndex) => _shardNames[shardIndex];
    internal PrepareResult PrepareOnShardForTx(int shardIndex, string txId, string coordinatorShardId, IReadOnlyList<ShardTxOp> ops) =>
        _shards[shardIndex].Prepare(txId, coordinatorShardId, ops);
    internal void CommitOnShardForTx(int shardIndex, string txId) =>
        _shards[shardIndex].CommitPrepared(txId);
    internal void RollbackOnShardForTx(int shardIndex, string txId) =>
        _shards[shardIndex].RollbackPrepared(txId);

    // Phase C.1 — coordinator-decision-log calls. Always issued against the
    // shard chosen as coordinator for this tx (lowest participant index).
    internal void RecordCoordinatorDecisionForTx(int coordinatorShardIndex, string txId, bool commit) =>
        _shards[coordinatorShardIndex].RecordCoordinatorDecision(txId, commit);
    internal void RecordCoordinatorDoneForTx(int coordinatorShardIndex, string txId) =>
        _shards[coordinatorShardIndex].RecordCoordinatorDone(txId);

    /// <summary>
    /// Result of a <see cref="Recover"/> sweep. <c>Committed</c> is the
    /// count of in-flight prepared tx slices that the coordinator decided
    /// COMMIT for and we successfully applied; <c>Aborted</c> is the count
    /// we rolled back (coordinator either didn't decide or never saw the
    /// tx). <c>Skipped</c> is the count we couldn't resolve because the
    /// coordinator shard isn't in the current cluster (stale shard
    /// configuration after a rebalance, say) — operator intervention
    /// needed for those.
    /// </summary>
    public sealed record RecoverySummary(int Committed, int Aborted, int Skipped);

    /// <summary>
    /// Resolve every in-flight 2PC transaction left over from a prior
    /// crash. For each shard, scan its prepared.log; for each PREPARED
    /// slice, ask the coordinator shard for the decision. If the
    /// coordinator decided COMMIT, finalize with COMMIT; otherwise
    /// (no decision recorded, or coord shard absent), finalize with
    /// ROLLBACK.
    ///
    /// <para>
    /// Idempotent: calling this on a cluster with no in-flight tx
    /// returns zero counts and does no work. Safe to call repeatedly,
    /// e.g. after a network reconnect or as a cluster startup hook.
    /// </para>
    ///
    /// <para>
    /// Phase C.2 scope: handles the participant-restart-with-PREPARED
    /// case. The "coordinator died after deciding but before broadcast"
    /// scenario is also covered, because the participants whose
    /// COMMIT message never arrived are still PREPARED — the sweep
    /// finds them and pulls the decision from the coordinator's
    /// log. The "coordinator died before deciding" scenario resolves
    /// to ROLLBACK.
    /// </para>
    /// </summary>
    public RecoverySummary Recover()
    {
        int committed = 0, aborted = 0, skipped = 0;

        for (int shardIdx = 0; shardIdx < _shards.Count; shardIdx++)
        {
            IReadOnlyList<PreparedTxRecord> inFlight;
            try { inFlight = _shards[shardIdx].ScanInFlightPrepared(); }
            catch (NotSupportedException)
            {
                // HTTP transport doesn't yet implement scanning. Tracked
                // alongside the other HTTP wire ops in the issue's later
                // phases — for now we just can't recover over HTTP.
                continue;
            }

            foreach (var rec in inFlight)
            {
                int coordIdx = FindShardIndexByName(rec.CoordinatorShardId);
                if (coordIdx < 0)
                {
                    skipped++;
                    continue;
                }

                CoordinatorTxState? state;
                try { state = _shards[coordIdx].GetCoordinatorTxState(rec.TxId); }
                catch (NotSupportedException)
                {
                    skipped++;
                    continue;
                }

                if (state?.Decided == true)
                {
                    _shards[shardIdx].CommitPrepared(rec.TxId);
                    committed++;
                }
                else
                {
                    _shards[shardIdx].RollbackPrepared(rec.TxId);
                    aborted++;
                }
            }
        }

        return new RecoverySummary(committed, aborted, skipped);
    }

    private int FindShardIndexByName(string name)
    {
        for (int i = 0; i < _shardNames.Count; i++)
            if (string.Equals(_shardNames[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var s in _shards) try { s.Dispose(); } catch { }
        _disposed = true;
    }
}
