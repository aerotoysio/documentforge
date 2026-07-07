# DocumentForge — In-flight status

Last updated: 2026-07-07 · branch `master`

This file is a pickup point — clone the repo on a different machine, `git pull`, read this, and you should know exactly where to continue.

---

## TL;DR

The July 5 Studio backlog (#113–#119, seven issues) is **fully shipped** — Studio (the WPF desktop app) now covers connect-to-follower from the topology graph, a service settings editor, a swarm panel for child services, query autocomplete, PITR + WAL-archiving + backup-settings wizards, one-click cluster-config distribution, and a full rebalance flow (plan preview → execute → live progress) backed by new engine HTTP endpoints.

The remaining big rocks are the **developer portal (#68)**, the production-readiness tail of **#66** (lazy open + per-DB quotas, auth scopes), and **#73** (macOS arm64 startup crash — needs a Mac to reproduce). Also outstanding: **14 Dependabot alerts** (7 high) on the default branch.

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

## What's queued (priority order)

| Item | Description | Effort |
|---|---|---|
| **#68** | Developer portal — docs site: getting-started, REST + CLI reference, LINQ & clustering guides, runnable examples | ~3–4 days |
| **Dependabot** | 14 alerts on the default branch (7 high, 5 moderate, 2 low) | ~half day |
| **#66 Phase 3b** | Lazy DB open + per-DB quotas + idle eviction (host 50+ DBs per process) | ~1 day |
| **#66 Phase 4** | Bearer auth scopes (`db:foo`, `db:*`, `*`) + per-request DB scoping | ~1–2 days |
| **#73** | `dfdb serve` intermittent native crash on macOS arm64 (Kestrel AcceptAsync) — needs a Mac | unknown |

Deferred (perf / debt, opportunistic):

- **BSON serializer rework** — the real insert-rate lever (allocation-bound; see `samples/DocumentForge.InsertBench` + `samples/DocumentForge.ScaleBench`).
- SqlBulkCopy-style turbo bulk-load.
- Benign build warnings (SYSLIB0057, a few xUnit analyzer nags).
- Rebalance shard transports assume one shared admin key (`shardApiKey`); per-shard keys would need the router config's per-endpoint `apiKey` shape in the engine format.
- Untracked `admin-ui/` folder in the repo root — legacy react-flow admin UI, superseded by the WPF Studio; decide keep-or-delete.

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
