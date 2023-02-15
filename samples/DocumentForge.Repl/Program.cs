using DocumentForge.Engine;
using DocumentForge.Document;
using DocumentForge.Index;

Console.WriteLine("╔═══════════════════════════════════════════════════════════╗");
Console.WriteLine("║         DocumentForge - Interactive SQL Console           ║");
Console.WriteLine("║         Type SQL queries or commands below                ║");
Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");
Console.WriteLine();

var dbPath = Path.Combine(Directory.GetCurrentDirectory(), "interactive.dfdb");

// Check if DB exists - offer to seed if not
bool isNew = !File.Exists(dbPath);
using var db = DocumentForgeDb.OpenOrCreate(dbPath);

if (isNew)
{
    Console.WriteLine("  New database created: " + dbPath);
    Console.WriteLine();
    Console.Write("  Seed with sample airline data? (y/n): ");
    var answer = Console.ReadLine()?.Trim().ToLower();
    if (answer == "y" || answer == "yes")
    {
        SeedSampleData(db);
    }
}
else
{
    Console.WriteLine("  Opened existing database: " + dbPath);
}

PrintHelp();

while (true)
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Green;
    Console.Write("  dfdb> ");
    Console.ResetColor();

    var input = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(input)) continue;

    // Commands
    if (input.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("exit", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("  Goodbye!");
        break;
    }

    if (input.Equals("help", StringComparison.OrdinalIgnoreCase))
    {
        PrintHelp();
        continue;
    }

    if (input.Equals("collections", StringComparison.OrdinalIgnoreCase))
    {
        var names = db.GetCollectionNames();
        if (names.Count == 0)
        {
            Console.WriteLine("  (no collections)");
        }
        else
        {
            foreach (var name in names)
                Console.WriteLine($"  - {name}");
        }
        continue;
    }

    if (input.Equals("stats", StringComparison.OrdinalIgnoreCase))
    {
        PrintStats(db);
        continue;
    }

    if (input.StartsWith("indexes ", StringComparison.OrdinalIgnoreCase))
    {
        var collName = input.Substring(8).Trim();
        var indexes = db.GetIndexes(collName);
        if (indexes.Count == 0)
        {
            Console.WriteLine($"  No indexes on '{collName}'");
        }
        else
        {
            foreach (var idx in indexes)
            {
                Console.WriteLine($"  - {idx.Name} on ({idx.JsonPath}){(idx.IsUnique ? " UNIQUE" : "")}");
            }
        }
        continue;
    }

    if (input.Equals("seed", StringComparison.OrdinalIgnoreCase))
    {
        SeedSampleData(db);
        continue;
    }

    if (input.Equals("clear", StringComparison.OrdinalIgnoreCase))
    {
        Console.Clear();
        continue;
    }

    // Execute as SQL query
    try
    {
        var result = db.Execute(input);

        if (!result.Success)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  ERROR: {result.Message}");
            Console.ResetColor();
            continue;
        }

        // Display results
        if (result.Documents.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"  Plan: {result.QueryPlan} | {result.Documents.Count} result(s) | {result.ExecutionTime.TotalMilliseconds:F2}ms");
            Console.ResetColor();
            Console.WriteLine("  ───────────────────────────────────────────────────");

            int shown = 0;
            foreach (var doc in result.Documents)
            {
                if (shown >= 20)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"  ... and {result.Documents.Count - 20} more (use LIMIT to control output)");
                    Console.ResetColor();
                    break;
                }

                var json = doc.ToJson();
                // Pretty print with indentation
                try
                {
                    var parsed = System.Text.Json.JsonDocument.Parse(json);
                    var pretty = System.Text.Json.JsonSerializer.Serialize(parsed, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    foreach (var line in pretty.Split('\n'))
                        Console.WriteLine($"  {line}");
                }
                catch
                {
                    Console.WriteLine($"  {json}");
                }

                if (result.Documents.Count > 1 && shown < Math.Min(result.Documents.Count - 1, 19))
                    Console.WriteLine("  ---");
                shown++;
            }
        }
        else if (result.Message is not null)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"  {result.Message} | {result.ExecutionTime.TotalMilliseconds:F2}ms");
            Console.ResetColor();
        }
        else
        {
            Console.WriteLine($"  OK | {result.ExecutionTime.TotalMilliseconds:F2}ms");
        }
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  ERROR: {ex.Message}");
        Console.ResetColor();
    }
}

db.Flush();

// ---- Helper methods ----

