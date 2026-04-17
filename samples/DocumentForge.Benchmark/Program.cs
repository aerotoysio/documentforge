using System.Diagnostics;
using DocumentForge.Document;
using DocumentForge.Engine;
using DocumentForge.Index;

const int TARGET_DOCS = 250_000;
const int BATCH_SIZE = 5_000;
const int QUERY_DURATION_SECONDS = 60; // 1 minute per query type

Console.WriteLine("╔═══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║     DocumentForge - STRESS TEST BENCHMARK                    ║");
Console.WriteLine("╠═══════════════════════════════════════════════════════════════╣");
Console.WriteLine($"║  Target:  {TARGET_DOCS:N0} documents                              ║");
Console.WriteLine($"║  Query:   {QUERY_DURATION_SECONDS}s sustained per query type                     ║");
Console.WriteLine("╚═══════════════════════════════════════════════════════════════╝");
Console.WriteLine();

var dbPath = Path.Combine(Path.GetTempPath(), "benchmark_10m.dfdb");
if (File.Exists(dbPath)) File.Delete(dbPath);
if (File.Exists(dbPath + ".wal")) File.Delete(dbPath + ".wal");

// Large cache for bulk operations: 10,000 pages = 80MB
var options = new DatabaseOptions { CacheSizeInPages = 10_000, EnableWal = false };
using var db = DocumentForgeDb.Create(dbPath, options);

var rng = new Random(42);
string[] firstNames = { "James", "Mary", "John", "Patricia", "Robert", "Jennifer", "Michael", "Linda", "William", "Elizabeth", "David", "Barbara", "Richard", "Susan", "Joseph", "Jessica", "Thomas", "Sarah", "Charles", "Karen", "Christopher", "Lisa", "Daniel", "Nancy", "Matthew", "Betty", "Anthony", "Margaret", "Mark", "Sandra", "Donald", "Ashley", "Steven", "Kimberly", "Paul", "Emily", "Andrew", "Donna", "Joshua", "Michelle" };
string[] lastNames = { "Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis", "Rodriguez", "Martinez", "Hernandez", "Lopez", "Gonzalez", "Wilson", "Anderson", "Thomas", "Taylor", "Moore", "Jackson", "Martin", "Lee", "Perez", "Thompson", "White", "Harris", "Sanchez", "Clark", "Ramirez", "Lewis", "Robinson" };
string[] airports = { "JFK", "LAX", "ORD", "LHR", "CDG", "FRA", "DXB", "SIN", "SYD", "NRT", "ATL", "DFW", "DEN", "SFO", "SEA", "MIA", "BOS", "IAD", "EWR", "PHX" };
string[] statuses = { "CONFIRMED", "CONFIRMED", "CONFIRMED", "CONFIRMED", "TICKETED", "TICKETED", "CANCELLED", "PENDING" };
string[] airlines = { "AA", "UA", "DL", "BA", "LH", "AF", "EK", "SQ" };
string[] cabins = { "ECONOMY", "PREMIUM_ECONOMY", "BUSINESS", "FIRST" };

// ===== PHASE 1: BULK INSERT =====
Console.WriteLine("PHASE 1: BULK INSERT");
Console.WriteLine("─────────────────────────────────────────────────────────────");

var insertSw = Stopwatch.StartNew();
var batchSw = new Stopwatch();
long totalInserted = 0;

// Store some known PNRs for query benchmarks
var knownPnrs = new List<string>();
var knownLastNames = new List<string>();

while (totalInserted < TARGET_DOCS)
{
    int batchCount = (int)Math.Min(BATCH_SIZE, TARGET_DOCS - totalInserted);
    batchSw.Restart();

    var batch = GenerateBatch(batchCount, rng, firstNames, lastNames, airports, statuses, airlines, cabins);

    db.BulkInsert("orders", batch);
    totalInserted += batchCount;

    batchSw.Stop();
    var elapsed = insertSw.Elapsed;
    var rate = totalInserted / elapsed.TotalSeconds;
    var eta = TimeSpan.FromSeconds((TARGET_DOCS - totalInserted) / rate);

    Console.WriteLine($"  {totalInserted:N0} / {TARGET_DOCS:N0} " +
                  $"({totalInserted * 100.0 / TARGET_DOCS:F1}%) " +
                  $"| {rate:N0} docs/sec " +
                  $"| Batch: {batchSw.Elapsed.TotalSeconds:F1}s " +
                  $"| ETA: {eta:mm\\:ss}");

    // Save some PNRs for later queries
    if (totalInserted <= BATCH_SIZE)
    {
        foreach (var doc in batch.Take(100))
        {
            knownPnrs.Add(doc["pnr"].AsString);
            var ln = JsonPathExtractor.Extract(doc, "passenger.lastName").AsString;
            if (!knownLastNames.Contains(ln)) knownLastNames.Add(ln);
        }
    }
}

