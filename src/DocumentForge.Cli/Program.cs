using DocumentForge.Cli.Commands;

namespace DocumentForge.Cli;

public class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintRootHelp();
            return 0;
        }

        var sub = args[0].ToLowerInvariant();
        var rest = args.Skip(1).ToArray();

        try
        {
            return sub switch
            {
                "serve"     => ServeCommand.Run(rest),
                "repl"      => ReplCommand.Run(rest),
                "inspect"   => InspectCommand.Run(rest),
                "query"     => QueryCommand.Run(rest),
                "compact"   => CompactCommand.Run(rest),
                "cluster"   => ClusterCommand.Run(rest),
                "health"    => HealthCommand.Run(rest),
                "rebalance" => RebalanceCommand.Run(rest),
                "seed"      => SeedCommand.Run(rest),
                "version" or "--version" or "-v" => PrintVersion(),
                "help" or "--help" or "-h" => PrintRootHelp(),
                _ => FailUnknown(sub)
            };
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }

    private static int FailUnknown(string cmd)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"Unknown command: '{cmd}'. Try 'dfdb help'.");
        Console.ResetColor();
        return 1;
    }

    private static int PrintVersion()
    {
        Console.WriteLine("dfdb 0.1.0");
        Console.WriteLine("DocumentForge - embedded JSON document database with replication and sharding");
        return 0;
    }

    private static int PrintRootHelp()
    {
        Console.WriteLine();
        Console.WriteLine("  \x1b[36mdfdb\x1b[0m  -  DocumentForge, one binary");
        Console.WriteLine();
        Console.WriteLine("  USAGE");
        Console.WriteLine("    dfdb <command> [args]");
        Console.WriteLine();
        Console.WriteLine("  RUN A NODE");
        Console.WriteLine("    serve [--config node.json | --port N --data-dir DIR | --api-key KEY]");
        Console.WriteLine("        Start the REST API for one node. Pairs with the admin UI.");
        Console.WriteLine();
        Console.WriteLine("  QUERY LOCAL DATABASE");
        Console.WriteLine("    inspect <file>                       Show stats, collections, indexes");
        Console.WriteLine("    query   <file> \"<sql>\"               One-shot SQL");
        Console.WriteLine("    repl    <file>                       Interactive SQL console");
        Console.WriteLine("    compact <file> <collection>          Reclaim space from deletes");
        Console.WriteLine("    seed    <file> [count]               Load sample airline data");
        Console.WriteLine();
        Console.WriteLine("  MANAGE A CLUSTER");
        Console.WriteLine("    cluster init          <config>");
        Console.WriteLine("    cluster show          <config>");
        Console.WriteLine("    cluster add-shard     <config> <name> <endpoint>");
        Console.WriteLine("    cluster add-collection <config> <name> (hash <path>|replicated)");
        Console.WriteLine("    health    <config>                   Ping every shard");
        Console.WriteLine("    rebalance <old-config> <new-config> [--plan-only]");
        Console.WriteLine();
        Console.WriteLine("  EXAMPLES");
        Console.WriteLine("    dfdb serve --port 5001 --data-dir ./data/shard-a");
        Console.WriteLine("    dfdb inspect ./data/shard-a/data.dfdb");
        Console.WriteLine("    dfdb query   ./data/shard-a/data.dfdb \"SELECT * FROM orders LIMIT 5\"");
        Console.WriteLine("    dfdb seed    ./data/shard-a/data.dfdb 10000");
        Console.WriteLine("    dfdb cluster init cluster.json");
        Console.WriteLine("    dfdb health  cluster.json");
        Console.WriteLine();
        return 0;
    }
}
