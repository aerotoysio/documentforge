# DocumentForge

**An embedded JSON document database for .NET. SQL-like queries, persistent indexes,
replication, sharding — in ~6,000 lines with zero external dependencies.**

**One binary does it all:**

```bash
dfdb serve    --port 5000 --api-key <key>         # run a node (auth is required;
                                                  #  --insecure-dev-mode for local-only tinkering)
dfdb repl     ./data/data.dfdb                    # interactive SQL
dfdb query    ./data/data.dfdb "SELECT * FROM o"  # one-shot query
dfdb seed     ./data/data.dfdb 10000              # demo data
dfdb cluster  init cluster.json                   # cluster config
dfdb health   cluster.json                        # ping every shard
dfdb rebalance old.json new.json                  # safely reshape
```

Or embed as a library:

```csharp
using var db = DocumentForgeDb.OpenOrCreate("airline.dfdb");

db.Insert("orders", """
{
    "pnr": "ABC123",
    "passenger": { "firstName": "John", "lastName": "Smith" },
    "flights": [{ "flightNumber": "AA100", "from": "JFK", "to": "LAX" }]
}
""");

db.CreateIndex("orders", "pnr", "idx_pnr", unique: true);

var result = db.Execute("SELECT * FROM orders WHERE pnr = 'ABC123'");
// 0.00ms, 210K QPS sustained
```

## Features

- **JSON-native, SQL-fluent** – nested paths, arrays, joins, aggregations, composite indexes
- **Sub-millisecond lookups** – direct-address location map, mainframe-inspired
- **Persistent B-tree indexes** – survive restart, no rebuild
- **Overflow pages** – documents larger than a page are transparently chained
- **Write-ahead recovery log** with CRC32 checksums per record
- **Page checksums** – silent corruption detection on every read
- **Crash recovery** – replays uncommitted writes on startup
- **LINQ API** – `db.Collection<Order>("orders").Where(o => o.Pnr == "ABC").FirstOrDefault()`
- **Logical replication** – monotonic sequence numbers, catchup on reconnect, heartbeats
- **Planned handover** – zero-data-loss datacenter moves
- **Auto-failover** – follower promotes on leader silence
- **Consistent-hash sharding** – add a shard without re-routing all data
- **Replicated reference collections** – small tables live on every shard; joins stay local

## Performance

Verified on a 250K and 10M document workload (modern laptop, NVMe SSD):

|                      | 250K docs | 10M docs |
|---|---|---|
| Bulk insert rate     | 70K/sec   | 41K/sec |
| Indexed point lookup | 210K QPS  | 163K QPS |
| Range query LIMIT 100| 144 QPS   | 47 QPS |
| File size            | 120 MB    | 4.77 GB |

Full methodology lives in the [documentation site](https://github.com/aerotoysio/documentforge-docs) — see `performance.html`.

## Getting started

### Option A — download the `dfdb` binary (no .NET required)

Build once, ship anywhere — self-contained, no runtime needed on the target machine:

```bash
.\scripts\publish-dfdb.ps1   # Windows
./scripts/publish-dfdb.sh    # macOS/Linux
```

Produces `dist/win-x64/dfdb.exe` (and friends) — drop on any server:

```bash
# Serve a node — an API key is required (deny-by-default).
# For throwaway local tinkering you can pass --insecure-dev-mode instead
# (loopback only; every request is admin until a key is added).
./dfdb serve --port 5000 --data-dir ./data --api-key "$(openssl rand -hex 24)"

# Seed + query
./dfdb seed  ./data/data.dfdb 10000
./dfdb query ./data/data.dfdb "SELECT COUNT(*) FROM orders"
```

### Option B — embed as a NuGet package in your .NET app

```bash
dotnet add package DocumentForge
```

```csharp
using DocumentForge.Engine;

using var db = DocumentForgeDb.OpenOrCreate("app.dfdb");
db.Insert("users", """{"name": "Alice", "email": "alice@example.com"}""");
var result = db.Execute("SELECT * FROM users WHERE name = 'Alice'");
```

Full guide in the [docs site](https://github.com/aerotoysio/documentforge-docs) — see `getting-started.html`.

## Documentation

The docs live in a separate repo so they're easy to host on GitHub Pages:

**[📘 aerotoysio/documentforge-docs](https://github.com/aerotoysio/documentforge-docs)**

10 pages covering: Getting Started · Query Language · Data Modeling · Replication ·
Sharding · Deployment (incl. Docker + Render) · Security · CLI Reference ·
Performance · Postman collection.

## Repo layout

```
src/
  DocumentForge.Core         Core types (PageId, DocumentId, Exceptions)
  DocumentForge.Storage      Page layout, cache, data file, checksums
  DocumentForge.Document     BSON serialization, Collection CRUD
  DocumentForge.Index        B-tree indexes + persistence
  DocumentForge.Query        SQL lexer, parser, executor, optimizer
  DocumentForge.Transactions WAL, replication, auto-failover
  DocumentForge.Engine       DocumentForgeDb, LINQ API, Cluster router
  DocumentForge.Cli          ✨ unified `dfdb` binary (serve + cli)

tests/
  DocumentForge.Tests        48 unit + integration tests

samples/                     Demos showing how to use the library
  DocumentForge.AirlineDemo  10K-order airline reservation demo
  DocumentForge.Repl         Interactive SQL console (same as `dfdb repl`)
  DocumentForge.Api          REST API (same as `dfdb serve`)
  DocumentForge.Benchmark    250K / 10M document stress test
  DocumentForge.Ctl          `dfctl` management CLI (superseded by `dfdb`)

src/DocumentForge.Studio/    DocumentForge Studio — Windows desktop admin app (WPF)
scripts/                     start-cluster, publish-dfdb, build-studio-installer, sample-cluster/
Dockerfile                   Multi-stage build → self-contained dfdb image
render.yaml                  Render blueprint: one-click private deploy

(Documentation site lives in the separate aerotoysio/documentforge-docs repo.)
```

## Status

DocumentForge is a learning-focused open-source project demonstrating how a
modern document database can be built from the ground up in a few thousand
lines of C#. It passes a comprehensive test suite and has the architectural
pieces you'd expect from a production system.

Use it for embedded scenarios, prototypes, small-to-medium production workloads,
and understanding how databases actually work.

## License

MIT