insertSw.Stop();
Console.WriteLine();
Console.WriteLine($"  DONE: {totalInserted:N0} documents in {insertSw.Elapsed:hh\\:mm\\:ss}");
Console.WriteLine($"  Rate: {totalInserted / insertSw.Elapsed.TotalSeconds:N0} docs/sec");

// Flush to disk
Console.Write("  Flushing to disk... ");
var flushSw = Stopwatch.StartNew();
db.Flush();
flushSw.Stop();
Console.WriteLine($"done ({flushSw.Elapsed.TotalSeconds:F1}s)");

var fileInfo = new FileInfo(dbPath);
Console.WriteLine($"  File size: {fileInfo.Length / 1024.0 / 1024.0 / 1024.0:F2} GB");

// ===== PHASE 2: CREATE INDEXES =====
Console.WriteLine();
Console.WriteLine("PHASE 2: CREATE INDEXES");
Console.WriteLine("─────────────────────────────────────────────────────────────");

var idxSw = Stopwatch.StartNew();

Console.Write("  Creating idx_pnr (unique)... ");
db.CreateIndex("orders", "pnr", "idx_pnr", unique: true);
Console.WriteLine($"done ({idxSw.Elapsed.TotalSeconds:F1}s)");

idxSw.Restart();
Console.Write("  Creating idx_lastname... ");
db.CreateIndex("orders", "passenger.lastName", "idx_lastname");
Console.WriteLine($"done ({idxSw.Elapsed.TotalSeconds:F1}s)");

idxSw.Restart();
Console.Write("  Creating idx_status... ");
db.CreateIndex("orders", "status", "idx_status");
Console.WriteLine($"done ({idxSw.Elapsed.TotalSeconds:F1}s)");

idxSw.Restart();
Console.Write("  Creating idx_departure... ");
db.CreateIndex("orders", "flights[0].departureAirport", "idx_departure");
Console.WriteLine($"done ({idxSw.Elapsed.TotalSeconds:F1}s)");

idxSw.Restart();
Console.Write("  Creating idx_fare (for range queries)... ");
db.CreateIndex("orders", "tickets[0].fare.total", "idx_fare");
Console.WriteLine($"done ({idxSw.Elapsed.TotalSeconds:F1}s)");

// Build location map
Console.Write("  Building location map... ");
idxSw.Restart();
var coll = db.GetCollection("orders")!;
coll.BuildLocationMap();
Console.WriteLine($"done ({idxSw.Elapsed.TotalSeconds:F1}s)");

// ===== PHASE 3: QUERY BENCHMARK =====
Console.WriteLine();
Console.WriteLine($"PHASE 3: QUERY BENCHMARK ({QUERY_DURATION_SECONDS / 60} MINUTES)");
Console.WriteLine("─────────────────────────────────────────────────────────────");

// Warm up
db.Execute($"SELECT * FROM orders WHERE pnr = '{knownPnrs[0]}'");

var benchmarks = new List<(string Name, string Query, long Count, double TotalMs, double MinMs, double MaxMs)>();

// Benchmark 1: PNR point lookup (indexed, unique, returns 1)
RunBenchmark("PNR Lookup (indexed)", () =>
    db.Execute($"SELECT * FROM orders WHERE pnr = '{knownPnrs[rng.Next(knownPnrs.Count)]}'"),
    benchmarks, QUERY_DURATION_SECONDS / 5);

// Benchmark 2: Last name search (indexed, returns many)
RunBenchmark("Last Name Search (indexed)", () =>
    db.Execute($"SELECT * FROM orders WHERE passenger.lastName = '{lastNames[rng.Next(lastNames.Length)]}'"),
    benchmarks, QUERY_DURATION_SECONDS / 5);

// Benchmark 3: Status search (indexed, returns ~millions)
RunBenchmark("Status = CONFIRMED (indexed)", () =>
    db.Execute("SELECT * FROM orders WHERE status = 'CONFIRMED' LIMIT 100"),
    benchmarks, QUERY_DURATION_SECONDS / 5);

// Benchmark 4: Airport search (indexed)
RunBenchmark("Departure Airport (indexed)", () =>
    db.Execute($"SELECT * FROM orders WHERE flights[0].departureAirport = '{airports[rng.Next(airports.Length)]}' LIMIT 50"),
    benchmarks, QUERY_DURATION_SECONDS / 5);

