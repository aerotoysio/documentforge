using DocumentForge.Engine;
using DocumentForge.Engine.Cluster;

namespace DocumentForge.Cli.Commands;

// ----- dfdb inspect -----
public static class InspectCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 1) return UiHelpers.Fail("Usage: dfdb inspect <db-file>");
        using var db = DocumentForgeDb.Open(args[0]);
        var s = db.GetStatistics();

        UiHelpers.Header("Database inspection");
        UiHelpers.KV("File", s.FilePath);
        UiHelpers.KV("Size", UiHelpers.FormatSize(s.FileSize));
        UiHelpers.KV("Pages", s.PageCount.ToString("N0"));
        UiHelpers.KV("Cache", $"{s.CachedPages:N0} pages ({s.DirtyPages} dirty)");
        Console.WriteLine();
        UiHelpers.Header($"Collections ({s.Collections.Count})");
        foreach (var c in s.Collections)
        {
            Console.WriteLine($"  {UiHelpers.Blue(c.Name),-25}  {c.DocumentCount,12:N0} docs   {c.IndexCount} index(es)");
            foreach (var idx in c.Indexes)
                Console.WriteLine($"    {UiHelpers.Gray("├─")} {idx.Name,-30} ({idx.JsonPath}) {idx.EntryCount,10:N0} entries{(idx.IsUnique ? UiHelpers.Red(" UNIQUE") : "")}");
        }
        return 0;
    }
}

// ----- dfdb query -----
public static class QueryCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 2) return UiHelpers.Fail("Usage: dfdb query <db-file> \"<sql>\"");
        using var db = DocumentForgeDb.Open(args[0]);
        var r = db.Execute(args[1]);
        if (!r.Success) return UiHelpers.Fail(r.Message ?? "query failed");

        UiHelpers.Header("Query result");
        UiHelpers.KV("Plan", r.QueryPlan ?? "(none)");
        UiHelpers.KV("Time", $"{r.ExecutionTime.TotalMilliseconds:F2}ms");
        UiHelpers.KV("Rows", r.Documents.Count.ToString("N0"));
        if (!string.IsNullOrEmpty(r.Message)) UiHelpers.KV("Message", r.Message);
        Console.WriteLine();

        int shown = 0;
        foreach (var d in r.Documents)
        {
            if (shown++ >= 20) { Console.WriteLine(UiHelpers.Gray($"  ... and {r.Documents.Count - 20} more")); break; }
            Console.WriteLine(d.ToJson());
        }
        return 0;
    }
}

// ----- dfdb compact -----
public static class CompactCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 2) return UiHelpers.Fail("Usage: dfdb compact <db-file> <collection>");
        using var db = DocumentForgeDb.Open(args[0]);
        var before = new FileInfo(args[0]).Length;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = db.Compact(args[1]);
        sw.Stop();
        db.Flush();
        var after = new FileInfo(args[0]).Length;
        UiHelpers.Header("Compaction result");
        UiHelpers.KV("Pages compacted", r.PagesCompacted.ToString("N0"));
        UiHelpers.KV("Bytes reclaimed", UiHelpers.FormatSize(r.BytesReclaimed));
        UiHelpers.KV("File before/after", $"{UiHelpers.FormatSize(before)} → {UiHelpers.FormatSize(after)}");
        UiHelpers.KV("Time", $"{sw.Elapsed.TotalSeconds:F2}s");
        return 0;
    }
}

