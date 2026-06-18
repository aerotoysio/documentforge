using System.Diagnostics;
using DocumentForge.Document;
using DocumentForge.Engine;
using DocumentForge.Index;
using DocumentForge.Query;

// ScaleBench — large-scale streaming insert + read benchmark.
//
// Unlike InsertBench (which pre-materializes every doc in a List and so is
// capped by RAM), this streams generate->BulkInsert->discard so peak managed
// heap stays flat regardless of N. That's what makes 100M docs viable on a
// 32 GB box: the engine is disk-backed (LRU page cache), only the harness
// was the bottleneck.
//
// Phases are INDEPENDENT and the DB file is PERSISTENT, so the expensive
// insert is banked to disk and reads can be replayed cheaply:
//
//   SCALEBENCH_PHASE=insert  -> stream N docs into the file, flush, exit
//   SCALEBENCH_PHASE=index   -> open file, build configured indexes, exit
//   SCALEBENCH_PHASE=read    -> open file, run read benchmarks, exit
//   SCALEBENCH_PHASE=all     -> insert, then index, then read (default)
//
// Env knobs (all optional):
//   SCALEBENCH_N            doc count                (default 100,000,000)
//   SCALEBENCH_BATCH        docs per BulkInsert      (default 2,000)
//   SCALEBENCH_PATH         .dfdb file path          (default ./scalebench-data/scale.dfdb)
//   SCALEBENCH_INDEXES      csv: pnr,lastname,status,departure,fare | none | all (default pnr)
//   SCALEBENCH_CACHE_PAGES  page-cache size (8KB ea) (default 200,000 = 1.6 GB)
//   SCALEBENCH_QSECS        sustained secs per read benchmark (default 10)
//   SCALEBENCH_FLUSH_EVERY  flush every K inserts for crash insurance (default 5,000,000)
//
//   dotnet run -c Release --project samples/DocumentForge.ScaleBench

long N = EnvLong("SCALEBENCH_N", 100_000_000);
int BATCH = (int)EnvLong("SCALEBENCH_BATCH", 2_000);
int CACHE = (int)EnvLong("SCALEBENCH_CACHE_PAGES", 200_000);
int QSECS = (int)EnvLong("SCALEBENCH_QSECS", 10);
long FLUSH_EVERY = EnvLong("SCALEBENCH_FLUSH_EVERY", 5_000_000);
string phase = (Environment.GetEnvironmentVariable("SCALEBENCH_PHASE") ?? "all").Trim().ToLowerInvariant();
string indexSpec = (Environment.GetEnvironmentVariable("SCALEBENCH_INDEXES") ?? "pnr").Trim().ToLowerInvariant();

string dbPath = Environment.GetEnvironmentVariable("SCALEBENCH_PATH")
                ?? Path.Combine(Directory.GetCurrentDirectory(), "scalebench-data", "scale.dfdb");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

var options = new DatabaseOptions { CacheSizeInPages = CACHE, EnableWal = false };

// Which indexes to build. pnr is the headline (unique point-lookup at scale).
var allIndexes = new (string Path, string Name, bool Unique)[]
{
    ("pnr",                          "idx_pnr",       true),
    ("passenger.lastName",           "idx_lastname",  false),
    ("status",                       "idx_status",    false),
    ("flights[0].departureAirport",  "idx_departure", false),
    ("tickets[0].fare.total",        "idx_fare",      false),
};
var wantIndexes = indexSpec switch
{
    "none" or "" => Array.Empty<(string Path, string Name, bool Unique)>(),
    "all"        => allIndexes,
    _            => allIndexes.Where(ix =>
                        indexSpec.Split(',').Select(s => s.Trim()).Contains(ShortName(ix.Name))).ToArray(),
};

