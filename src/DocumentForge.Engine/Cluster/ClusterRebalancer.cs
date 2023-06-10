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

                long scanned = 0, moved = 0;
                foreach (var doc in result.Documents)
                {
                    scanned++;
                    var keyVal = JsonPathExtractor.Extract(doc, migration.ShardKeyPath).ToString();
                    int newIdx = newRing.PickShardIndex(keyVal);
                    var toName = plan.NewConfig.Shards[newIdx].Name;
                    if (fromName.Equals(toName, StringComparison.OrdinalIgnoreCase)) continue;

                    // Move: write on new shard, delete from old.
                    // (Best-effort: if the dest write fails we abort - doc still on old shard.)
                    var docId = doc.GetId();
                    newShards.Items[newIdx].Insert(migration.CollectionName, doc);
                    srcShard.DeleteById(migration.CollectionName, docId);
                    moved++;

                    if (moved % 1000 == 0)
                        onProgress?.Invoke(new Progress {
                            CollectionName = migration.CollectionName,
                            FromShard = fromName, ToShard = "(many)",
                            DocsMoved = moved, DocsScanned = scanned
                        });
                }

                onProgress?.Invoke(new Progress {
                    CollectionName = migration.CollectionName,
                    FromShard = fromName, ToShard = "(done)",
                    DocsMoved = moved, DocsScanned = scanned
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
}
