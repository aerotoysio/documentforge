using System.Diagnostics;
using DocumentForge.Document;
using DocumentForge.Engine;

// Pure-insert ceiling micro-bench.
//
// The stress benchmark times document GENERATION and BulkInsert together on a
// single thread, so its headline "docs/sec" understates the engine: a chunk of
// the wall-clock is just building the nested BsonDocuments. This tool splits
// the two so we can see the engine's true insert ceiling (and judge whether a
// SqlBulkCopy-style turbo path is worth building).
//
//   dotnet run -c Release --project samples/DocumentForge.InsertBench   (200k default)
//   INSERTBENCH_N=1000000 dotnet run -c Release --project samples/DocumentForge.InsertBench

int N = int.TryParse(Environment.GetEnvironmentVariable("INSERTBENCH_N"), out var n) && n > 0 ? n : 200_000;
const int BATCH = 25_000;

string[] firstNames = { "James", "Mary", "John", "Patricia", "Robert", "Jennifer", "Michael", "Linda", "William", "Elizabeth" };
string[] lastNames = { "Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis", "Wilson", "Anderson" };
string[] airports = { "JFK", "LAX", "ORD", "LHR", "CDG", "FRA", "DXB", "SIN", "SYD", "NRT" };
string[] statuses = { "CONFIRMED", "CONFIRMED", "CONFIRMED", "TICKETED", "CANCELLED", "PENDING" };
string[] airlines = { "AA", "UA", "DL", "BA", "LH", "AF", "EK", "SQ" };
string[] cabins = { "ECONOMY", "PREMIUM_ECONOMY", "BUSINESS", "FIRST" };

Console.WriteLine($"Pure-insert micro-bench — {N:N0} docs, batch {BATCH:N0}, WAL off, no indexes (stress-bench doc shape)\n");

var rng = new Random(42);

// Phase A — generate every doc up front. This is the cost the stress bench
// folds into its insert number.
var genSw = Stopwatch.StartNew();
var docs = new List<BsonDocument>(N);
for (int i = 0; i < N; i++) docs.Add(MakeDoc(rng, firstNames, lastNames, airports, statuses, airlines, cabins));
genSw.Stop();
double genRate = N / genSw.Elapsed.TotalSeconds;
Console.WriteLine($"  Generation only : {genSw.Elapsed.TotalSeconds,6:F2}s  =>  {genRate,10:N0} docs/sec");

// Phase B — pure BulkInsert of the pre-built docs. THIS is the engine ceiling.
var dbPath = Path.Combine(Path.GetTempPath(), $"insertbench_{Guid.NewGuid():N}.dfdb");
Cleanup(dbPath);
var options = new DatabaseOptions { CacheSizeInPages = 200_000, EnableWal = false };
double insRate;
using (var db = DocumentForgeDb.Create(dbPath, options))
{
    int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
    long alloc = GC.GetTotalAllocatedBytes();
    var insSw = Stopwatch.StartNew();
    for (int off = 0; off < N; off += BATCH)
        db.BulkInsert("orders", docs.GetRange(off, Math.Min(BATCH, N - off)));
    insSw.Stop();
    insRate = N / insSw.Elapsed.TotalSeconds;
    Console.WriteLine($"  BulkInsert only : {insSw.Elapsed.TotalSeconds,6:F2}s  =>  {insRate,10:N0} docs/sec   <-- engine ceiling");
    Console.WriteLine($"  GC during insert: gen0 {GC.CollectionCount(0) - g0}, gen1 {GC.CollectionCount(1) - g1}, gen2 {GC.CollectionCount(2) - g2}; allocated {(GC.GetTotalAllocatedBytes() - alloc) / 1048576.0:F0} MB");
    db.Flush();
}
Cleanup(dbPath);

// What the stress bench actually measures: generation + insert on one thread.
double combined = 1.0 / (1.0 / genRate + 1.0 / insRate);
Console.WriteLine();
Console.WriteLine($"  Combined gen+insert (≈ stress bench) ≈ {combined,10:N0} docs/sec");
Console.WriteLine($"  => the INSERT path is the bottleneck: generation is only ~{100 * combined / genRate:F0}% of wall-clock, insert ~{100 * combined / insRate:F0}%.");
Console.WriteLine($"     Pre-built docs gain only ~{(insRate / combined - 1) * 100:F0}%; real speedups must come from the insert hot path (or sharding).");

static void Cleanup(string dbPath)
{
    foreach (var f in new[] { dbPath, dbPath + ".wal" })
        if (File.Exists(f)) { try { File.Delete(f); } catch { /* best effort */ } }
}

static BsonDocument MakeDoc(Random rng, string[] firstNames, string[] lastNames, string[] airports, string[] statuses, string[] airlines, string[] cabins)
{
    var doc = new BsonDocument();
    Span<char> pnr = stackalloc char[8];
    const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    for (int i = 0; i < 8; i++) pnr[i] = chars[rng.Next(chars.Length)];
    doc["pnr"] = BsonValue.FromString(new string(pnr));
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
    flight["flightNumber"] = BsonValue.FromString($"{airlines[rng.Next(airlines.Length)]}{rng.Next(100, 9999)}");
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

    return doc;
}