string[] firstNames = { "James", "Mary", "John", "Patricia", "Robert", "Jennifer", "Michael", "Linda", "William", "Elizabeth", "David", "Barbara", "Richard", "Susan", "Joseph", "Jessica", "Thomas", "Sarah", "Charles", "Karen", "Christopher", "Lisa", "Daniel", "Nancy", "Matthew", "Betty", "Anthony", "Margaret", "Mark", "Sandra", "Donald", "Ashley", "Steven", "Kimberly", "Paul", "Emily", "Andrew", "Donna", "Joshua", "Michelle" };
string[] lastNames = { "Smith", "Johnson", "Williams", "Brown", "Jones", "Garcia", "Miller", "Davis", "Rodriguez", "Martinez", "Hernandez", "Lopez", "Gonzalez", "Wilson", "Anderson", "Thomas", "Taylor", "Moore", "Jackson", "Martin", "Lee", "Perez", "Thompson", "White", "Harris", "Sanchez", "Clark", "Ramirez", "Lewis", "Robinson" };
string[] airports = { "JFK", "LAX", "ORD", "LHR", "CDG", "FRA", "DXB", "SIN", "SYD", "NRT", "ATL", "DFW", "DEN", "SFO", "SEA", "MIA", "BOS", "IAD", "EWR", "PHX" };
string[] statuses = { "CONFIRMED", "CONFIRMED", "CONFIRMED", "CONFIRMED", "TICKETED", "TICKETED", "CANCELLED", "PENDING" };
string[] airlines = { "AA", "UA", "DL", "BA", "LH", "AF", "EK", "SQ" };
string[] cabins = { "ECONOMY", "PREMIUM_ECONOMY", "BUSINESS", "FIRST" };

Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║  DocumentForge — ScaleBench                                   ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
Console.WriteLine($"  phase={phase}  N={N:N0}  batch={BATCH:N0}  cache={CACHE:N0} pages ({CACHE * 8192L / 1024.0 / 1024.0:F0} MB)");
Console.WriteLine($"  indexes=[{string.Join(",", wantIndexes.Select(i => ShortName(i.Name)))}]  qsecs={QSECS}");
Console.WriteLine($"  path={dbPath}");
Console.WriteLine();

bool doInsert = phase is "insert" or "all";
bool doIndex = phase is "index" or "all";
bool doRead = phase is "read" or "all";

// ===================== INSERT =====================
if (doInsert)
{
    foreach (var f in new[] { dbPath, dbPath + ".wal" })
        if (File.Exists(f)) File.Delete(f);

    Console.WriteLine("PHASE: INSERT (streaming, flat RAM)");
    Console.WriteLine("──────────────────────────────────────────────────────────────");
    using var db = DocumentForgeDb.Create(dbPath, options);

    long progressEvery = Math.Max(BATCH, RoundTo(N / 100, BATCH)); // ~100 progress lines
    long lastFlush = 0;
    var rng = new Random(42);
    var sw = Stopwatch.StartNew();
    long inserted = 0;
    var batch = new List<BsonDocument>(BATCH);

    while (inserted < N)
    {
        int count = (int)Math.Min(BATCH, N - inserted);
        batch.Clear();
        for (int i = 0; i < count; i++)
            batch.Add(MakeDoc(rng, firstNames, lastNames, airports, statuses, airlines, cabins));
        db.BulkInsert("orders", batch);
        inserted += count;

        if (inserted - lastFlush >= FLUSH_EVERY) { db.Flush(); lastFlush = inserted; }

        if (inserted % progressEvery == 0 || inserted == N)
        {
            double rate = inserted / sw.Elapsed.TotalSeconds;
            var eta = TimeSpan.FromSeconds(Math.Max(0, (N - inserted) / Math.Max(rate, 1)));
            double mem = GC.GetTotalMemory(false) / 1048576.0;
            double ws = Process.GetCurrentProcess().WorkingSet64 / 1048576.0;
            Console.WriteLine($"  {inserted,14:N0} / {N:N0} ({inserted * 100.0 / N,5:F1}%) | {rate,9:N0}/s | heap {mem,6:F0}MB ws {ws,6:F0}MB | ETA {eta:d\\.hh\\:mm\\:ss}");
        }
    }
    sw.Stop();

    Console.Write("  Flushing... ");
    var fsw = Stopwatch.StartNew();
    db.Flush();
    fsw.Stop();
    double sizeGb = new FileInfo(dbPath).Length / 1073741824.0;
    Console.WriteLine($"done ({fsw.Elapsed.TotalSeconds:F1}s)");
    Console.WriteLine($"  INSERTED {inserted:N0} in {sw.Elapsed:d\\.hh\\:mm\\:ss} = {inserted / sw.Elapsed.TotalSeconds:N0} docs/sec");
    Console.WriteLine($"  File: {sizeGb:F2} GB  ({new FileInfo(dbPath).Length / (double)inserted:F0} bytes/doc on disk)");
    Console.WriteLine();
}

