using DocumentForge.Core;
using DocumentForge.Document;
using DocumentForge.Index;
using DocumentForge.Query;

namespace DocumentForge.Engine.Cluster;

/// <summary>
/// Plans and executes data migration when the cluster topology changes.
///
/// The typical flow is:
///   1. Generate a new ClusterConfig with the target shard list
///   2. Call <see cref="Plan"/> to get a migration plan (how many docs move where)
///   3. Review the plan, then call <see cref="Execute"/> during a maintenance window
///
/// The current implementation is OFFLINE: it reads each doc from its current shard,
/// writes it to the correct shard, deletes it from the old one. All shards must be
/// reachable and writeable during execution. A future version will add online
/// rebalancing with dual-read fallback.
/// </summary>
public sealed class ClusterRebalancer
{
    public sealed class MigrationPlan
    {
        public ClusterConfig OldConfig { get; init; } = new();
        public ClusterConfig NewConfig { get; init; } = new();
        /// <summary>Per-collection plan.</summary>
        public List<CollectionMigration> Collections { get; init; } = new();
        public long TotalDocumentsToMove => Collections.Sum(c => c.EstimatedMovedDocs);
    }

    public sealed class CollectionMigration
    {
        public string CollectionName { get; init; } = "";
        public string? ShardKeyPath { get; init; }
        public long EstimatedMovedDocs { get; init; }
        public Dictionary<(string From, string To), long> Moves { get; init; } = new();
    }

    public sealed class Progress
    {
        public string CollectionName { get; init; } = "";
        public string FromShard { get; init; } = "";
        public string ToShard { get; init; } = "";
        public long DocsMoved { get; init; }
        public long DocsScanned { get; init; }

        /// <summary>Documents whose source-shard delete failed (or whose _id
        /// couldn't be resolved) — they were NOT moved and may now exist on
        /// both shards. Non-zero means the operator should investigate.</summary>
        public long MoveFailures { get; init; }
    }

    /// <summary>Resolve a document's id whether it arrived in-process (typed
    /// ObjectId) or over HTTP (JSON round-trip turns _id into a hex STRING —
    /// GetId() returns Empty for those, which made HTTP-transport rebalances
    /// delete nothing and duplicate every moved doc).</summary>
    private static DocumentId ResolveId(DocumentForge.Document.BsonDocument doc)
    {
        var id = doc.GetId();
        if (!id.IsEmpty) return id;
        var raw = doc["_id"];
        if (raw.Type == DocumentForge.Document.BsonType.String && Guid.TryParse(raw.AsString, out var g))
            return new DocumentId(g);
        return DocumentId.Empty;
    }

    /// <summary>
    /// Produce a plan by sampling each collection and computing how many docs would
    /// move from each shard to each other shard under the new topology.
    /// This requires all shards in the OLD config to be reachable.
    /// </summary>
    public static MigrationPlan Plan(
        ClusterConfig oldConfig, ClusterConfig newConfig,
        Func<ShardDescriptor, IShardTransport> transportFactory)
    {
        var plan = new MigrationPlan { OldConfig = oldConfig, NewConfig = newConfig };

        var oldRing = new ConsistentHashRing(oldConfig.Shards.Select(s => s.Name).ToList(),
                                             oldConfig.VirtualNodesPerShard);
        var newRing = new ConsistentHashRing(newConfig.Shards.Select(s => s.Name).ToList(),
                                             newConfig.VirtualNodesPerShard);

        using var oldShards = new DisposableShards(oldConfig.Shards.Select(transportFactory).ToList());

        foreach (var (collName, policy) in oldConfig.Collections)
        {
            if (policy.Strategy != ShardingStrategy.Hash || policy.ShardKeyPath is null)
                continue; // Replicated collections don't move

            var mig = new CollectionMigration { CollectionName = collName, ShardKeyPath = policy.ShardKeyPath };
            long movedTotal = 0;

            for (int i = 0; i < oldShards.Items.Count; i++)
            {
                var shard = oldShards.Items[i];
                var fromName = oldConfig.Shards[i].Name;
                var result = shard.Execute($"SELECT * FROM {collName}");
                foreach (var doc in result.Documents)
                {
                    var keyVal = JsonPathExtractor.Extract(doc, policy.ShardKeyPath).ToString();
                    int newIdx = newRing.PickShardIndex(keyVal);
                    var toName = newConfig.Shards[newIdx].Name;
                    if (!fromName.Equals(toName, StringComparison.OrdinalIgnoreCase))
                    {
                        movedTotal++;
                        var key = (fromName, toName);
                        mig.Moves[key] = mig.Moves.GetValueOrDefault(key) + 1;
                    }
                }
            }

            // Re-init collection migration with final count (init-only prop pattern)
            plan.Collections.Add(new CollectionMigration
            {
                CollectionName = mig.CollectionName,
                ShardKeyPath = mig.ShardKeyPath,
                EstimatedMovedDocs = movedTotal,
                Moves = mig.Moves
            });
        }

        return plan;
    }

