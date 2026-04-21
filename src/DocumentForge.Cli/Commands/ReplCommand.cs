using DocumentForge.Engine;

namespace DocumentForge.Cli.Commands;

/// <summary>
/// `dfdb repl <file>` — interactive SQL console against a single DB file.
/// </summary>
public static class ReplCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 1) return UiHelpers.Fail("Usage: dfdb repl <db-file>");
        var path = args[0];
        bool existed = File.Exists(path);

        using var db = DocumentForgeDb.OpenOrCreate(path);

        Console.WriteLine();
        Console.WriteLine("  \x1b[36mdfdb repl\x1b[0m  -  " + (existed ? "opened " : "created ") + path);
        Console.WriteLine("  type SQL to execute. commands: help, stats, collections, indexes <name>, quit");
        Console.WriteLine();

        while (true)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("  dfdb> ");
            Console.ResetColor();

            var input = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(input)) continue;

            if (input.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
                input.Equals("exit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (input.Equals("help", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("  SQL queries run directly. Examples:");
                Console.WriteLine("    SELECT * FROM orders WHERE pnr = 'ABC123'");
                Console.WriteLine("    SELECT status, COUNT(*) FROM orders GROUP BY status");
                Console.WriteLine("    CREATE INDEX idx_pnr ON orders (pnr)");
                Console.WriteLine("  Meta-commands: stats, collections, indexes <name>, clear, quit");
                continue;
            }

            if (input.Equals("stats", StringComparison.OrdinalIgnoreCase)) { PrintStats(db); continue; }
            if (input.Equals("collections", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var n in db.GetCollectionNames()) Console.WriteLine("  " + n);
                if (db.GetCollectionNames().Count == 0) Console.WriteLine("  (none)");
                continue;
            }
            if (input.StartsWith("indexes ", StringComparison.OrdinalIgnoreCase))
            {
                var collName = input.Substring(8).Trim();
                foreach (var i in db.GetIndexes(collName))
                    Console.WriteLine($"  {i.Name}  on ({i.JsonPath}){(i.IsUnique ? "  UNIQUE" : "")}");
                continue;
            }
            if (input.Equals("clear", StringComparison.OrdinalIgnoreCase)) { Console.Clear(); continue; }

            try
            {
                var r = db.Execute(input);
                if (!r.Success)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("  ERROR: " + r.Message);
                    Console.ResetColor();
                    continue;
                }
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"  Plan: {r.QueryPlan}  |  {r.Documents.Count} row(s)  |  {r.ExecutionTime.TotalMilliseconds:F2}ms");
                Console.ResetColor();
                int shown = 0;
                foreach (var d in r.Documents)
                {
                    if (shown++ >= 10) { Console.WriteLine($"  ... and {r.Documents.Count - 10} more (use LIMIT)"); break; }
                    Console.WriteLine("  " + d.ToJson());
                }
                if (!string.IsNullOrEmpty(r.Message)) Console.WriteLine("  " + r.Message);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  ERROR: " + ex.Message);
                Console.ResetColor();
            }
        }
        db.Flush();
        return 0;
    }

    private static void PrintStats(DocumentForgeDb db)
    {
        db.Flush();
        var s = db.GetStatistics();
        Console.WriteLine($"  file:   {s.FilePath}");
        Console.WriteLine($"  size:   {UiHelpers.FormatSize(s.FileSize)}");
        Console.WriteLine($"  pages:  {s.PageCount:N0}  ({s.CachedPages:N0} cached, {s.DirtyPages} dirty)");
        foreach (var c in s.Collections)
        {
            Console.WriteLine($"  [{c.Name}]  {c.DocumentCount:N0} docs, {c.IndexCount} indexes");
            foreach (var i in c.Indexes)
                Console.WriteLine($"    - {i.Name} on ({i.JsonPath}) [{i.EntryCount:N0} entries{(i.IsUnique ? ", UNIQUE" : "")}]");
        }
    }
}
