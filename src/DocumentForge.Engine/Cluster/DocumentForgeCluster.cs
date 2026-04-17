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
    private readonly Dictionary<string, CollectionShardingPolicy> _policies = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public int ShardCount => _shards.Count;
    public IReadOnlyList<IShardTransport> Shards => _shards;

    /// <summary>Add a shard to the cluster. Shards are identified by their position (0..N-1).</summary>
    public DocumentForgeCluster AddShard(IShardTransport transport)
    {
        _shards.Add(transport);
        return this;
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
    /// Pick a shard for a given shard-key value. Uses a stable hash so the same value
    /// always lands on the same shard.
    /// </summary>
    private int PickShard(BsonValue keyValue)
    {
        if (_shards.Count == 0)
            throw new DocumentForgeException("Cluster has no shards configured.");
        // Stable string hash so any BsonValue type works and is deterministic.
        var s = keyValue.ToString() ?? "";
        unchecked
        {
            uint h = 2166136261;
            foreach (var c in s) h = (h ^ c) * 16777619;
            return (int)(h % (uint)_shards.Count);
        }
    }

    // --- Insert ---

    public DocumentId Insert(string collectionName, BsonDocument doc)
    {
        doc.EnsureId();
        var policy = GetPolicy(collectionName);

        if (policy.Strategy == ShardingStrategy.Replicated)
        {
            // Write to every shard
            DocumentId id = doc.GetId();
            foreach (var shard in _shards) shard.Insert(collectionName, doc);
            return id;
        }

        // Hash sharding - pick one shard
        var keyValue = JsonPathExtractor.Extract(doc, policy.ShardKeyPath!);
        if (keyValue.IsNull)
            throw new DocumentForgeException($"Document missing shard key '{policy.ShardKeyPath}' for collection '{collectionName}'.");
        int shardIdx = PickShard(keyValue);
        return _shards[shardIdx].Insert(collectionName, doc);
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
            return r with { QueryPlan = $"SINGLE_SHARD({_shards[targetShard.Value].ShardName}) + {r.QueryPlan}" };
        }

        // Scatter-gather: run on every shard and merge
        return ScatterGather(stmt, sql);
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

    public void Dispose()
    {
        if (_disposed) return;
        foreach (var s in _shards) try { s.Dispose(); } catch { }
        _disposed = true;
    }
}
