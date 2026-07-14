# DocumentForge — In-flight status

Last updated: 2026-07-13 · branch `master`

This file is a pickup point — clone the repo on a different machine, `git pull`, read this, and you should know exactly where to continue.

---

## TL;DR

The July 5 Studio backlog (#113–#119, seven issues) is **fully shipped** — Studio (the WPF desktop app) now covers connect-to-follower from the topology graph, a service settings editor, a swarm panel for child services, query autocomplete, PITR + WAL-archiving + backup-settings wizards, one-click cluster-config distribution, and a full rebalance flow (plan preview → execute → live progress) backed by new engine HTTP endpoints.

The remaining big rocks are the **developer portal (#68)**, the production-readiness tail of **#66** (lazy open + per-DB quotas, auth scopes), and **#73** (macOS arm64 startup crash — needs a Mac to reproduce). Dependabot is clean: all 14 alerts pointed at the retired admin-ui's package-lock and were dismissed as not-used (2026-07-07).

New since 2026-07-13: a **relational-parity review** (driven by AeroBus) confirmed the engine already has the hard relational pieces — multi-doc transactions (#89–91) and per-collection schemas (#106) — but they're only reachable on the *default* database's flat routes. Closing that surface gap is now the top of the queue; see the review section below.

---

## What shipped 2026-07-07 (all on `master`, all pushed)

Studio backlog (#113–#119):

```
#119 — topology graph: click a follower (advertised HTTP endpoint) to connect  ✅
#115 — Service Settings panel over GET/PUT /admin/config (#111); semi-sync     ✅
       knobs live-editable, restart-required fields labelled
#118 — Services (swarm) panel: spawn/list/kill/log-tail child dfdb services;   ✅
       children inherit the parent's admin key; one-click connect
#114 — query workbench autocomplete: keywords/functions, collection names      ✅
       after FROM/INTO/UPDATE/JOIN, sampled field paths; Ctrl+Space
#117 — Backups tab → tabbed surface: PITR wizard (preview → restore-as-new),   ✅
       WAL-archiving toggle + segments, backup settings editor
#113 — cluster-config distribution: GET/PUT /admin/cluster-config on serve     ✅
       nodes (validated, atomic, 409 on stale version) + Studio
       "Push to nodes" with per-node results
#116 — rebalance over HTTP: plan/execute/status endpoints + Studio             ✅
       plan-preview/execute/progress UI in the cluster view
```

Engine fix found while verifying #116:

```
ClusterRebalancer duplicated every doc moved over HTTP transports —
docs fetched over HTTP carry _id as a JSON string, GetId() returned
Empty, source-shard deletes silently failed. ResolveId now parses
string _ids; failed deletes surface as MoveFailures, never as moves.
(The dfdb rebalance CLI verb had the same bug.)                          ✅
```

Earlier (May–July, already on master): multi-DB epic phases 1–6c, blob store (#109), service settings API (#111), follower HTTP advertisement (#112), backups + WAL archiving + PITR (#87/#88), persistent attach catalog (#82), scoped keys (#101), rebalance CLI, router hot-swap (#134).

## Relational-parity review (2026-07-13)

Question asked: how far is DocumentForge from usable-as-relational (transactions, sort,
group, join) while keeping the schemaless JSON story? Answer: the engine is already
there; the gaps are surface, not engine.

Already have (verified in source):

```
SQL          — multi-JOIN (INNER/LEFT/RIGHT/CROSS, multi-predicate ON),
               GROUP BY + COUNT/SUM/AVG/MIN/MAX, ORDER BY, LIMIT/OFFSET,
               DISTINCT, scalar functions
Transactions — multi-doc/multi-collection, staged writes, read-your-writes,
               first-committer-wins, crash-atomic via WAL commit markers
               (#89–91); exposed as POST /tx/batch (flat routes only)
Schemas      — opt-in per collection: required fields, types, CHECK (#106);
               no-schema collections stay schemaless
```

Gaps, in priority order:

| Gap | Nature of the work | Status |
|---|---|---|
| `/db/{name}/tx/batch` + `/db/{name}/collections/{c}/schema` — tx + schemas unreachable on named DBs (blocks AeroBus, which talks to `aerotoys` via the scoped surface) | route plumbing, reuse flat handlers | **in progress** (worktree session, 2026-07-13) |
| Transactional multi-statement `POST /query` — a SQL script that runs inside one engine `Transaction`, all-or-nothing; gives SQL-flavoured transactions with no session state | executor plumbing onto existing `Transaction` | queued |
| SQL polish: multi-column ORDER BY (AST holds one `OrderByPath`), HAVING, subqueries, non-equi joins | parser + executor, incremental | queued |
| Restrict-only foreign keys (`REFERENCES`, no cascades): write-time existence check + delete-time reverse check, layered on schemas + unique indexes | write-path enforcement, not syntax | queued |
| Session-style `BEGIN`/`COMMIT`/`ROLLBACK` over HTTP (handles, leases, abandoned-tx sweep) | deferred unless the script form proves insufficient | deferred |

Client-side note: AeroBus's `DocumentStore` still pages client-side on a stale
"LIMIT has no OFFSET" assumption — a fix pushing LIMIT/OFFSET server-side is
in flight in the aerobus repo (2026-07-13).

## What's queued (priority order)

| Item | Description | Effort |
|---|---|---|
| **Scoped tx/schema routes** | `/db/{name}/tx/batch` + schema routes (see review above) — *in progress* | ~½ day |
| **Transactional multi-statement query** | SQL script in one `POST /query`, one `Transaction`, all-or-nothing | ~1–2 days |
| **#68** | Developer portal — docs site: getting-started, REST + CLI reference, LINQ & clustering guides, runnable examples | ~3–4 days |
| **#66 Phase 3b** | Lazy DB open + per-DB quotas + idle eviction (host 50+ DBs per process) | ~1 day |
| **#66 Phase 4** | Bearer auth scopes (`db:foo`, `db:*`, `*`) + per-request DB scoping | ~1–2 days |
| **SQL polish** | multi-column ORDER BY, HAVING, subqueries, non-equi joins | incremental |
| **Foreign keys (restrict-only)** | `REFERENCES` enforcement on insert/delete, no cascades | ~1–2 days |
| **#73** | `dfdb serve` intermittent native crash on macOS arm64 (Kestrel AcceptAsync) — needs a Mac | unknown |

Deferred (perf / debt, opportunistic):

- **BSON serializer rework** — the real insert-rate lever (allocation-bound; see `samples/DocumentForge.InsertBench` + `samples/DocumentForge.ScaleBench`).
- SqlBulkCopy-style turbo bulk-load.
- Benign build warnings (SYSLIB0057, a few xUnit analyzer nags).
- Rebalance shard transports assume one shared admin key (`shardApiKey`); per-shard keys would need the router config's per-endpoint `apiKey` shape in the engine format.

## To resume

```bash
git clone https://github.com/aerotoysio/documentforge
cd documentforge
git checkout master
dotnet build
dotnet test                      # engine suite: 564 green; Studio.Core: 85 green
```

Run the stack locally:

```bash
# Terminal A — host service (Studio connects here)
dotnet src/DocumentForge.Cli/bin/Release/net9.0/dfdb.dll serve --data-dir /tmp/dfdb_dev --port 5099 --api-key <key>

# DocumentForge Studio (Windows desktop app) — the management UI
dotnet run --project src/DocumentForge.Studio
#   then add an HTTP connection to http://localhost:5099 with the key
```

Studio server admin surface (right-click a server): Topology, API Keys, Backups (PITR/WAL/settings tabs), Service Settings, Services (swarm). Cluster menu → New/Open cluster: edit shards + policies, Check health, Push to nodes, Rebalance (plan → execute → progress).

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
│  {dataDir}/cluster.json — the node's stored cluster        │
│  config (#113); rebalance defaults its "old" side to it    │
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

Two config formats exist — don't mix them:
- **Engine format** (PascalCase, `Shards[].Endpoint`, `Collections` as a map): written by Studio's cluster editor, `dfdb cluster` verbs, `/admin/cluster-config`, and rebalance.
- **Router format** (camelCase, `shards[].leader`, `collections` as an array): consumed by `dfdb router` / POST /routers.

## On-disk format / upgrade notes

The per-database file format is `FileFormatVersion = 1` (magic `DFDB`); `DataFile.ValidateHeader` hard-rejects anything else. Multi-DB hosting is additive. Indexes self-heal (#64). Back up the file and do a clean flush before swapping binaries; downgrading (new file → older build) is the riskier direction and isn't guaranteed clean.