static void PrintHelp()
{
    Console.WriteLine();
    Console.WriteLine("  COMMANDS:");
    Console.WriteLine("    help          - Show this help");
    Console.WriteLine("    collections   - List all collections");
    Console.WriteLine("    indexes <col> - List indexes on a collection");
    Console.WriteLine("    stats         - Show database statistics");
    Console.WriteLine("    seed          - Load sample airline data");
    Console.WriteLine("    clear         - Clear screen");
    Console.WriteLine("    quit          - Exit");
    Console.WriteLine();
    Console.WriteLine("  SQL QUERIES:");
    Console.WriteLine("    SELECT * FROM orders WHERE pnr = 'ABC123'");
    Console.WriteLine("    SELECT * FROM orders WHERE passenger.lastName = 'Smith'");
    Console.WriteLine("    SELECT pnr, status FROM orders LIMIT 10");
    Console.WriteLine("    SELECT COUNT(*) FROM orders WHERE status = 'CONFIRMED'");
    Console.WriteLine("    INSERT INTO orders VALUES {\"pnr\": \"TEST01\", \"status\": \"NEW\"}");
    Console.WriteLine("    UPDATE orders SET status = 'CANCELLED' WHERE pnr = 'TEST01'");
    Console.WriteLine("    DELETE FROM orders WHERE pnr = 'TEST01'");
    Console.WriteLine("    CREATE INDEX idx_pnr ON orders (pnr)");
    Console.WriteLine("    CREATE UNIQUE INDEX idx_pnr ON orders (pnr)");
}

static void PrintStats(DocumentForgeDb db)
{
    var stats = db.GetStatistics();
    Console.WriteLine($"  File:    {stats.FilePath}");
    Console.WriteLine($"  Size:    {stats.FileSize / 1024.0 / 1024.0:F2} MB");
    Console.WriteLine($"  Pages:   {stats.PageCount:N0}");
    Console.WriteLine($"  Cache:   {stats.CachedPages} pages ({stats.DirtyPages} dirty)");
    foreach (var coll in stats.Collections)
    {
        Console.WriteLine($"  [{coll.Name}] {coll.DocumentCount:N0} docs, {coll.IndexCount} indexes");
        foreach (var idx in coll.Indexes)
            Console.WriteLine($"    - {idx.Name} ({idx.JsonPath}) [{idx.EntryCount:N0} entries{(idx.IsUnique ? ", UNIQUE" : "")}]");
    }
}

static void SeedSampleData(DocumentForgeDb db)
{
    Console.Write("  How many orders to seed? (default 500): ");
    var countStr = Console.ReadLine()?.Trim();
    int count = 500;
    if (!string.IsNullOrEmpty(countStr) && int.TryParse(countStr, out var parsed))
        count = parsed;

    Console.Write($"  Seeding {count} orders...");
    var rng = new Random(42);
    var sw = System.Diagnostics.Stopwatch.StartNew();

    for (int i = 0; i < count; i++)
    {
        var json = DocumentForge.AirlineDemo.SeedData.GenerateOrder(rng, i);
        db.Insert("orders", json);
    }

    // Seed some flights too
    for (int i = 0; i < 100; i++)
    {
        string[] airlines = { "AA", "UA", "DL", "BA", "LH" };
        var airline = airlines[rng.Next(airlines.Length)];
        var flightNum = $"{airline}{rng.Next(100, 9999)}";
        var date = DateTime.Today.AddDays(rng.Next(0, 30)).ToString("yyyy-MM-dd");
        var json = DocumentForge.AirlineDemo.SeedData.GenerateFlight(rng, flightNum, date);
        db.Insert("flights", json);
    }

    sw.Stop();
    Console.WriteLine($" done! ({sw.Elapsed.TotalSeconds:F1}s)");

    // Create indexes
    Console.Write("  Creating indexes...");
    try { db.CreateIndex("orders", "pnr", "idx_orders_pnr", unique: true); } catch { }
    try { db.CreateIndex("orders", "passenger.lastName", "idx_orders_lastname"); } catch { }
    try { db.CreateIndex("orders", "status", "idx_orders_status"); } catch { }
    try { db.CreateIndex("flights", "flightNumber", "idx_flights_number"); } catch { }
    try { db.CreateIndex("flights", "departureAirport", "idx_flights_departure"); } catch { }
    Console.WriteLine(" done!");

    Console.WriteLine($"  Seeded {count} orders + 100 flights with 5 indexes.");
}
