using DocumentForge.Engine;
using DocumentForge.Engine.Cluster;

if (args.Length == 0)
{
    PrintHelp();
    return 0;
}

try
{
    return args[0].ToLowerInvariant() switch
    {
        "inspect"       => CmdInspect(args.Skip(1).ToArray()),
        "query"         => CmdQuery(args.Skip(1).ToArray()),
        "compact"       => CmdCompact(args.Skip(1).ToArray()),
        "cluster"       => CmdCluster(args.Skip(1).ToArray()),
        "health"        => CmdHealth(args.Skip(1).ToArray()),
        "rebalance"     => CmdRebalance(args.Skip(1).ToArray()),
        "help" or "--help" or "-h" => PrintHelp(),
        _               => Fail($"Unknown command '{args[0]}'. Try 'dfctl help'."),
    };
}
catch (Exception ex)
{
    return Fail(ex.Message);
}

// ---- Commands ----

static int CmdInspect(string[] args)
{
    if (args.Length < 1) return Fail("Usage: dfctl inspect <db-file>");
    using var db = DocumentForgeDb.Open(args[0]);
    var s = db.GetStatistics();

    Header("Database Inspection");
    KV("File",       s.FilePath);
    KV("Size",       FormatSize(s.FileSize));
    KV("Pages",      s.PageCount.ToString("N0"));
    KV("Cache",      $"{s.CachedPages:N0} pages ({s.DirtyPages} dirty)");
    Console.WriteLine();
    Header($"Collections ({s.Collections.Count})");
    foreach (var c in s.Collections)
    {
        Console.WriteLine($"  {Blue(c.Name),-20}  {c.DocumentCount,12:N0} docs   {c.IndexCount} index(es)");
        foreach (var idx in c.Indexes)
            Console.WriteLine($"    {Gray("├─")} {idx.Name,-30} ({idx.JsonPath}) {idx.EntryCount,10:N0} entries{(idx.IsUnique ? Red(" UNIQUE") : "")}");
    }
    return 0;
}

static int CmdQuery(string[] args)
{
    if (args.Length < 2) return Fail("Usage: dfctl query <db-file> \"<sql>\"");
    using var db = DocumentForgeDb.Open(args[0]);
    var result = db.Execute(args[1]);

    if (!result.Success) return Fail(result.Message ?? "query failed");

    Header("Query Result");
    KV("Plan",      result.QueryPlan ?? "(none)");
    KV("Time",      $"{result.ExecutionTime.TotalMilliseconds:F2}ms");
    KV("Rows",      result.Documents.Count.ToString("N0"));
    if (!string.IsNullOrEmpty(result.Message)) KV("Message", result.Message);
    Console.WriteLine();

    int shown = 0;
    foreach (var d in result.Documents)
    {
        if (shown++ >= 20) { Console.WriteLine(Gray($"  ... and {result.Documents.Count - 20} more")); break; }
        Console.WriteLine(d.ToJson());
    }
    return 0;
}

static int CmdCompact(string[] args)
{
    if (args.Length < 2) return Fail("Usage: dfctl compact <db-file> <collection>");
    using var db = DocumentForgeDb.Open(args[0]);
    var before = new FileInfo(args[0]).Length;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var r = db.Compact(args[1]);
    sw.Stop();
    db.Flush();
    var after = new FileInfo(args[0]).Length;
    Header("Compaction Result");
    KV("Pages compacted", r.PagesCompacted.ToString("N0"));
    KV("Bytes reclaimed", FormatSize(r.BytesReclaimed));
    KV("File before/after", $"{FormatSize(before)} → {FormatSize(after)}");
    KV("Time",             $"{sw.Elapsed.TotalSeconds:F2}s");
    return 0;
}