    /// <summary>
    /// Execute the migration. Callers should ensure the cluster is in a maintenance
    /// window - new writes during execution will land based on the NEW ring, which is
    /// what we want; but reads may transiently see stale data on old shards for
    /// documents not yet migrated.
    /// </summary>
    public static void Execute(
        MigrationPlan plan,
        Func<ShardDescriptor, IShardTransport> transportFactory,
        Action<Progress>? onProgress = null)
    {
        var oldRing = new ConsistentHashRing(plan.OldConfig.Shards.Select(s => s.Name).ToList(),
                                             plan.OldConfig.VirtualNodesPerShard);
        var newRing = new ConsistentHashRing(plan.NewConfig.Shards.Select(s => s.Name).ToList(),
                                             plan.NewConfig.VirtualNodesPerShard);

        using var oldShards = new DisposableShards(plan.OldConfig.Shards.Select(transportFactory).ToList());
        using var newShards = new DisposableShards(plan.NewConfig.Shards.Select(transportFactory).ToList());

        var newShardByName = plan.NewConfig.Shards
            .Select((s, i) => (s.Name, idx: i))
            .ToDictionary(x => x.Name, x => x.idx, StringComparer.OrdinalIgnoreCase);

        foreach (var migration in plan.Collections)
        {
            if (migration.ShardKeyPath is null) continue;

            for (int srcIdx = 0; srcIdx < oldShards.Items.Count; srcIdx++)
            {
                var srcShard = oldShards.Items[srcIdx];
                var fromName = plan.OldConfig.Shards[srcIdx].Name;
                var result = srcShard.Execute($"SELECT * FROM {migration.CollectionName}");

                long scanned = 0, moved = 0, failures = 0;
                foreach (var doc in result.Documents)
                {
                    scanned++;
                    var keyVal = JsonPathExtractor.Extract(doc, migration.ShardKeyPath).ToString();
                    int newIdx = newRing.PickShardIndex(keyVal);
                    var toName = plan.NewConfig.Shards[newIdx].Name;
                    if (fromName.Equals(toName, StringComparison.OrdinalIgnoreCase)) continue;

                    // Move: write on new shard, delete from old — in that order,
                    // so a failure can only ever duplicate a doc, never lose one.
                    var docId = ResolveId(doc);
                    if (docId.IsEmpty) { failures++; continue; }
                    newShards.Items[newIdx].Insert(migration.CollectionName, doc);
                    if (!srcShard.DeleteById(migration.CollectionName, docId)) { failures++; continue; }
                    moved++;

                    if (moved % 1000 == 0)
                        onProgress?.Invoke(new Progress {
                            CollectionName = migration.CollectionName,
                            FromShard = fromName, ToShard = "(many)",
                            DocsMoved = moved, DocsScanned = scanned, MoveFailures = failures
                        });
                }

                onProgress?.Invoke(new Progress {
                    CollectionName = migration.CollectionName,
                    FromShard = fromName, ToShard = "(done)",
                    DocsMoved = moved, DocsScanned = scanned, MoveFailures = failures
                });
            }
        }
    }

    private sealed class DisposableShards : IDisposable
    {
        public List<IShardTransport> Items { get; }
        public DisposableShards(List<IShardTransport> items) { Items = items; }
        public void Dispose() { foreach (var s in Items) try { s.Dispose(); } catch { } }
    }

    // =====================================================================
    //  Online rebalancing
    // =====================================================================

    /// <summary>
    /// Start an online rebalance: enables migration mode on the cluster (dual-read
    /// fallback), then copies misplaced documents off the calling thread without
    /// blocking writes. Clients continue inserting and querying throughout. When
    /// the returned Task completes, all misplaced documents have been moved and
    /// callers should call <see cref="CompleteOnlineRebalance"/> to drop the
    /// previous ring.
    ///
    /// <para>
    /// Pre-fix this was declared <c>async</c> but never awaited anything, so the
    /// CS1998 warning aside, awaiting it ran the entire rebalance synchronously
    /// on the calling thread. Now the body runs through <see cref="Task.Run"/>
    /// and awaits genuinely yield. <see cref="RunOnline"/> is the synchronous
    /// shape if you want explicit control over thread placement.
    /// </para>
    /// </summary>
    public static Task<OnlineRebalanceReport> RunOnlineAsync(
        DocumentForgeCluster cluster,
        ClusterConfig oldConfig,
        ClusterConfig newConfig,
        List<IShardTransport> previousShards,
        Action<Progress>? onProgress = null,
        CancellationToken ct = default)
    {
        return Task.Run(
            () => RunOnline(cluster, oldConfig, newConfig, previousShards, onProgress, ct),
            ct);
    }

