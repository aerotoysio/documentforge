# DocumentForge — In-flight status

Last updated: 2026-05-21 · branch `master`

This file is a pickup point — clone the repo on a different machine, `git pull`, read this, and you should know exactly where to continue.

---

## TL;DR

Issue #66 (multi-database hosting) is feature-complete through the visual cluster builder. One `dfdb serve` process hosts N attached databases; Studio manages the whole lifecycle on a single react-flow canvas — drag on a cluster / service / database, wire DBs together for replication, claim DBs into shard rings, and export `cluster.json`. The Studio Explorer, Dashboard and Admin pages are all multi-connection / multi-DB aware.

The remaining big rock is the **developer portal (#68)** — now in progress. After that, the production-readiness tail of #66 (lazy open + per-DB quotas, auth scopes) closes the epic.

---

## What's done (on `master`, all pushed)

Multi-DB epic (#66):

```
Phase 1   — DatabaseRegistry (N attached engines in one process)            ✅
Phase 2   — /databases REST + Studio databases surface                      ✅
Phase 2.5 — Scoped /db/{name}/replication/* routes                          ✅
Phase 3a  — Scoped /db/{name}/query + per-tab DB picker                      ✅
Phase 5a  — Spawn child dfdb serve from Studio (ServiceManager)             ✅
Phase 5b  — Across-services topology view (cross-connection graph)          ✅
Phase 5c  — Shard frame grouping                                            ✅
Phase 5d  — Drag-to-wire replication + drag-to-reshard                      ✅
Phase 6   — dfdb router (stateless cluster gateway)                         ✅
Phase 6b  — Collections tab + Spawn router from Studio                       ✅
Phase 6c  — Unified cluster builder canvas: drag cluster/service/database,   ✅
            claim-to-ring, resizable service frames, live-vs-conceptual
            health dots, export/copy/download cluster.json
            (design persisted to localStorage)
```

Studio / portal alignment:

```
Explorer  — every connection → its databases → collections, with a          ✅
            /stats fallback so pre-#66 single-DB services show reachable
Dashboard — cross-connection overview (reachability probed via /stats)      ✅
Admin     — connection picker + databases panel                            ✅
"active" connection reframed as "default" connection                        ✅
```

Engine fixes / hardening:

```
#62 — typed LINQ Insert/Where JSON casing mismatch       ✅ (closed)
#63 — FormatLiteral missing Guid literal case            ✅ (closed)
#64 — self-heal corrupt index catalog (PR #65, merged)   ✅
Stage A — single-writer / multiple-reader concurrency    ✅
          (ReaderWriterLockSlim in TransactionManager)
Insert hot path — dropped two per-doc allocations (a)+(b) ✅
```

## What's queued (priority order)

| Item | Description | Effort |
|---|---|---|
| **#68** *(in progress)* | Developer portal — Nextra docs site: getting-started, REST + CLI reference, LINQ & clustering guides, runnable examples | ~3–4 days |
| **#66 Phase 3b** | Lazy DB open + per-DB quotas + idle eviction (host 50+ DBs per process) | ~1 day |
| **#66 Phase 4** | Bearer auth scopes (`db:foo`, `db:*`, `*`) + per-request DB scoping | ~1–2 days |
| **#70** | Partial-document update (PATCH) / atomic field ops — a new write path | medium |
| **#69** | Reads against a non-existent collection return HTTP 400 instead of an empty result | quick |

Deferred (perf / debt, opportunistic):

- **BSON serializer rework** — the real insert-rate lever. Insert is allocation-bound (~6.8 KB allocated/doc; BSON serialization dominates), while the engine logic is only ~5.5 µs/doc. The (a)+(b) wins trimmed allocation but throughput is unchanged until the serializer itself is rebuilt. Worth a dedicated session. See `samples/DocumentForge.InsertBench`.
- SqlBulkCopy-style turbo bulk-load (unsafe ingest + reindex after, or wrapped in a transaction with a size cap).
- Benign build warnings (SYSLIB0057, a few xUnit analyzer nags).
- 12 Dependabot alerts on the repo.

## To resume

```bash
git clone https://github.com/aerotoysio/documentforge
cd documentforge
git checkout master
dotnet build
dotnet test                      # full suite green; one known-flaky LogicalReplication test
cd admin-ui && npm install
```

Run the stack locally:

```bash
# Terminal A — host service (Studio connects here)
dotnet src/DocumentForge.Cli/bin/Release/net9.0/dfdb.dll serve --data-dir /tmp/dfdb_dev --port 5099

# Terminal B — Studio (port 3000 is often taken locally; 3001 is the dev default here)
cd admin-ui
NEXT_PUBLIC_DFDB_URL=http://localhost:5099 npx next dev -p 3001

# Browser
http://localhost:3001/studio       # SQL editor + Explorer (all connections → DBs → collections)
http://localhost:3001/topology     # unified cluster builder canvas
http://localhost:3001/connections  # add connections + spawn services/routers
```

To spin up a router via the REST endpoint:

```bash
curl -X POST -H "Content-Type: application/json" -d @- http://localhost:5099/routers <<EOF
{
  "name": "demo",
  "clusterConfigJson": "{
    \"shards\": [
      { \"name\": \"ring-a\", \"leader\": { \"baseUrl\": \"http://localhost:5099\", \"database\": \"ring_a\" } },
      { \"name\": \"ring-b\", \"leader\": { \"baseUrl\": \"http://localhost:5099\", \"database\": \"ring_b\" } }
    ],
    \"collections\": [
      { \"name\": \"orders\", \"strategy\": \"hash\", \"shardKeyPath\": \"pnr\" },
      { \"name\": \"lookup\", \"strategy\": \"replicated\" }
    ]
  }"
}
EOF
```

…or build it visually on the Topology canvas and hit "Spawn router".

---

## Architecture cheat-sheet

```
┌─── Service (one dfdb serve process, one HTTP port) ──────┐
│  DatabaseRegistry holds N DocumentForgeDb instances       │
│                                                            │
│   default (DB)    alpha (DB)    beta (DB)    ...           │
│      │              │              │                       │
│      └──── each has own WAL, lock, page cache, indexes ───┘
│                                                            │
│  ServiceManager spawns child dfdb processes:               │
│    - dfdb serve children (more attached-DB hosts)          │
│    - dfdb router children (cluster gateways)               │
└────────────────────────────────────────────────────────────┘

         ┌───── dfdb router (stateless) ─────┐
         │  loads cluster.json                │   ← one URL fronts the whole cluster
         │  routes per (collection, strategy) │
         └──┬──────────────┬──────────────────┘
            │              │
       ring-a leader   ring-b leader   ...
       (a service+DB)  (a service+DB)
            │              │
        followers      followers     ← logical replication, same TCP protocol
```

Rings can be backed by either a specific attached DB on a multi-DB host
(`baseUrl + database`) or a separate service's default DB (`baseUrl` only).
The same router config works for both — peel a ring from local-dev to prod by
editing one field.

## On-disk format / upgrade notes

The per-database file format is `FileFormatVersion = 1` (magic `DFDB`); `DataFile.ValidateHeader` hard-rejects anything else. Multi-DB hosting is additive — `DatabaseRegistry` maps names → separate `.dfdb` files; it does not change the file format. So upgrading the engine over an existing data file is a no-op migration: point the new build at the old `.dfdb` and it opens as the `default` database. Indexes self-heal (#64). Back up the file and do a clean flush before swapping binaries; downgrading (new file → older build) is the riskier direction and isn't guaranteed clean.

## Open questions

- **Phase 4** auth shape: scopes as `db:foo` (one DB), `db:*` (all DBs), plus the existing `*` admin — standard JWT-style scopes or a custom claim format?
- **#68** docs hosting: static export to GitHub Pages vs. a hosted target; where the API reference is sourced from (hand-written MDX vs. generated from the route table).
