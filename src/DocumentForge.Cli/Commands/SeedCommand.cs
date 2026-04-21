using DocumentForge.Engine;

namespace DocumentForge.Cli.Commands;

/// <summary>
/// `dfdb seed <file> [count]` — load sample airline order + flight data.
/// Creates the file if it doesn't exist. Idempotent-ish: duplicate PNR collisions
/// are accepted if the counter state differs, but the seed counter is timestamp-based
/// so running this twice in a row usually adds fresh rows without error.
/// </summary>
public static class SeedCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 1) return UiHelpers.Fail("Usage: dfdb seed <db-file> [count]");
        var path = args[0];
        int count = args.Length >= 2 && int.TryParse(args[1], out var n) ? n : 500;

        using var db = DocumentForgeDb.OpenOrCreate(path);
        var rng = new Random(42);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        Console.Write($"Seeding {count:N0} orders + 100 flights into {path}... ");

        for (int i = 0; i < count; i++)
            db.Insert("orders", DocumentForge.AirlineDemo.SeedData.GenerateOrder(rng, i));

        string[] airlines = { "AA", "UA", "DL", "BA", "LH" };
        for (int i = 0; i < 100; i++)
        {
            var al = airlines[rng.Next(airlines.Length)];
            var fn = $"{al}{rng.Next(100, 9999)}";
            var date = DateTime.Today.AddDays(rng.Next(0, 30)).ToString("yyyy-MM-dd");
            db.Insert("flights", DocumentForge.AirlineDemo.SeedData.GenerateFlight(rng, fn, date));
        }

        try { db.CreateIndex("orders", "pnr", "idx_orders_pnr", unique: true); } catch { }
        try { db.CreateIndex("orders", "passenger.lastName", "idx_orders_lastname"); } catch { }
        try { db.CreateIndex("orders", "status", "idx_orders_status"); } catch { }
        try { db.CreateIndex("flights", "flightNumber", "idx_flights_number"); } catch { }
        try { db.CreateIndex("flights", "departureAirport", "idx_flights_departure"); } catch { }

        db.Flush();
        sw.Stop();

        Console.WriteLine($"done in {sw.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine();
        Console.WriteLine($"  try: dfdb query {path} \"SELECT * FROM orders LIMIT 5\"");
        Console.WriteLine($"  or:  dfdb repl  {path}");
        return 0;
    }
}
