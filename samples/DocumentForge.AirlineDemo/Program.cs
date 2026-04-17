using System.Diagnostics;
using DocumentForge.Engine;

namespace DocumentForge.AirlineDemo;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("=============================================================");
        Console.WriteLine("   DocumentForge - Airline Reservation System Demo");
        Console.WriteLine("   Inspired by Sabre & Amadeus mainframe systems");
        Console.WriteLine("=============================================================");
        Console.WriteLine();

        var dbPath = Path.Combine(Path.GetTempPath(), "airline_demo.dfdb");

        // Clean up previous run
        if (File.Exists(dbPath)) File.Delete(dbPath);
        if (File.Exists(dbPath + ".wal")) File.Delete(dbPath + ".wal");

        using var db = DocumentForgeDb.Create(dbPath);
        var rng = new Random(42); // deterministic seed for reproducibility

        // --- SEED DATA ---
        Console.WriteLine("[1/6] Seeding order data...");
        var seedSw = Stopwatch.StartNew();
        int orderCount = 10_000;
        var samplePnrs = new List<string>();

        for (int i = 0; i < orderCount; i++)
        {
            var json = SeedData.GenerateOrder(rng, i);
            db.Insert("orders", json);

            // Save some PNRs for later queries
            if (i % 1000 == 0)
            {
                var doc = Document.BsonDocument.FromJson(json);
                samplePnrs.Add(doc["pnr"].AsString);
            }

            if ((i + 1) % 2500 == 0)
                Console.WriteLine($"   ... {i + 1:N0} orders inserted");
        }
        seedSw.Stop();
        Console.WriteLine($"   Inserted {orderCount:N0} orders in {seedSw.Elapsed.TotalSeconds:F2}s " +
                          $"({orderCount / seedSw.Elapsed.TotalSeconds:F0} docs/sec)");

        // Seed flights
        Console.WriteLine();
        Console.WriteLine("[2/6] Seeding flight schedule data...");
        seedSw.Restart();
        int flightCount = 1_000;
        string[] airlines = { "AA", "UA", "DL", "BA", "LH" };
        for (int i = 0; i < flightCount; i++)
        {
            var airline = airlines[rng.Next(airlines.Length)];
            var flightNum = $"{airline}{rng.Next(100, 9999)}";
            var date = DateTime.Today.AddDays(rng.Next(0, 30)).ToString("yyyy-MM-dd");
            var json = SeedData.GenerateFlight(rng, flightNum, date);
            db.Insert("flights", json);
        }
        seedSw.Stop();
        Console.WriteLine($"   Inserted {flightCount:N0} flights in {seedSw.Elapsed.TotalSeconds:F2}s");

        // --- CREATE INDEXES ---
        Console.WriteLine();
        Console.WriteLine("[3/6] Creating indexes...");
        var idxSw = Stopwatch.StartNew();

        db.CreateIndex("orders", "pnr", "idx_orders_pnr", unique: true);
        Console.WriteLine("   Created: idx_orders_pnr (unique)");

        db.CreateIndex("orders", "passenger.lastName", "idx_orders_lastname");
        Console.WriteLine("   Created: idx_orders_lastname");

        db.CreateIndex("orders", "status", "idx_orders_status");
        Console.WriteLine("   Created: idx_orders_status");

        db.CreateIndex("flights", "flightNumber", "idx_flights_number");
        Console.WriteLine("   Created: idx_flights_number");

        db.CreateIndex("flights", "departureAirport", "idx_flights_departure");
        Console.WriteLine("   Created: idx_flights_departure");

        idxSw.Stop();
        Console.WriteLine($"   All indexes created in {idxSw.Elapsed.TotalSeconds:F2}s");

        // --- QUERIES ---
        Console.WriteLine();
        Console.WriteLine("[4/6] Running queries...");
        Console.WriteLine("-------------------------------------------------------------");

        // Query 1: PNR lookup (indexed)
        var testPnr = samplePnrs[0];
        Console.WriteLine($"\n   Query: SELECT * FROM orders WHERE pnr = '{testPnr}'");
        var result = db.Execute($"SELECT * FROM orders WHERE pnr = '{testPnr}'");
        Console.WriteLine($"   Plan:    {result.QueryPlan}");
        Console.WriteLine($"   Results: {result.Documents.Count}");
        Console.WriteLine($"   Time:    {result.ExecutionTime.TotalMilliseconds:F2}ms");
        if (result.Documents.Count > 0)
        {
            var doc = result.Documents[0];
            Console.WriteLine($"   PNR:     {doc["pnr"]}");
            Console.WriteLine($"   Passenger: {Index.JsonPathExtractor.Extract(doc, "passenger.firstName")} " +
                              $"{Index.JsonPathExtractor.Extract(doc, "passenger.lastName")}");
            Console.WriteLine($"   Status:  {doc["status"]}");
        }

        // Query 2: Passenger search (indexed)
        Console.WriteLine($"\n   Query: SELECT * FROM orders WHERE passenger.lastName = 'Smith'");
        result = db.Execute("SELECT * FROM orders WHERE passenger.lastName = 'Smith'");
        Console.WriteLine($"   Plan:    {result.QueryPlan}");
        Console.WriteLine($"   Results: {result.Documents.Count}");
        Console.WriteLine($"   Time:    {result.ExecutionTime.TotalMilliseconds:F2}ms");

        // Query 3: Status search (indexed)
        Console.WriteLine($"\n   Query: SELECT * FROM orders WHERE status = 'CANCELLED'");
        result = db.Execute("SELECT * FROM orders WHERE status = 'CANCELLED'");
        Console.WriteLine($"   Plan:    {result.QueryPlan}");
        Console.WriteLine($"   Results: {result.Documents.Count}");
        Console.WriteLine($"   Time:    {result.ExecutionTime.TotalMilliseconds:F2}ms");

        // Query 4: Collection scan (no index on this path)
        Console.WriteLine($"\n   Query: SELECT pnr, passenger.lastName, status FROM orders WHERE passenger.firstName = 'James' LIMIT 5");
        result = db.Execute("SELECT pnr, passenger.lastName, status FROM orders WHERE passenger.firstName = 'James' LIMIT 5");
        Console.WriteLine($"   Plan:    {result.QueryPlan}");
        Console.WriteLine($"   Results: {result.Documents.Count}");
        Console.WriteLine($"   Time:    {result.ExecutionTime.TotalMilliseconds:F2}ms");
        foreach (var doc in result.Documents)
        {
            Console.WriteLine($"     - {doc["pnr"]} | {doc["passenger.lastName"]} | {doc["status"]}");
        }

        // Query 5: COUNT
        Console.WriteLine($"\n   Query: SELECT COUNT(*) FROM orders WHERE status = 'CONFIRMED'");
        result = db.Execute("SELECT COUNT(*) FROM orders WHERE status = 'CONFIRMED'");
        Console.WriteLine($"   Count:   {result.Documents[0]["count"]}");
        Console.WriteLine($"   Time:    {result.ExecutionTime.TotalMilliseconds:F2}ms");

        // Query 6: Flight lookup
        Console.WriteLine($"\n   Query: SELECT * FROM flights WHERE departureAirport = 'JFK' LIMIT 3");
        result = db.Execute("SELECT * FROM flights WHERE departureAirport = 'JFK' LIMIT 3");
        Console.WriteLine($"   Plan:    {result.QueryPlan}");
        Console.WriteLine($"   Results: {result.Documents.Count}");
        Console.WriteLine($"   Time:    {result.ExecutionTime.TotalMilliseconds:F2}ms");

        // --- UPDATE ---
        Console.WriteLine();
        Console.WriteLine("[5/6] Testing updates...");
        Console.WriteLine($"\n   Query: UPDATE orders SET status = 'CANCELLED' WHERE pnr = '{testPnr}'");
        result = db.Execute($"UPDATE orders SET status = 'CANCELLED' WHERE pnr = '{testPnr}'");
        Console.WriteLine($"   Updated: {result.AffectedCount} document(s)");
        Console.WriteLine($"   Time:    {result.ExecutionTime.TotalMilliseconds:F2}ms");

        // Verify update
        result = db.Execute($"SELECT status FROM orders WHERE pnr = '{testPnr}'");
        if (result.Documents.Count > 0)
            Console.WriteLine($"   Verify:  status = {result.Documents[0]["status"]}");

        // --- STATISTICS ---
        Console.WriteLine();
        Console.WriteLine("[6/6] Database statistics");
        Console.WriteLine("-------------------------------------------------------------");

        db.Flush();
        var stats = db.GetStatistics();

        Console.WriteLine($"   File:       {stats.FilePath}");
        Console.WriteLine($"   File Size:  {stats.FileSize / 1024.0 / 1024.0:F2} MB");
        Console.WriteLine($"   Pages:      {stats.PageCount:N0} ({stats.PageCount * 8:N0} KB)");
        Console.WriteLine($"   Cache:      {stats.CachedPages:N0} pages cached, {stats.DirtyPages} dirty");
        Console.WriteLine();

        foreach (var coll in stats.Collections)
        {
            Console.WriteLine($"   Collection: {coll.Name}");
            Console.WriteLine($"     Documents: {coll.DocumentCount:N0}");
            Console.WriteLine($"     Indexes:   {coll.IndexCount}");
            foreach (var idx in coll.Indexes)
            {
                Console.WriteLine($"       - {idx.Name} on ({idx.JsonPath}) " +
                                  $"[{idx.EntryCount:N0} entries{(idx.IsUnique ? ", UNIQUE" : "")}]");
            }
        }

        Console.WriteLine();
        Console.WriteLine("=============================================================");
        Console.WriteLine("   Demo complete! DocumentForge is ready for takeoff.");
        Console.WriteLine("=============================================================");
    }
}