    /// <summary>
    /// Synchronous form of <see cref="RunOnlineAsync"/>. Runs to completion on
    /// the calling thread; throw <see cref="OperationCanceledException"/> if
    /// <paramref name="ct"/> is cancelled mid-walk. Callers who want the work
    /// off-thread should use the async wrapper.
    /// </summary>
    public static OnlineRebalanceReport RunOnline(
        DocumentForgeCluster cluster,
        ClusterConfig oldConfig,
        ClusterConfig newConfig,
        List<IShardTransport> previousShards,
        Action<Progress>? onProgress = null,
        CancellationToken ct = default)
    {
        cluster.EnableMigrationMode(previousShards);

        var report = new OnlineRebalanceReport { StartedAt = DateTime.UtcNow };

        try
        {
            var newRing = new ConsistentHashRing(
                newConfig.Shards.Select(s => s.Name).ToList(),
                newConfig.VirtualNodesPerShard);
            var oldRing = new ConsistentHashRing(
                oldConfig.Shards.Select(s => s.Name).ToList(),
                oldConfig.VirtualNodesPerShard);

            var shardByName = cluster.Shards.ToDictionary(s => s.ShardName, s => s, StringComparer.OrdinalIgnoreCase);

            foreach (var (collName, policy) in oldConfig.Collections)
            {
                if (policy.Strategy != ShardingStrategy.Hash || policy.ShardKeyPath is null)
                    continue;

                for (int srcIdx = 0; srcIdx < previousShards.Count; srcIdx++)
                {
                    ct.ThrowIfCancellationRequested();
                    var srcShard = previousShards[srcIdx];
                    var fromName = oldConfig.Shards[srcIdx].Name;

                    var result = srcShard.Execute($"SELECT * FROM {collName}");
                    long scanned = 0, moved = 0;

                    foreach (var doc in result.Documents)
                    {
                        ct.ThrowIfCancellationRequested();
                        scanned++;
                        var keyVal = JsonPathExtractor.Extract(doc, policy.ShardKeyPath).ToString();
                        int newIdx = newRing.PickShardIndex(keyVal);
                        var toName = newConfig.Shards[newIdx].Name;
                        if (fromName.Equals(toName, StringComparison.OrdinalIgnoreCase)) continue;

                        if (!shardByName.TryGetValue(toName, out var destShard)) continue;

                        var docId = ResolveId(doc);
                        if (docId.IsEmpty) continue;

                        // Idempotent: if the doc was already written by a client, our DeleteById
                        // on srcShard succeeds (even without a live copy there it's fine) and
                        // the destination may already have a newer version.
                        try
                        {
                            destShard.Insert(collName, doc);
                        }
                        catch
                        {
                            // Destination has it (possibly a newer version from a client write)
                            // - don't overwrite, just clean up the source.
                        }
                        srcShard.DeleteById(collName, docId);
                        moved++;
                        report.TotalMoved++;

                        if (moved % 500 == 0)
                            onProgress?.Invoke(new Progress {
                                CollectionName = collName,
                                FromShard = fromName, ToShard = "(many)",
                                DocsMoved = moved, DocsScanned = scanned
                            });
                    }

                    onProgress?.Invoke(new Progress {
                        CollectionName = collName, FromShard = fromName, ToShard = "(done)",
                        DocsMoved = moved, DocsScanned = scanned
                    });
                }
            }

            report.CompletedAt = DateTime.UtcNow;
            return report;
        }
        catch
        {
            report.CompletedAt = DateTime.UtcNow;
            throw;
        }
    }

    /// <summary>
    /// Call after <see cref="RunOnlineAsync"/> returns successfully. Drops the previous
    /// ring so reads no longer pay the dual-scan cost, and disposes the previous transports.
    /// </summary>
    public static void CompleteOnlineRebalance(DocumentForgeCluster cluster, List<IShardTransport> previousShards)
    {
        cluster.DisableMigrationMode();
        foreach (var s in previousShards) try { s.Dispose(); } catch { }
    }

    public sealed class OnlineRebalanceReport
    {
        public DateTime StartedAt { get; set; }
        public DateTime CompletedAt { get; set; }
        public long TotalMoved { get; set; }
        public TimeSpan Duration => CompletedAt - StartedAt;
    }
}
