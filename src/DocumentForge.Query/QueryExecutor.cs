using System.Diagnostics;
using DocumentForge.Core;
using DocumentForge.Document;
using DocumentForge.Index;

namespace DocumentForge.Query;

public sealed class QueryExecutor
{
    private readonly CollectionCatalog _catalog;
    private readonly IndexManager _indexManager;

    public QueryExecutor(CollectionCatalog catalog, IndexManager indexManager)
    {
        _catalog = catalog;
        _indexManager = indexManager;
    }

    public QueryResult Execute(string query)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var lexer = new Lexer(query);
            var tokens = lexer.Tokenize();
            var parser = new Parser(tokens);
            var statement = parser.Parse();

            var result = statement switch
            {
                SelectStatement select => ExecuteSelect(select),
                InsertStatement insert => ExecuteInsert(insert),
                UpdateStatement update => ExecuteUpdate(update),
                DeleteStatement delete => ExecuteDelete(delete),
                CreateIndexStatement createIdx => ExecuteCreateIndex(createIdx),
                DropIndexStatement dropIdx => ExecuteDropIndex(dropIdx),
                CountStatement count => ExecuteCount(count),
                _ => QueryResult.Error($"Unknown statement type: {statement.GetType().Name}")
            };

            sw.Stop();
            return result with { ExecutionTime = sw.Elapsed };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return QueryResult.Error(ex.Message) with { ExecutionTime = sw.Elapsed };
        }
    }

    private QueryResult ExecuteSelect(SelectStatement stmt)
    {
        var collection = _catalog.GetCollection(stmt.Collection);
        if (collection is null)
            return QueryResult.Error(BuildUnknownCollectionMessage(stmt.Collection), QueryResult.Codes.CollectionNotFound);

        // Can we push LIMIT down? Only if we don't need to sort afterward,
        // join, aggregate/group, or dedupe. Each of those operations needs
        // the full result set before LIMIT can be safely applied.
        int? effectiveLimit = null;
        if (stmt.OrderByPath is null
            && stmt.Limit.HasValue
            && stmt.Join is null
            && !stmt.HasAggregates
            && stmt.GroupByPaths.Count == 0
            && !stmt.IsDistinct)
        {
            effectiveLimit = (stmt.Offset ?? 0) + stmt.Limit.Value;
        }

        List<BsonDocument> results;
        string plan;

        // With JOIN, defer WHERE until after join (paths may be prefixed with collection names)
        Expression? preJoinWhere = stmt.Join is null ? stmt.Where : null;

        // Try index scan
        var indexScan = TryIndexScan(stmt.Collection, preJoinWhere, collection, effectiveLimit);
        if (indexScan is not null)
        {
            results = indexScan.Value.docs;
            plan = indexScan.Value.plan;
        }
        else
        {
            // Full collection scan - break early if LIMIT pushed down
            results = new List<BsonDocument>();
            foreach (var doc in collection.FindAll())
            {
                if (preJoinWhere is null || ExpressionEvaluator.Evaluate(preJoinWhere, doc))
                {
                    results.Add(doc);
                    if (effectiveLimit.HasValue && results.Count >= effectiveLimit.Value)
                        break;
                }
            }
            plan = "COLLECTION_SCAN";
        }

        // JOINs: chain left-deep (issue #44 Phase B). The result of each
        // join feeds the next as the outer side. The "outer collection
        // name" only matters for the FIRST join's path-prefix splitting;
        // subsequent joins resolve against the running result set, where
        // both nested collections are present.
        if (stmt.Joins.Count > 0)
        {
            foreach (var join in stmt.Joins)
            {
                var (joined, joinPlan) = ExecuteHashJoin(stmt, join, results);
                results = joined;
                plan = $"{plan} + {joinPlan}";
            }

            // Apply WHERE to the combined documents (now that prefixed paths resolve correctly)
            if (stmt.Where is not null)
            {
                results = results.Where(doc => ExpressionEvaluator.Evaluate(stmt.Where, doc)).ToList();
            }
        }

        // AGGREGATIONS / GROUP BY (applied after WHERE + JOIN)
        if (stmt.HasAggregates || stmt.GroupByPaths.Count > 0)
        {
            results = ApplyAggregations(stmt, results);
            plan = $"{plan} + AGGREGATE";
        }

        // DISTINCT must operate on the user-visible row shape, so when a non-*
        // projection is requested we project early. Aggregations have already
        // shaped the rows, so they're already in user-visible form.
        // Importantly we drop _id from the projection here - if every row has
        // a unique _id, dedupe collapses nothing.
        bool projectedEarly = false;
        if (stmt.IsDistinct
            && !stmt.HasAggregates
            && stmt.Fields.Count > 0
            && !stmt.Fields.Contains("*"))
        {
            results = results.Select(doc => ProjectFields(doc, stmt.Fields, includeId: false)).ToList();
            projectedEarly = true;
        }

        if (stmt.IsDistinct)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var deduped = new List<BsonDocument>(results.Count);
            foreach (var doc in results)
            {
                // Dedupe by the JSON of the (already-projected) doc.
                if (seen.Add(doc.ToJson())) deduped.Add(doc);
            }
            results = deduped;
            plan = $"{plan} + DISTINCT";
        }

        // ORDER BY
        if (stmt.OrderByPath is not null)
        {
            results.Sort((a, b) =>
            {
                var va = JsonPathExtractor.Extract(a, stmt.OrderByPath);
                var vb = JsonPathExtractor.Extract(b, stmt.OrderByPath);
                var cmp = va.CompareTo(vb);
                return stmt.OrderDescending ? -cmp : cmp;
            });
        }

        // OFFSET
        if (stmt.Offset.HasValue)
            results = results.Skip(stmt.Offset.Value).ToList();

        // LIMIT
        if (stmt.Limit.HasValue)
            results = results.Take(stmt.Limit.Value).ToList();

        // Field projection - skip when aggregates are present (ApplyAggregations already shaped the row)
        // and skip when DISTINCT already projected early (above).
        if (stmt.Fields.Count > 0 && !stmt.Fields.Contains("*") && !stmt.HasAggregates && !projectedEarly)
        {
            results = results.Select(doc => ProjectFields(doc, stmt.Fields)).ToList();
        }

        return QueryResult.Ok(results, plan);
    }

    private (List<BsonDocument> docs, string plan)? TryIndexScan(
        string collectionName, Expression? where, Collection collection, int? effectiveLimit = null)
    {
        if (where is null) return null;

        // OR-of-equalities on a single indexed path: covers
        //   WHERE x = a OR x = b OR x = c
        // and (because the parser lowers IN to OR-equalities) also covers
        //   WHERE x IN (a, b, c).
        // Each branch becomes one INDEX_SCAN; results are unioned with dedupe.
        if (TryFlattenSamePathOrEqualities(where, out var orPath, out var orValues))
        {
            var orIndex = _indexManager.FindIndexForPath(collectionName, orPath);
            if (orIndex is not null && !orIndex.Definition.IsComposite)
            {
                collection.BuildLocationMap();
                var seen = new HashSet<DocumentId>();
                var orResults = new List<BsonDocument>();
                foreach (var (val, valType) in orValues)
                {
                    var key = new IndexKey(ConvertToSearchValue(val, valType));
                    foreach (var docId in orIndex.Search(key))
                    {
                        if (!seen.Add(docId)) continue; // doc already collected via another branch
                        var doc = collection.FindById(docId);
                        if (doc is null) continue;
                        orResults.Add(doc);
                        if (effectiveLimit.HasValue && orResults.Count >= effectiveLimit.Value) break;
                    }
                    if (effectiveLimit.HasValue && orResults.Count >= effectiveLimit.Value) break;
                }
                return (orResults, $"INDEX_SCAN_MULTI({orIndex.Definition.Name}, {orValues.Count} keys)");
            }
        }

        // Gather all top-level AND comparisons
        var comparisons = CollectAndComparisons(where);

        // Try composite index match first: need all leading paths of some composite index covered by equality
        var compositeHit = TryCompositeIndexMatch(collectionName, comparisons);
        if (compositeHit is not null)
        {
            var (compIndex, compKey, residuals) = compositeHit.Value;
            collection.BuildLocationMap();
            var compResults = new List<BsonDocument>();
            var residualFilter = BuildResidualFilter(residuals);
            foreach (var docId in compIndex.Search(compKey))
            {
                var doc = collection.FindById(docId);
                if (doc is null) continue;
                if (residualFilter is null || ExpressionEvaluator.Evaluate(residualFilter, doc))
                {
                    compResults.Add(doc);
                    if (effectiveLimit.HasValue && compResults.Count >= effectiveLimit.Value) break;
                }
            }
            return (compResults, $"INDEX_SCAN_COMPOSITE({compIndex.Definition.Name})");
        }

        // Extract the indexable comparison from the WHERE clause (single-field path)
        ComparisonExpression? comp = null;
        Expression? remainingFilter = null;

        if (where is ComparisonExpression directComp)
        {
            comp = directComp;
        }
        else if (where is LogicalExpression logical && logical.Operator == TokenType.And)
        {
            if (logical.Left is ComparisonExpression leftComp)
            {
                comp = leftComp;
                remainingFilter = logical.Right;
            }
            else if (logical.Right is ComparisonExpression rightComp)
            {
                comp = rightComp;
                remainingFilter = logical.Left;
            }
        }

        if (comp is null) return null;

        if (comp.Operator is not (TokenType.Equals
            or TokenType.GreaterThan or TokenType.LessThan
            or TokenType.GreaterThanOrEqual or TokenType.LessThanOrEqual))
            return null;

        var index = _indexManager.FindIndexForPath(collectionName, comp.JsonPath);
        if (index is null || index.Definition.IsComposite) return null; // composite match was tried above

        // Ensure location map is built for O(1) lookups
        collection.BuildLocationMap();

        var searchKey = new IndexKey(ConvertToSearchValue(comp.Value, comp.ValueType));

        // Choose between Search (equality) and RangeScan (>, <, >=, <=)
        IEnumerable<DocumentId> docIds;
        string scanType;
        switch (comp.Operator)
        {
            case TokenType.Equals:
                docIds = index.Search(searchKey);
                scanType = "INDEX_SCAN";
                break;
            case TokenType.GreaterThan:
                docIds = index.RangeScan(low: searchKey, high: null, inclusiveLow: false);
                scanType = "INDEX_RANGE_SCAN";
                break;
            case TokenType.GreaterThanOrEqual:
                docIds = index.RangeScan(low: searchKey, high: null, inclusiveLow: true);
                scanType = "INDEX_RANGE_SCAN";
                break;
            case TokenType.LessThan:
                docIds = index.RangeScan(low: null, high: searchKey, inclusiveHigh: false);
                scanType = "INDEX_RANGE_SCAN";
                break;
            case TokenType.LessThanOrEqual:
                docIds = index.RangeScan(low: null, high: searchKey, inclusiveHigh: true);
                scanType = "INDEX_RANGE_SCAN";
                break;
            default:
                return null;
        }

        // Stream IDs lazily, break early if LIMIT pushed down
        var results = new List<BsonDocument>();
        foreach (var docId in docIds)
        {
            var doc = collection.FindById(docId); // O(1) via location map
            if (doc is not null)
            {
                if (remainingFilter is null || ExpressionEvaluator.Evaluate(remainingFilter, doc))
                {
                    results.Add(doc);
                    if (effectiveLimit.HasValue && results.Count >= effectiveLimit.Value)
                        break;
                }
            }
        }

        return (results, $"{scanType}({index.Definition.Name})");
    }

    /// <summary>
    /// Walk down a top-level AND tree and collect all leaf comparisons.
    /// OR/NOT stop the collection (we can't use indexes through them in this simple form).
    /// </summary>
    /// <summary>
    /// Build a self-debugging error message for an unknown collection. Lists
    /// the actual collections the catalog knows about, so a typo or case-folding
    /// surprise is immediately obvious from the error alone.
    /// </summary>
    private string BuildUnknownCollectionMessage(string requested)
    {
        var known = _catalog.GetCollectionNames();
        if (known.Count == 0)
            return $"Collection '{requested}' not found. The database has no collections yet — POST a document to create one.";

        // Surface a hint when the only difference is case.
        var caseMatch = known.FirstOrDefault(n => string.Equals(n, requested, StringComparison.OrdinalIgnoreCase));
        if (caseMatch is not null && caseMatch != requested)
            return $"Collection '{requested}' not found. Did you mean '{caseMatch}'? Names are stored case-insensitively.";

        var preview = known.Count > 12
            ? string.Join(", ", known.Take(12)) + $", … (+{known.Count - 12} more)"
            : string.Join(", ", known);
        return $"Collection '{requested}' not found. Known collections: {preview}.";
    }

    private static List<ComparisonExpression> CollectAndComparisons(Expression expr)
    {
        var list = new List<ComparisonExpression>();
        void Visit(Expression e)
        {
            if (e is ComparisonExpression c) list.Add(c);
            else if (e is LogicalExpression l && l.Operator == TokenType.And)
            {
                Visit(l.Left);
                Visit(l.Right);
            }
            // OR/NOT -> do not descend; we can't use indexes through them
        }
        Visit(expr);
        return list;
    }

    /// <summary>
    /// Returns true if <paramref name="expr"/> is exclusively equality comparisons
    /// on a single shared JSON path, joined by OR (or just one comparison alone).
    /// Used by the planner to fold IN(...) and OR-of-equalities into a single
    /// multi-key INDEX_SCAN.
    /// </summary>
    private static bool TryFlattenSamePathOrEqualities(
        Expression expr,
        out string path,
        out List<(object? Value, TokenType ValueType)> values)
    {
        var pathRef = "";
        var collected = new List<(object?, TokenType)>();

        bool Visit(Expression e)
        {
            if (e is ComparisonExpression c)
            {
                if (c.Operator != TokenType.Equals) return false;
                if (pathRef.Length == 0) pathRef = c.JsonPath;
                else if (pathRef != c.JsonPath) return false;
                collected.Add((c.Value, c.ValueType));
                return true;
            }
            if (e is LogicalExpression l && l.Operator == TokenType.Or)
            {
                return Visit(l.Left) && Visit(l.Right);
            }
            return false;
        }

        if (Visit(expr) && collected.Count >= 1)
        {
            path = pathRef;
            values = collected;
            return true;
        }

        path = "";
        values = new List<(object?, TokenType)>();
        return false;
    }

    /// <summary>
    /// Find a composite index whose path list is entirely covered by equality comparisons
    /// in the WHERE clause. Returns the index, composite key, and leftover comparisons.
    /// </summary>
    private (BTreeIndex Index, IndexKey Key, List<ComparisonExpression> Residuals)?
        TryCompositeIndexMatch(string collectionName, List<ComparisonExpression> comparisons)
    {
        var indexes = _indexManager.GetIndexes(collectionName);
        foreach (var idx in indexes)
        {
            if (!idx.Definition.IsComposite) continue;

            var components = new BsonValue[idx.Definition.Paths.Count];
            var matched = new bool[comparisons.Count];
            bool allFound = true;

            for (int i = 0; i < idx.Definition.Paths.Count; i++)
            {
                var path = idx.Definition.Paths[i];
                int found = -1;
                for (int j = 0; j < comparisons.Count; j++)
                {
                    if (matched[j]) continue;
                    var c = comparisons[j];
                    if (c.Operator == TokenType.Equals && c.JsonPath == path)
                    {
                        found = j;
                        break;
                    }
                }
                if (found < 0) { allFound = false; break; }
                matched[found] = true;
                components[i] = ConvertToSearchValue(comparisons[found].Value, comparisons[found].ValueType);
            }

            if (!allFound) continue;

            // Collect residual comparisons (not consumed by the index)
            var residuals = new List<ComparisonExpression>();
            for (int j = 0; j < comparisons.Count; j++)
                if (!matched[j]) residuals.Add(comparisons[j]);

            return (idx, new IndexKey(components), residuals);
        }
        return null;
    }

    private static Expression? BuildResidualFilter(List<ComparisonExpression> residuals)
    {
        if (residuals.Count == 0) return null;
        Expression expr = residuals[0];
        for (int i = 1; i < residuals.Count; i++)
        {
            expr = new LogicalExpression { Left = expr, Operator = TokenType.And, Right = residuals[i] };
        }
        return expr;
    }

    /// <summary>
    /// Hash join: build a hash map of the join collection keyed by the ON column,
    /// then for each outer document, look up matches and combine.
    /// </summary>
    private (List<BsonDocument> docs, string plan) ExecuteHashJoin(SelectStatement stmt, JoinClause join, List<BsonDocument> outerDocs)
    {
        // CROSS JOIN bypasses the hash-join apparatus entirely — it's a
        // pure cartesian product with no key extraction or null padding.
        if (join.Type == JoinType.Cross)
            return ExecuteCrossJoin(stmt, join, outerDocs);

        var joinCollection = _catalog.GetCollection(join.Collection);
        if (joinCollection is null)
        {
            // INNER / RIGHT against a missing inner: nothing to emit.
            // LEFT against a missing inner: every outer row, null-padded.
            // (The collection genuinely doesn't exist — different from
            // existing-but-empty, which the regular path handles.)
            if (join.Type == JoinType.Left)
            {
                var leftPadded = outerDocs
                    .Select(o => CombineDocuments(o, EmptyDoc, stmt.Collection, join.Collection))
                    .ToList();
                return (leftPadded, $"HASH_LEFT_JOIN({join.Collection}:missing)");
            }
            return (new List<BsonDocument>(), $"{JoinPlanLabel(join.Type)}({join.Collection}:missing)");
        }

        // For each predicate determine which side resolves against the
        // outer doc (the running result set) vs the inner (the new
        // collection being joined in). The outer-side path is taken from
        // whichever side does NOT match join.Collection — either the
        // source collection (first join) or a previously-joined collection
        // (chained — its prefixed path "<coll>.<path>" still resolves on
        // the combined doc thanks to JsonPathExtractor's dotted-path
        // support).
        var preds = join.Predicates.Count > 0 ? join.Predicates : new List<JoinPredicate>
        {
            // Back-compat for callers that populated only the legacy single-
            // predicate fields without going through the parser.
            new() { LeftCollection = join.LeftCollection, LeftPath = join.LeftPath,
                    RightCollection = join.RightCollection, RightPath = join.RightPath }
        };
        var (outerPaths, innerPaths) = SplitOuterInnerPaths(preds, join.Collection);

        // Build the inner hash map — single-key Dictionary<BsonValue, ...>
        // for the simple case, multi-key (tuple) for compound ON. Index
        // hint applies to single-key only; compound queries fall back to
        // full collection scan today (an "indexed compound join" is its
        // own much larger feature).
        Dictionary<object, List<BsonDocument>> innerMap;
        string joinStrategy;
        if (innerPaths.Length == 1)
        {
            var single = BuildHashMap(joinCollection, innerPaths[0]);
            innerMap = single.ToDictionary(kvp => (object)kvp.Key, kvp => kvp.Value);
            var innerIndex = _indexManager.FindIndexForPath(join.Collection, innerPaths[0]);
            joinStrategy = innerIndex is not null
                ? $"{JoinPlanLabel(join.Type)}(using idx:{innerIndex.Definition.Name})"
                : $"{JoinPlanLabel(join.Type)}({join.Collection})";
        }
        else
        {
            innerMap = BuildCompositeHashMap(joinCollection, innerPaths);
            joinStrategy = $"{JoinPlanLabel(join.Type)}({join.Collection}, compound:{innerPaths.Length})";
        }

        // Phase A semantics:
        //   INNER  — emit only matched pairs (pre-#17 behaviour).
        //   LEFT   — emit every outer; null-pad if no inner match.
        //   RIGHT  — emit every inner; null-pad if no outer match. To do this
        //            cheaply we track which inner docs were matched as we
        //            walk the outer side, then sweep the unmatched at the end.
        var results = new List<BsonDocument>();
        HashSet<BsonDocument>? matchedInner = join.Type == JoinType.Right
            ? new HashSet<BsonDocument>(ReferenceEqualityComparer.Instance)
            : null;

        foreach (var outerDoc in outerDocs)
        {
            var outerKey = ExtractKey(outerDoc, outerPaths);
            if (outerKey is null)
            {
                if (join.Type == JoinType.Left)
                    results.Add(CombineDocuments(outerDoc, EmptyDoc, stmt.Collection, join.Collection));
                continue;
            }

            if (innerMap.TryGetValue(outerKey, out var matches))
            {
                foreach (var innerDoc in matches)
                {
                    results.Add(CombineDocuments(outerDoc, innerDoc, stmt.Collection, join.Collection));
                    matchedInner?.Add(innerDoc);
                }
            }
            else if (join.Type == JoinType.Left)
            {
                results.Add(CombineDocuments(outerDoc, EmptyDoc, stmt.Collection, join.Collection));
            }
        }

        if (join.Type == JoinType.Right)
        {
            foreach (var innerDocs in innerMap.Values)
                foreach (var innerDoc in innerDocs)
                    if (!matchedInner!.Contains(innerDoc))
                        results.Add(CombineDocuments(EmptyDoc, innerDoc, stmt.Collection, join.Collection));
        }

        return (results, joinStrategy);
    }

    /// <summary>
    /// Pull the outer-side and inner-side paths out of a predicate list.
    /// The "inner side" is whichever path's collection matches the join's
    /// new collection; the "outer side" is the other (could be the source
    /// collection or a previously-joined one). Returns parallel arrays so
    /// the caller can extract paired tuples.
    /// </summary>
    private static (string[] outerPaths, string[] innerPaths) SplitOuterInnerPaths(
        List<JoinPredicate> preds, string innerCollection)
    {
        var outers = new string[preds.Count];
        var inners = new string[preds.Count];
        for (int i = 0; i < preds.Count; i++)
        {
            var p = preds[i];
            bool rightIsInner = string.Equals(p.RightCollection, innerCollection, StringComparison.OrdinalIgnoreCase);
            if (rightIsInner)
            {
                inners[i] = p.RightPath;
                // Outer-side path may be prefixed with its collection name
                // when the doc is a chained-combined doc; we re-attach the
                // prefix here so JsonPathExtractor.Extract finds it either
                // way (bare collection docs have the bare path; combined
                // docs have the prefixed path).
                outers[i] = string.IsNullOrEmpty(p.LeftCollection)
                    ? p.LeftPath
                    : $"{p.LeftCollection}.{p.LeftPath}";
            }
            else
            {
                inners[i] = p.LeftPath;
                outers[i] = string.IsNullOrEmpty(p.RightCollection)
                    ? p.RightPath
                    : $"{p.RightCollection}.{p.RightPath}";
            }
        }
        return (outers, inners);
    }

    /// <summary>Resolve a key from a doc using one or more paths. Returns
    /// null if any single path yields a null value (a NULL component never
    /// matches in SQL semantics). Single-path returns the BsonValue itself;
    /// multi-path returns a string tuple key (cheap and equality-correct).
    ///
    /// <para>
    /// Each path may be prefixed (<c>"orders.flightNumber"</c>) for chained
    /// joins where the doc is combined, or bare (<c>"flightNumber"</c>) for
    /// the first join's bare outer rows. We try the path as-given first;
    /// if null, fall back to the suffix after the first dot. That handles
    /// both shapes cleanly without the executor having to track which
    /// position in the join chain it's at.
    /// </para>
    /// </summary>
    private static object? ExtractKey(BsonDocument doc, string[] paths)
    {
        if (paths.Length == 1)
            return ExtractOne(doc, paths[0]) ?? (object?)null;

        var parts = new BsonValue[paths.Length];
        for (int i = 0; i < paths.Length; i++)
        {
            var v = ExtractOne(doc, paths[i]);
            if (v is null) return null;
            parts[i] = v;
        }
        return string.Join("\x1f", parts.Select(p => p.ToString()));
    }

    private static BsonValue? ExtractOne(BsonDocument doc, string path)
    {
        var v = JsonPathExtractor.Extract(doc, path);
        if (!v.IsNull) return v;

        // Fall back: strip the first dotted segment. Handles "orders.x" on
        // a bare orders row (where the bare path "x" is what's actually there).
        int dot = path.IndexOf('.');
        if (dot > 0 && dot < path.Length - 1)
        {
            var bare = path[(dot + 1)..];
            var v2 = JsonPathExtractor.Extract(doc, bare);
            if (!v2.IsNull) return v2;
        }
        return null;
    }

    private static Dictionary<object, List<BsonDocument>> BuildCompositeHashMap(
        Collection collection, string[] paths)
    {
        var map = new Dictionary<object, List<BsonDocument>>();
        foreach (var doc in collection.FindAll())
        {
            var key = ExtractKey(doc, paths);
            if (key is null) continue;
            if (!map.TryGetValue(key, out var list))
            {
                list = new List<BsonDocument>();
                map[key] = list;
            }
            list.Add(doc);
        }
        return map;
    }

    private (List<BsonDocument> docs, string plan) ExecuteCrossJoin(SelectStatement stmt, JoinClause join, List<BsonDocument> outerDocs)
    {
        var joinCollection = _catalog.GetCollection(join.Collection);
        if (joinCollection is null)
            return (new List<BsonDocument>(), $"CROSS_JOIN({join.Collection}:missing)");

        var innerDocs = joinCollection.FindAll().ToList();
        var results = new List<BsonDocument>(outerDocs.Count * innerDocs.Count);
        foreach (var outer in outerDocs)
            foreach (var inner in innerDocs)
                results.Add(CombineDocuments(outer, inner, stmt.Collection, join.Collection));

        return (results, $"CROSS_JOIN({join.Collection})");
    }

    /// <summary>Reused as the null-padded side in LEFT/RIGHT joins. A single
    /// shared empty doc is fine because <see cref="CombineDocuments"/> only
    /// reads from it and the result is a fresh document.</summary>
    private static readonly BsonDocument EmptyDoc = new();

    private static string JoinPlanLabel(JoinType t) => t switch
    {
        JoinType.Inner => "HASH_JOIN",
        JoinType.Left => "HASH_LEFT_JOIN",
        JoinType.Right => "HASH_RIGHT_JOIN",
        JoinType.Cross => "CROSS_JOIN",
        _ => "JOIN",
    };

    private static Dictionary<BsonValue, List<BsonDocument>> BuildHashMap(Collection collection, string path)
    {
        var map = new Dictionary<BsonValue, List<BsonDocument>>();
        foreach (var doc in collection.FindAll())
        {
            foreach (var val in JsonPathExtractor.ExtractAll(doc, path))
            {
                if (val.IsNull) continue;
                if (!map.TryGetValue(val, out var list))
                {
                    list = new List<BsonDocument>();
                    map[val] = list;
                }
                list.Add(doc);
            }
        }
        return map;
    }

    /// <summary>
    /// Apply aggregations and GROUP BY to a result set.
    /// Without GROUP BY: produces a single row with aggregate values.
    /// With GROUP BY: produces one row per distinct group key with aggregate values.
    /// </summary>
    private static List<BsonDocument> ApplyAggregations(SelectStatement stmt, List<BsonDocument> input)
    {
        if (stmt.GroupByPaths.Count == 0)
        {
            // Single-group aggregation - one result row
            var row = ComputeAggregateRow(stmt.Aggregates, input, new Dictionary<string, BsonValue>());
            return new List<BsonDocument> { row };
        }

        // GROUP BY: bucket documents by group key tuple
        var groups = new Dictionary<string, (Dictionary<string, BsonValue> Key, List<BsonDocument> Docs)>();

        foreach (var doc in input)
        {
            // Build composite key for this row
            var keyValues = new Dictionary<string, BsonValue>();
            var keyParts = new List<string>(stmt.GroupByPaths.Count);
            foreach (var path in stmt.GroupByPaths)
            {
                var v = JsonPathExtractor.Extract(doc, path);
                keyValues[path] = v;
                keyParts.Add(v.ToString());
            }
            var groupKey = string.Join("\u0001", keyParts);

            if (!groups.TryGetValue(groupKey, out var bucket))
            {
                bucket = (keyValues, new List<BsonDocument>());
                groups[groupKey] = bucket;
            }
            bucket.Docs.Add(doc);
        }

        var results = new List<BsonDocument>(groups.Count);
        foreach (var (_, bucket) in groups)
        {
            results.Add(ComputeAggregateRow(stmt.Aggregates, bucket.Docs, bucket.Key));
        }
        return results;
    }

    private static BsonDocument ComputeAggregateRow(
        List<AggregateField> aggregates, List<BsonDocument> docs, Dictionary<string, BsonValue> groupKeys)
    {
        var row = new BsonDocument();

        // Include group key fields in the output
        foreach (var (path, value) in groupKeys)
        {
            row[path] = value;
        }

        foreach (var agg in aggregates)
        {
            row[agg.Alias] = ComputeAggregate(agg, docs);
        }
        return row;
    }

    private static BsonValue ComputeAggregate(AggregateField agg, List<BsonDocument> docs)
    {
        if (agg.Function == AggregateFunction.Count)
        {
            if (agg.Path == "*")
                return BsonValue.FromInt64(docs.Count);
            // COUNT(path) counts non-null occurrences
            long c = 0;
            foreach (var d in docs)
                if (!JsonPathExtractor.Extract(d, agg.Path).IsNull) c++;
            return BsonValue.FromInt64(c);
        }

        double sum = 0;
        long count = 0;
        double min = double.MaxValue;
        double max = double.MinValue;

        foreach (var d in docs)
        {
            var v = JsonPathExtractor.Extract(d, agg.Path);
            if (v.IsNull || !v.IsNumeric) continue;
            var n = v.ToDouble();
            sum += n;
            count++;
            if (n < min) min = n;
            if (n > max) max = n;
        }

        if (count == 0) return BsonValue.Null;
        return agg.Function switch
        {
            AggregateFunction.Sum => BsonValue.FromDouble(sum),
            AggregateFunction.Avg => BsonValue.FromDouble(sum / count),
            AggregateFunction.Min => BsonValue.FromDouble(min),
            AggregateFunction.Max => BsonValue.FromDouble(max),
            _ => BsonValue.Null
        };
    }

    /// <summary>
    /// Combine two documents into one by nesting under collection names.
    /// Result: { "orders": {...}, "flights": {...} }
    /// So queries like SELECT orders.pnr, flights.flightNumber work naturally via path extraction.
    /// </summary>
    private static BsonDocument CombineDocuments(BsonDocument outerDoc, BsonDocument innerDoc,
        string outerCollection, string innerCollection)
    {
        // Chained joins (#44 Phase B): the second join's outer is already a
        // combined doc {a: ..., b: ...} from join #1. We detect this by
        // checking whether outerDoc already has outerCollection as a field —
        // if yes, copy its existing fields through (preserving a, b, ...)
        // and just add the new inner. If no, wrap both as before.
        var combined = new BsonDocument();
        if (outerDoc.ContainsKey(outerCollection))
        {
            // Already-combined outer: copy each top-level (collection-named)
            // field through, then add the new inner collection alongside.
            foreach (var key in outerDoc.Keys)
                combined[key] = outerDoc[key];
        }
        else
        {
            combined[outerCollection] = BsonValue.FromDocument(outerDoc);
        }
        combined[innerCollection] = BsonValue.FromDocument(innerDoc);
        return combined;
    }

    private BsonValue ConvertToSearchValue(object? value, TokenType type)
    {
        // ValueExpression case: a function call (NEWID(), GETDATE()) or a path
        // captured in a value position. Evaluate against no document — the
        // index-probe call sites have no row context, so a path-in-value here
        // would throw. That's intentional; an indexed lookup of `WHERE x = y.z`
        // is ambiguous (which row's `y.z`?) and the user wants either a literal,
        // a constant function, or a row-scoped UPDATE SET (which goes through
        // the ConvertToSearchValueFor variant below).
        if (type == TokenType.ValueExpression && value is ValueExpression vexpr)
            return ValueEvaluator.Evaluate(vexpr, docContext: null);

        return type switch
        {
            TokenType.StringLiteral => BsonValue.FromString((string)value!),
            TokenType.NumberLiteral => value is double d && d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue
                ? BsonValue.FromInt32((int)d)
                : BsonValue.FromDouble((double)value!),
            TokenType.True => BsonValue.FromBool(true),
            TokenType.False => BsonValue.FromBool(false),
            TokenType.Null => BsonValue.Null,
            _ => BsonValue.Null
        };
    }

    /// <summary>
    /// Row-scoped variant of <see cref="ConvertToSearchValue"/>. Used by UPDATE
    /// SET so <c>UPDATE users SET name = LOWER(name)</c> can resolve the path
    /// argument against the row being updated.
    /// </summary>
    private BsonValue ConvertToSearchValueFor(object? value, TokenType type, BsonDocument doc)
    {
        if (type == TokenType.ValueExpression && value is ValueExpression vexpr)
            return ValueEvaluator.Evaluate(vexpr, docContext: doc);
        return ConvertToSearchValue(value, type);
    }

    private QueryResult ExecuteInsert(InsertStatement stmt)
    {
        BsonDocument doc;
        if (stmt.Columns.Count > 0)
        {
            // SQL-tuple form: build a fresh document by evaluating each
            // ValueExpression. No row context (this is INSERT, there's no
            // pre-existing row), so PathValueExpression args would throw —
            // that's the right behaviour: `INSERT INTO t (x) VALUES (y.z)`
            // is meaningless without a source row, and the parser doesn't
            // accept it via SELECT-INTO yet.
            doc = new BsonDocument();
            for (int i = 0; i < stmt.Columns.Count; i++)
            {
                var val = ValueEvaluator.Evaluate(stmt.Values[i], docContext: null);
                doc[stmt.Columns[i]] = val;
            }
        }
        else
        {
            // Classic JSON-literal form. No function-call support here —
            // BsonDocument.FromJson is a strict JSON parser and we don't
            // pre-process its input.
            doc = BsonDocument.FromJson(stmt.JsonDocument);
        }

        var collection = _catalog.GetOrCreateCollection(stmt.Collection);
        // Pre-validate uniqueness so a function-generated value (e.g. a NEWID
        // collision against an existing _id, vanishingly rare but possible if
        // someone used a fixed-seed RNG client-side and then NEWID() from
        // server) doesn't half-commit. Mirrors the discipline DocumentForgeDb
        // uses on its own Insert path.
        _indexManager.ValidateUniqueInsert(stmt.Collection, doc);
        var id = collection.Insert(doc);
        _indexManager.OnDocumentInserted(stmt.Collection, id, doc);
        return QueryResult.Affected(1, $"Inserted document {id}");
    }

    private QueryResult ExecuteUpdate(UpdateStatement stmt)
    {
        var collection = _catalog.GetCollection(stmt.Collection);
        if (collection is null)
            return QueryResult.Affected(0, "Collection not found");

        long updated = 0;
        var toUpdate = new List<(DocumentId id, BsonDocument doc)>();

        foreach (var doc in collection.FindAll())
        {
            if (stmt.Where is null || ExpressionEvaluator.Evaluate(stmt.Where, doc))
                toUpdate.Add((doc.GetId(), doc));
        }

        foreach (var (id, oldDoc) in toUpdate)
        {
            var newDoc = CloneDocument(oldDoc);
            foreach (var clause in stmt.SetClauses)
            {
                // Pass the OLD doc as the path-resolution context so
                // `SET name = LOWER(name)` reads the pre-update value, not
                // the half-built newDoc.
                var val = ConvertToSearchValueFor(clause.Value, clause.ValueType, oldDoc);
                SetNestedValue(newDoc, clause.Path, val);
            }
            if (collection.Update(id, newDoc))
            {
                _indexManager.OnDocumentUpdated(stmt.Collection, id, oldDoc, newDoc);
                updated++;
            }
        }

        return QueryResult.Affected(updated, $"Updated {updated} document(s)");
    }

    private QueryResult ExecuteDelete(DeleteStatement stmt)
    {
        var collection = _catalog.GetCollection(stmt.Collection);
        if (collection is null)
            return QueryResult.Affected(0, "Collection not found");

        var toDelete = new List<(DocumentId id, BsonDocument doc)>();

        foreach (var doc in collection.FindAll())
        {
            if (stmt.Where is null || ExpressionEvaluator.Evaluate(stmt.Where, doc))
                toDelete.Add((doc.GetId(), doc));
        }

        long deleted = 0;
        foreach (var (id, doc) in toDelete)
        {
            if (collection.Delete(id))
            {
                _indexManager.OnDocumentDeleted(stmt.Collection, id, doc);
                deleted++;
            }
        }

        return QueryResult.Affected(deleted, $"Deleted {deleted} document(s)");
    }

    private QueryResult ExecuteCreateIndex(CreateIndexStatement stmt)
    {
        var collection = _catalog.GetCollection(stmt.Collection);
        if (collection is null)
            return QueryResult.Error($"Collection '{stmt.Collection}' not found. Insert data first.", QueryResult.Codes.CollectionNotFound);

        var definition = new IndexDefinition
        {
            Name = stmt.IndexName,
            CollectionName = new CollectionName(stmt.Collection),
            JsonPath = stmt.JsonPath,
            IsUnique = stmt.IsUnique
        };

        var index = _indexManager.CreateIndex(definition);
        _indexManager.RebuildIndex(index, collection);

        return QueryResult.Affected(index.TotalEntries,
            $"Created index '{stmt.IndexName}' on {stmt.Collection}({stmt.JsonPath}) with {index.TotalEntries} entries");
    }

    private QueryResult ExecuteDropIndex(DropIndexStatement stmt)
    {
        _indexManager.DropIndex(stmt.IndexName);
        return QueryResult.Affected(1, $"Dropped index '{stmt.IndexName}'");
    }

    private QueryResult ExecuteCount(CountStatement stmt)
    {
        var collection = _catalog.GetCollection(stmt.Collection);
        if (collection is null)
            return QueryResult.Ok(new List<BsonDocument> { CreateCountDoc(0) });

        long count;
        if (stmt.Where is null)
        {
            count = collection.FindAll().Count();
        }
        else
        {
            count = collection.FindAll().Count(doc => ExpressionEvaluator.Evaluate(stmt.Where, doc));
        }

        return QueryResult.Ok(new List<BsonDocument> { CreateCountDoc(count) });
    }

    private static BsonDocument CreateCountDoc(long count)
    {
        var doc = new BsonDocument();
        doc["count"] = BsonValue.FromInt64(count);
        return doc;
    }

    private static BsonDocument ProjectFields(BsonDocument doc, List<string> fields, bool includeId = true)
    {
        var projected = new BsonDocument();
        if (includeId)
        {
            var id = doc["_id"];
            if (!id.IsNull) projected["_id"] = id;
        }

        foreach (var field in fields)
        {
            var value = JsonPathExtractor.Extract(doc, field);
            if (!value.IsNull)
                projected[field] = value;
        }
        return projected;
    }

    private static BsonDocument CloneDocument(BsonDocument doc)
    {
        // Serialize and deserialize to clone
        var json = doc.ToJson();
        return BsonDocument.FromJson(json);
    }

    private static void SetNestedValue(BsonDocument doc, string path, BsonValue value)
    {
        var parts = path.Split('.');
        if (parts.Length == 1)
        {
            doc[path] = value;
            return;
        }

        var current = doc;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var val = current[parts[i]];
            if (val.Type == BsonType.Document)
                current = val.AsDocument;
            else
                return; // can't set nested value on non-document
        }
        current[parts[^1]] = value;
    }
}