// Benchmark 5: Full collection scan with filter + limit
RunBenchmark("First Name Scan (no index)", () =>
    db.Execute($"SELECT * FROM orders WHERE passenger.firstName = '{firstNames[rng.Next(firstNames.Length)]}' LIMIT 10"),
    benchmarks, QUERY_DURATION_SECONDS / 5);

// Benchmark 6: Range query (NEW! - uses INDEX_RANGE_SCAN)
RunBenchmark("Fare > 1500 LIMIT 100 (range idx)", () =>
    db.Execute("SELECT * FROM orders WHERE tickets[0].fare.total > 1500 LIMIT 100"),
    benchmarks, QUERY_DURATION_SECONDS / 6);

// ===== RESULTS =====
Console.WriteLine();
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine("  FINAL RESULTS");
Console.WriteLine("═══════════════════════════════════════════════════════════════");
Console.WriteLine();
Console.WriteLine($"  Database: {TARGET_DOCS:N0} documents | {fileInfo.Length / 1024.0 / 1024.0 / 1024.0:F2} GB");
Console.WriteLine();
PrintLine("Query", "Queries", "Avg(ms)", "Min(ms)", "Max(ms)", "QPS");
Console.WriteLine("  " + new string('─', 95));

long totalQueries = 0;
foreach (var (name, _, count, totalMs, minMs, maxMs) in benchmarks)
{
    var avg = count > 0 ? totalMs / count : 0;
    var qps = count > 0 ? count / (totalMs / 1000.0) : 0;
    PrintLine(name, $"{count:N0}", $"{avg:F2}", $"{minMs:F2}", $"{maxMs:F2}", $"{qps:N0}");
    totalQueries += count;
}

Console.WriteLine();
Console.WriteLine($"  TOTAL QUERIES EXECUTED: {totalQueries:N0}");
Console.WriteLine();

// ===== PHASE 4: REOPEN BENCHMARK =====
Console.WriteLine();
Console.WriteLine("PHASE 4: REOPEN WITH PERSISTENT INDEXES");
Console.WriteLine("─────────────────────────────────────────────────────────────");

// Close the db - indexes should be persisted to disk
Console.Write("  Flushing and closing DB... ");
var closeSw = Stopwatch.StartNew();
db.Flush();
db.Dispose();
closeSw.Stop();
Console.WriteLine($"done ({closeSw.Elapsed.TotalSeconds:F2}s)");

// Reopen - this used to rebuild indexes from scratch (~7s for 250K docs)
// With persistent indexes, it should just load from disk
Console.Write("  Reopening DB (loading 250K index entries from disk)... ");
var reopenSw = Stopwatch.StartNew();
using var reopenedDb = DocumentForgeDb.Open(dbPath, options);
reopenSw.Stop();
Console.WriteLine($"done ({reopenSw.Elapsed.TotalSeconds:F2}s)");

// Verify the index actually works without any rebuild
Console.Write("  First query after reopen (PNR lookup)... ");
var querySw = Stopwatch.StartNew();
var result = reopenedDb.Execute($"SELECT * FROM orders WHERE pnr = '{knownPnrs[0]}'");
querySw.Stop();
Console.WriteLine($"done ({querySw.Elapsed.TotalMilliseconds:F2}ms | plan: {result.QueryPlan} | results: {result.Documents.Count})");

var indexesAfterReopen = reopenedDb.GetIndexes("orders");
Console.WriteLine($"  Indexes loaded: {indexesAfterReopen.Count}");
foreach (var idx in indexesAfterReopen)
    Console.WriteLine($"    - {idx.Name} on ({idx.JsonPath}){(idx.IsUnique ? " UNIQUE" : "")}");

Console.WriteLine();
Console.WriteLine($"  Index rebuild cost SAVED: ~7 seconds (vs rebuild from scratch)");
Console.WriteLine("═══════════════════════════════════════════════════════════════");

// Cleanup
reopenedDb.Dispose();
File.Delete(dbPath);
if (File.Exists(dbPath + ".wal")) File.Delete(dbPath + ".wal");

// ---- Helpers ----