// ===================== INDEX =====================
if (doIndex && wantIndexes.Length > 0)
{
    Console.WriteLine("PHASE: INDEX");
    Console.WriteLine("──────────────────────────────────────────────────────────────");
    using var db = DocumentForgeDb.Open(dbPath, options);
    foreach (var (jsonPath, name, unique) in wantIndexes)
    {
        Console.Write($"  {name,-16} on {jsonPath,-30} ");
        var isw = Stopwatch.StartNew();
        db.CreateIndex("orders", jsonPath, name, unique);
        isw.Stop();
        double ws = Process.GetCurrentProcess().WorkingSet64 / 1048576.0;
        double heap = GC.GetTotalMemory(false) / 1048576.0;
        Console.WriteLine($"built in {isw.Elapsed.TotalSeconds,7:F1}s | heap {heap:F0}MB ws {ws:F0}MB");
    }
    Console.Write("  Building location map... ");
    var lsw = Stopwatch.StartNew();
    db.GetCollection("orders")!.BuildLocationMap();
    lsw.Stop();
    Console.WriteLine($"done ({lsw.Elapsed.TotalSeconds:F1}s)");
    db.Flush();
    Console.WriteLine();
}

// ===================== READ =====================
if (doRead)
{
    Console.WriteLine("PHASE: READ");
    Console.WriteLine("──────────────────────────────────────────────────────────────");
    using var db = DocumentForgeDb.Open(dbPath, options);

    long count = N;
    var stats = db.GetStatistics();
    var orders = stats.Collections.FirstOrDefault(c => c.Name == "orders");
    if (orders != null && orders.DocumentCount > 0) count = orders.DocumentCount;

    var live = db.GetIndexes("orders").Select(i => i.Name).ToHashSet();
    bool hasPnr = live.Contains("idx_pnr");
    bool hasLast = live.Contains("idx_lastname");
    bool hasStatus = live.Contains("idx_status");
    double fileGb = new FileInfo(dbPath).Length / 1073741824.0;
    Console.WriteLine($"  {count:N0} docs | {fileGb:F2} GB | indexes: [{string.Join(",", live)}]");
    Console.WriteLine();

    var rng = new Random(7);

    // Warm the JIT / query pipeline.
    db.Execute($"SELECT * FROM orders WHERE pnr = '{Base36(1)}'");

    PrintHead();

    // 1. Point lookup by PNR. Keys are synthesized (base36 of a random doc
    //    ordinal) so every lookup hits exactly one row — no pre-scan needed.
    if (hasPnr)
        RunSustained("PNR point lookup", QSECS, () =>
            db.Execute($"SELECT * FROM orders WHERE pnr = '{Base36(rng.NextInt64(1, count + 1))}'"));
    else
        Console.WriteLine("  PNR point lookup            SKIPPED (no idx_pnr — would be a full scan)");

    // 2. Last-name match (many rows). Indexed if available, else a scan.
    RunSustained($"Last-name = X LIMIT 100 [{(hasLast ? "idx" : "scan")}]", QSECS, () =>
        db.Execute($"SELECT * FROM orders WHERE passenger.lastName = '{lastNames[rng.Next(lastNames.Length)]}' LIMIT 100"));

    // 3. Status = CONFIRMED (very low cardinality). Indexed if available.
    RunSustained($"Status = CONFIRMED LIMIT 100 [{(hasStatus ? "idx" : "scan")}]", QSECS, () =>
        db.Execute("SELECT * FROM orders WHERE status = 'CONFIRMED' LIMIT 100"));

    // 4. First-name filter, never indexed. Matches ~1/40 so the scan
    //    short-circuits after ~hundreds of rows: "find a few needles" latency.
    RunSustained("First-name = X LIMIT 10 [scan,early-out]", QSECS, () =>
        db.Execute($"SELECT * FROM orders WHERE passenger.firstName = '{firstNames[rng.Next(firstNames.Length)]}' LIMIT 10"));

    Console.WriteLine();
    Console.WriteLine("  FULL SCAN (no match -> reads every document):");
    // 5. The raw read-throughput number: filter on a value that exists in no
    //    document forces a complete linear scan of all N docs.
    var scanTimes = new List<double>();
    for (int r = 0; r < 3; r++)
    {
        var qsw = Stopwatch.StartNew();
        var res = db.Execute("SELECT * FROM orders WHERE passenger.firstName = '__no_such_name__' LIMIT 1");
        qsw.Stop();
        scanTimes.Add(qsw.Elapsed.TotalSeconds);
        Console.WriteLine($"    run {r + 1}: {qsw.Elapsed.TotalSeconds,7:F2}s  | {count / qsw.Elapsed.TotalSeconds,12:N0} docs/sec scanned | {fileGb / qsw.Elapsed.TotalSeconds,6:F2} GB/sec | plan {res.QueryPlan}");
    }
    double best = scanTimes.Min();
    Console.WriteLine($"    best: {best:F2}s => {count / best:N0} docs/sec, {fileGb / best:F2} GB/sec over {fileGb:F1} GB");
    Console.WriteLine();
}