static int CmdCluster(string[] args)
{
    if (args.Length < 2) return Fail("Usage: dfctl cluster init|show|add-shard|add-collection [...]");
    var sub = args[0].ToLowerInvariant();
    var configPath = args[1];

    switch (sub)
    {
        case "init":
        {
            if (ClusterConfig.Exists(configPath)) return Fail($"Config already exists at {configPath}");
            var cfg = new ClusterConfig();
            cfg.Save(configPath);
            Console.WriteLine($"{Green("✓")} Created empty cluster config at {configPath}");
            Console.WriteLine($"  Next: dfctl cluster add-shard {configPath} <name> <endpoint>");
            return 0;
        }
        case "show":
        {
            var cfg = ClusterConfig.Load(configPath);
            Header($"Cluster Config v{cfg.Version} ({configPath})");
            KV("Virtual nodes/shard", cfg.VirtualNodesPerShard.ToString());
            Console.WriteLine();
            Header($"Shards ({cfg.Shards.Count})");
            foreach (var s in cfg.Shards)
                Console.WriteLine($"  {Blue(s.Name),-20} {s.Endpoint}");
            Console.WriteLine();
            Header($"Collections ({cfg.Collections.Count})");
            foreach (var (name, pol) in cfg.Collections)
            {
                var strat = pol.Strategy == ShardingStrategy.Replicated ? "replicated" : $"hash on {pol.ShardKeyPath}";
                Console.WriteLine($"  {Blue(name),-20} {strat}");
            }
            return 0;
        }
        case "add-shard":
        {
            if (args.Length < 4) return Fail("Usage: dfctl cluster add-shard <config> <name> <endpoint>");
            var cfg = ClusterConfig.Load(configPath);
            cfg.Shards.Add(new ShardDescriptor { Name = args[2], Endpoint = args[3] });
            cfg.Version++;
            cfg.Save(configPath);
            Console.WriteLine($"{Green("✓")} Added shard '{args[2]}' → {args[3]} (config version {cfg.Version})");
            return 0;
        }
        case "add-collection":
        {
            if (args.Length < 4) return Fail("Usage: dfctl cluster add-collection <config> <name> (hash <path>|replicated)");
            var cfg = ClusterConfig.Load(configPath);
            var collName = args[2];
            if (args[3] == "replicated")
                cfg.Collections[collName] = new CollectionPolicyDescriptor { Strategy = ShardingStrategy.Replicated };
            else if (args[3] == "hash" && args.Length >= 5)
                cfg.Collections[collName] = new CollectionPolicyDescriptor { Strategy = ShardingStrategy.Hash, ShardKeyPath = args[4] };
            else return Fail("Expected 'hash <path>' or 'replicated'");
            cfg.Version++;
            cfg.Save(configPath);
            Console.WriteLine($"{Green("✓")} Configured collection '{collName}' (config version {cfg.Version})");
            return 0;
        }
        default: return Fail($"Unknown cluster subcommand '{sub}'");
    }
}

static int CmdHealth(string[] args)
{
    if (args.Length < 1) return Fail("Usage: dfctl health <cluster-config-file>");
    var cfg = ClusterConfig.Load(args[0]);
    Header($"Cluster Health ({cfg.Shards.Count} shards)");

    foreach (var shard in cfg.Shards)
    {
        try
        {
            using var transport = new HttpShardTransport(shard.Name, shard.Endpoint);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var stats = transport.GetStatistics();
            sw.Stop();
            Console.WriteLine($"  {Green("●")} {Blue(shard.Name),-20} {shard.Endpoint,-40} {FormatSize(stats.FileSize),10}  ({sw.Elapsed.TotalMilliseconds:F0}ms)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  {Red("●")} {Blue(shard.Name),-20} {shard.Endpoint,-40} {Red("UNREACHABLE")}: {ex.Message}");
        }
    }
    return 0;
}