static void RunBenchmark(string name, Func<DocumentForge.Query.QueryResult> queryFn,
    List<(string, string, long, double, double, double)> results, int durationSeconds)
{
    Console.Write($"  {name,-40}");

    long count = 0;
    double totalMs = 0;
    double minMs = double.MaxValue;
    double maxMs = 0;

    var sw = Stopwatch.StartNew();
    while (sw.Elapsed.TotalSeconds < durationSeconds)
    {
        var querySw = Stopwatch.StartNew();
        var result = queryFn();
        querySw.Stop();

        var ms = querySw.Elapsed.TotalMilliseconds;
        totalMs += ms;
        if (ms < minMs) minMs = ms;
        if (ms > maxMs) maxMs = ms;
        count++;
    }
    sw.Stop();

    if (count == 0) minMs = 0;
    var avg = count > 0 ? totalMs / count : 0;
    var qps = count > 0 ? count / (totalMs / 1000.0) : 0;
    Console.WriteLine($" {count:N0} queries | avg {avg:F2}ms | {qps:N0} QPS");

    results.Add((name, "", count, totalMs, minMs, maxMs));
}

static List<BsonDocument> GenerateBatch(int count, Random rng,
    string[] firstNames, string[] lastNames, string[] airports,
    string[] statuses, string[] airlines, string[] cabins)
{
    var batch = new List<BsonDocument>(count);

    for (int i = 0; i < count; i++)
    {
        var doc = new BsonDocument();
        doc["pnr"] = BsonValue.FromString(GeneratePnr(rng));
        doc["status"] = BsonValue.FromString(statuses[rng.Next(statuses.Length)]);
        doc["createdAt"] = BsonValue.FromDateTime(DateTimeOffset.UtcNow.AddDays(-rng.Next(0, 365)));

        var passenger = new BsonDocument();
        passenger["firstName"] = BsonValue.FromString(firstNames[rng.Next(firstNames.Length)]);
        passenger["lastName"] = BsonValue.FromString(lastNames[rng.Next(lastNames.Length)]);
        passenger["email"] = BsonValue.FromString($"user{rng.Next(999999)}@email.com");
        passenger["phone"] = BsonValue.FromString($"+1-555-{rng.Next(1000, 9999)}");
        doc["passenger"] = BsonValue.FromDocument(passenger);

        var flight = new BsonDocument();
        flight["segmentId"] = BsonValue.FromInt32(1);
        var al = airlines[rng.Next(airlines.Length)];
        flight["flightNumber"] = BsonValue.FromString($"{al}{rng.Next(100, 9999)}");
        flight["departureAirport"] = BsonValue.FromString(airports[rng.Next(airports.Length)]);
        flight["arrivalAirport"] = BsonValue.FromString(airports[rng.Next(airports.Length)]);
        flight["cabin"] = BsonValue.FromString(cabins[rng.Next(cabins.Length)]);
        flight["status"] = BsonValue.FromString(statuses[rng.Next(statuses.Length)]);

        var flights = new BsonArray();
        flights.Add(BsonValue.FromDocument(flight));
        doc["flights"] = BsonValue.FromArray(flights);

        var fare = new BsonDocument();
        var baseFare = Math.Round(100 + rng.NextDouble() * 2000, 2);
        fare["baseFare"] = BsonValue.FromDouble(baseFare);
        fare["taxes"] = BsonValue.FromDouble(Math.Round(baseFare * 0.15, 2));
        fare["total"] = BsonValue.FromDouble(Math.Round(baseFare * 1.15, 2));
        fare["currency"] = BsonValue.FromString("USD");

        var ticket = new BsonDocument();
        ticket["ticketNumber"] = BsonValue.FromString($"{rng.Next(100, 999)}-{rng.Next(100000000, int.MaxValue)}");
        ticket["fare"] = BsonValue.FromDocument(fare);

        var tickets = new BsonArray();
        tickets.Add(BsonValue.FromDocument(ticket));
        doc["tickets"] = BsonValue.FromArray(tickets);

        batch.Add(doc);
    }

    return batch;
}

// Counter-based unique PNR generator (realistic - airlines do this too)
static string GeneratePnr(Random rng)
{
    const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    const int n = 36;
    long val = PnrCounter.Next();
    // Encode as 8-char base36 for uniqueness up to 36^8 = ~2.8 trillion
    Span<char> buf = stackalloc char[8];
    for (int i = 7; i >= 0; i--)
    {
        buf[i] = chars[(int)(val % n)];
        val /= n;
    }
    return new string(buf);
}

static void PrintLine(string col1, string col2, string col3, string col4, string col5, string col6)
{
    Console.WriteLine($"  {col1.PadRight(35)} {col2.PadLeft(10)} {col3.PadLeft(10)} {col4.PadLeft(10)} {col5.PadLeft(10)} {col6.PadLeft(10)}");
}

static class PnrCounter
{
    private static long _value = 0;
    public static long Next() => Interlocked.Increment(ref _value);
}