// ----- dfdb cluster -----
public static class ClusterCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 2) return UiHelpers.Fail("Usage: dfdb cluster init|show|add-shard|add-collection <config> ...");
        var sub = args[0].ToLowerInvariant();
        var configPath = args[1];

        switch (sub)
        {
            case "init":
                if (ClusterConfig.Exists(configPath)) return UiHelpers.Fail($"Config already exists at {configPath}");
                new ClusterConfig().Save(configPath);
                Console.WriteLine($"{UiHelpers.Green("✓")} Created empty cluster config at {configPath}");
                Console.WriteLine($"  Next: dfdb cluster add-shard {configPath} <name> <endpoint>");
                return 0;

            case "show":
            {
                var cfg = ClusterConfig.Load(configPath);
                UiHelpers.Header($"Cluster Config v{cfg.Version}  ({configPath})");
                UiHelpers.KV("Virtual nodes/shard", cfg.VirtualNodesPerShard.ToString());
                Console.WriteLine();
                UiHelpers.Header($"Shards ({cfg.Shards.Count})");
                foreach (var s in cfg.Shards)
                    Console.WriteLine($"  {UiHelpers.Blue(s.Name),-25} {s.LeaderEndpoint}" +
                                      (s.Followers.Count > 0 ? $" (+{s.Followers.Count} followers)" : ""));
                Console.WriteLine();
                UiHelpers.Header($"Collections ({cfg.Collections.Count})");
                foreach (var (name, pol) in cfg.Collections)
                {
                    var strat = pol.Strategy == ShardingStrategy.Replicated ? "replicated" : $"hash on {pol.ShardKeyPath}";
                    Console.WriteLine($"  {UiHelpers.Blue(name),-25} {strat}");
                }
                return 0;
            }

            case "add-shard":
            {
                if (args.Length < 4) return UiHelpers.Fail("Usage: dfdb cluster add-shard <config> <name> <endpoint>");
                var cfg = ClusterConfig.Load(configPath);
                cfg.Shards.Add(new ShardDescriptor { Name = args[2], Endpoint = args[3], Leader = args[3] });
                cfg.Version++;
                cfg.Save(configPath);
                Console.WriteLine($"{UiHelpers.Green("✓")} Added shard '{args[2]}' → {args[3]} (config version {cfg.Version})");
                return 0;
            }

            case "add-collection":
            {
                if (args.Length < 4) return UiHelpers.Fail("Usage: dfdb cluster add-collection <config> <name> (hash <path>|replicated)");
                var cfg = ClusterConfig.Load(configPath);
                var collName = args[2];
                if (args[3] == "replicated")
                    cfg.Collections[collName] = new CollectionPolicyDescriptor { Strategy = ShardingStrategy.Replicated };
                else if (args[3] == "hash" && args.Length >= 5)
                    cfg.Collections[collName] = new CollectionPolicyDescriptor { Strategy = ShardingStrategy.Hash, ShardKeyPath = args[4] };
                else return UiHelpers.Fail("Expected 'hash <path>' or 'replicated'");
                cfg.Version++;
                cfg.Save(configPath);
                Console.WriteLine($"{UiHelpers.Green("✓")} Configured collection '{collName}' (config version {cfg.Version})");
                return 0;
            }

            default: return UiHelpers.Fail($"Unknown cluster subcommand '{sub}'");
        }
    }
}

// ----- dfdb health -----
public static class HealthCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 1) return UiHelpers.Fail("Usage: dfdb health <cluster-config>");
        var cfg = ClusterConfig.Load(args[0]);
        UiHelpers.Header($"Cluster health ({cfg.Shards.Count} shards)");
        foreach (var shard in cfg.Shards)
        {
            try
            {
                using var t = new HttpShardTransport(shard.Name, shard.LeaderEndpoint);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var stats = t.GetStatistics();
                sw.Stop();
                Console.WriteLine($"  {UiHelpers.Green("●")} {UiHelpers.Blue(shard.Name),-25} {shard.LeaderEndpoint,-40} {UiHelpers.FormatSize(stats.FileSize),10}  ({sw.Elapsed.TotalMilliseconds:F0}ms)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {UiHelpers.Red("●")} {UiHelpers.Blue(shard.Name),-25} {shard.LeaderEndpoint,-40} {UiHelpers.Red("UNREACHABLE")}: {ex.Message}");
            }
        }
        return 0;
    }
}

// ----- dfdb rebalance -----
public static class RebalanceCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 2) return UiHelpers.Fail("Usage: dfdb rebalance <old-config> <new-config> [--plan-only]");
        var oldCfg = ClusterConfig.Load(args[0]);
        var newCfg = ClusterConfig.Load(args[1]);
        bool planOnly = args.Contains("--plan-only");

        IShardTransport Make(ShardDescriptor d) => new HttpShardTransport(d.Name, d.LeaderEndpoint);

        Console.WriteLine("Scanning current topology...");
        var plan = ClusterRebalancer.Plan(oldCfg, newCfg, Make);

        UiHelpers.Header("Migration plan");
        UiHelpers.KV("Total docs to move", plan.TotalDocumentsToMove.ToString("N0"));
        Console.WriteLine();
        foreach (var coll in plan.Collections)
        {
            Console.WriteLine($"  {UiHelpers.Blue(coll.CollectionName)}  ({coll.EstimatedMovedDocs:N0} moves)");
            foreach (var ((from, to), count) in coll.Moves.OrderBy(k => k.Key.From))
                Console.WriteLine($"    {from} → {to}: {count:N0}");
        }

        if (planOnly)
        {
            Console.WriteLine();
            Console.WriteLine(UiHelpers.Gray("Plan-only run. Omit --plan-only to execute."));
            return 0;
        }

        Console.WriteLine();
        Console.Write("Execute this migration? (type 'yes' to proceed): ");
        if (Console.ReadLine()?.Trim() != "yes") { Console.WriteLine("Aborted."); return 0; }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        ClusterRebalancer.Execute(plan, Make, progress =>
            Console.WriteLine($"  {progress.CollectionName,-20} {progress.FromShard,-10} {UiHelpers.Gray("moved")} {progress.DocsMoved,8:N0} / scanned {progress.DocsScanned,8:N0}"));
        sw.Stop();

        Console.WriteLine();
        Console.WriteLine($"{UiHelpers.Green("✓")} Migration complete in {sw.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"  Distribute the new config to every node: cp {args[1]} ...");
        return 0;
    }
}