static int CmdRebalance(string[] args)
{
    if (args.Length < 2) return Fail("Usage: dfctl rebalance <old-config> <new-config> [--plan-only]");
    var oldCfg = ClusterConfig.Load(args[0]);
    var newCfg = ClusterConfig.Load(args[1]);
    bool planOnly = args.Any(a => a == "--plan-only");

    IShardTransport Make(ShardDescriptor d) => new HttpShardTransport(d.Name, d.Endpoint);

    Console.WriteLine("Scanning current topology...");
    var plan = ClusterRebalancer.Plan(oldCfg, newCfg, Make);

    Header("Migration Plan");
    KV("Total docs to move", plan.TotalDocumentsToMove.ToString("N0"));
    Console.WriteLine();
    foreach (var coll in plan.Collections)
    {
        Console.WriteLine($"  {Blue(coll.CollectionName)}  ({coll.EstimatedMovedDocs:N0} moves)");
        foreach (var ((from, to), count) in coll.Moves.OrderBy(k => k.Key.From))
            Console.WriteLine($"    {from} → {to}: {count:N0}");
    }

    if (planOnly)
    {
        Console.WriteLine();
        Console.WriteLine(Gray("Plan-only run. Pass without --plan-only to execute."));
        return 0;
    }

    Console.WriteLine();
    Console.Write("Execute this migration? (type 'yes' to proceed): ");
    if (Console.ReadLine()?.Trim() != "yes") { Console.WriteLine("Aborted."); return 0; }

    var sw = System.Diagnostics.Stopwatch.StartNew();
    ClusterRebalancer.Execute(plan, Make, progress =>
        Console.WriteLine($"  {progress.CollectionName,-20} {progress.FromShard,-10} {Gray("moved")} {progress.DocsMoved,8:N0} / scanned {progress.DocsScanned,8:N0}"));
    sw.Stop();

    Console.WriteLine();
    Console.WriteLine($"{Green("✓")} Migration complete in {sw.Elapsed.TotalSeconds:F1}s");
    Console.WriteLine($"  Save the new config to all nodes: cp {args[1]} ...");
    return 0;
}

static int PrintHelp()
{
    Header("dfctl - DocumentForge management CLI");
    Console.WriteLine("""
  COMMANDS

    inspect   <db-file>
        Show database stats, collections and indexes.

    query     <db-file> "<sql>"
        Execute a SQL query and print results.

    compact   <db-file> <collection>
        Reclaim space from deleted documents.

    cluster init          <config-file>
    cluster show          <config-file>
    cluster add-shard     <config-file> <name> <endpoint>
    cluster add-collection <config-file> <name> (hash <path>|replicated)
        Manage a cluster config file.

    health    <config-file>
        Ping every shard over HTTP and report size + latency.

    rebalance <old-config> <new-config> [--plan-only]
        Show what would move under a new topology, then migrate the data.
        Use --plan-only to preview without changing anything.

  EXAMPLES

    dfctl inspect airline.dfdb
    dfctl query airline.dfdb "SELECT * FROM orders WHERE pnr = 'ABC123'"
    dfctl cluster init cluster.json
    dfctl cluster add-shard cluster.json dubai dubai.internal:4300
    dfctl cluster add-collection cluster.json orders hash pnr
    dfctl cluster add-collection cluster.json airports replicated
    dfctl health cluster.json
""");
    return 0;
}

static int Fail(string msg) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("ERROR: " + msg); Console.ResetColor(); return 1; }
static void Header(string title) { Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine("═ " + title); Console.ResetColor(); }
static void KV(string k, string v) { Console.ForegroundColor = ConsoleColor.DarkGray; Console.Write($"  {k,-18} "); Console.ResetColor(); Console.WriteLine(v); }

static string Blue(string s)  { return $"\u001b[36m{s}\u001b[0m"; }
static string Red(string s)   { return $"\u001b[31m{s}\u001b[0m"; }
static string Green(string s) { return $"\u001b[32m{s}\u001b[0m"; }
static string Gray(string s)  { return $"\u001b[90m{s}\u001b[0m"; }

static string FormatSize(long bytes) => bytes switch
{
    < 1024 => $"{bytes} B",
    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
    < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
    _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB"
};