Console.WriteLine("Done.");
if (doInsert || doIndex)
{
    Console.WriteLine($"DB persisted at: {dbPath}");
    Console.WriteLine($"Replay reads with:  SCALEBENCH_PHASE=read SCALEBENCH_PATH=\"{dbPath}\" dotnet run -c Release --project samples/DocumentForge.ScaleBench");
}

// ---------------- helpers ----------------

static void PrintHead() =>
    Console.WriteLine($"  {"benchmark",-44} {"queries",10} {"avg ms",9} {"p-min",8} {"p-max",9} {"QPS",10}");

static void RunSustained(string name, int seconds, Func<QueryResult> q)
{
    long n = 0; double total = 0, min = double.MaxValue, max = 0;
    string? plan = null;
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed.TotalSeconds < seconds)
    {
        var qsw = Stopwatch.StartNew();
        var res = q();
        qsw.Stop();
        plan ??= res.QueryPlan;
        double ms = qsw.Elapsed.TotalMilliseconds;
        total += ms; n++;
        if (ms < min) min = ms;
        if (ms > max) max = ms;
    }
    sw.Stop();
    if (n == 0) { min = 0; }
    double avg = n > 0 ? total / n : 0;
    double qps = total > 0 ? n / (total / 1000.0) : 0;
    Console.WriteLine($"  {name,-44} {n,10:N0} {avg,9:F3} {min,8:F3} {max,9:F2} {qps,10:N0}   {plan}");
}

static long RoundTo(long v, long step) => step <= 0 ? v : Math.Max(step, (v / step) * step);

static string ShortName(string indexName) =>
    indexName.StartsWith("idx_") ? indexName.Substring(4) : indexName;

static long EnvLong(string key, long fallback) =>
    long.TryParse(Environment.GetEnvironmentVariable(key), out var v) && v > 0 ? v : fallback;

// base36(ordinal) — identical encoding to the insert-side counter, so the read
// phase can reconstruct any document's PNR from its 1-based ordinal.
static string Base36(long val)
{
    const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    Span<char> buf = stackalloc char[8];
    for (int i = 7; i >= 0; i--) { buf[i] = chars[(int)(val % 36)]; val /= 36; }
    return new string(buf);
}

static BsonDocument MakeDoc(Random rng, string[] firstNames, string[] lastNames, string[] airports, string[] statuses, string[] airlines, string[] cabins)
{
    var doc = new BsonDocument();
    doc["pnr"] = BsonValue.FromString(Base36(PnrCounter.Next()));
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

static class PnrCounter
{
    private static long _value = 0;
    public static long Next() => Interlocked.Increment(ref _value);
}
