# DocumentForge

**An embedded JSON document database for .NET. SQL-like queries, persistent indexes,
replication, sharding — in ~5,000 lines with zero external dependencies.**

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

See [docs/performance.html](docs/performance.html) for full methodology.

## Getting started

```bash
dotnet add package DocumentForge
```

Then in code:

```csharp
using DocumentForge.Engine;

using var db = DocumentForgeDb.OpenOrCreate("app.dfdb");
db.Insert("users", """{"name": "Alice", "email": "alice@example.com"}""");
var result = db.Execute("SELECT * FROM users WHERE name = 'Alice'");
```

Full guide: [docs/getting-started.html](docs/getting-started.html)

## Documentation

- [Getting Started](docs/getting-started.html)
- [Query Language](docs/query-language.html)
- [Data Modeling](docs/data-modeling.html) — embed vs reference
- [Replication](docs/replication.html) — logical, physical, planned handover
- [Deployment](docs/deployment.html) — single-node to multi-region
- [Performance](docs/performance.html) — real numbers, limits, tuning

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

tests/
  DocumentForge.Tests        44+ unit and integration tests

samples/
  DocumentForge.AirlineDemo  10K-order airline reservation demo
  DocumentForge.Repl         Interactive SQL console
  DocumentForge.Api          REST API for Postman / remote shards
  DocumentForge.Benchmark    250K / 10M document stress test
  DocumentForge.Ctl          `dfctl` management CLI

admin-ui/                    Next.js 15 admin web UI (separate app)
docs/                        Swiss-style documentation website
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
