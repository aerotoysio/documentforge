# DocumentForge — In-flight status

Last updated: 2026-05-19 · branch `feat/66-multi-db-phase1-registry` · PR [#67](https://github.com/aerotoysio/documentforge/pull/67)

This file is a pickup point — clone the repo on a different machine, `git pull`, read this, and you should know exactly where to continue.

---

## TL;DR

Issue #66 (multi-database hosting) is feature-complete from the ground up to the cluster router. One service can host N attached DBs; Studio manages the whole lifecycle (create / drop / detach / configure replication / spawn sibling services / spawn cluster routers) visually. The cross-service topology view groups services by shard and supports drag-to-wire replication.

The remaining big rocks are: a visual cluster meta-node in the topology canvas (Phase 6c), production-readiness work (lazy open, per-DB quotas, auth scopes — Phase 3b + 4), and the developer portal (#68).

---

## What's done (current branch, all pushed)

```
Phase 1   — DatabaseRegistry (registry of N attached engines)               ✅
Phase 2   — /databases REST + Studio databases page                        ✅
Phase 2.5 — Scoped /db/{name}/replication/* routes                         ✅
Phase 2.6 — react-flow Topology page (within-service graph)                ✅
Phase 3a  — Scoped /db/{name}/query + Studio per-tab DB picker             ✅
Phase 5a  — Spawn child dfdb serve from Studio (ServiceManager)            ✅
Phase 5b  — Across-services topology view (cross-connection graph)         ✅
Phase 5c  — Shard frame grouping in topology                               ✅
Phase 5d  — Drag-to-wire replication + drag-to-reshard                     ✅
Phase 6   — dfdb router (stateless cluster gateway)                        ✅
Phase 6b  — Collections tab + Spawn router from Studio (with editor)       ✅
```

Independent fix on its own branch:
```
fix/64-index-catalog-self-heal — PR #65 (self-heal corrupt catalog)        ✅
```

## What's queued (in priority order)

| Phase | Description | Estimated effort |
|---|---|---|
| **6c** | Cluster meta-node in topology canvas + drag-to-claim-rings + visual config builder + export cluster.json | 1 day |
| **3b** | Lazy open + per-DB quotas + idle eviction (for hosting 50+ DBs per process) | 1 day |
| **4** | Bearer auth scopes (`db:foo`) + per-request DB scoping for prod multi-tenancy | 1-2 days |
| **#68** | Developer portal (Nextra + API reference + guides) | 3-4 days |

## To resume tomorrow

```bash
git clone https://github.com/aerotoysio/documentforge
cd documentforge
git checkout feat/66-multi-db-phase1-registry
dotnet build
dotnet test                                            # 280/281 (one known flaky LogicalReplication test)
cd admin-ui && npm install                             # Studio deps
```

To run the full stack locally:

```bash
# Terminal A — host service (Studio connects here)
dotnet src/DocumentForge.Cli/bin/Release/net9.0/dfdb.dll serve --data-dir /tmp/dfdb_dev --port 5099

# Terminal B — Studio
cd admin-ui
NEXT_PUBLIC_DFDB_URL=http://localhost:5099 npx next dev -p 3000

# Browser
http://localhost:3000/topology     # the swarm canvas
http://localhost:3000/connections  # add connections + spawn services/routers
http://localhost:3000/studio       # SQL editor with per-tab DB picker
```

To spin up a router via the new endpoint:

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

…or just hit "Spawn cluster router" on the Connections page with the "↺ Build starter config" button.

---

## Key files added today (Phase 5 + 6 surface)

```
src/DocumentForge.Engine/DatabaseRegistry.cs                  Registry of attached engines
src/DocumentForge.Cli/ServiceManager.cs                       Spawn child serve + router processes
src/DocumentForge.Cli/Commands/DatabaseEndpoints.cs           /databases CRUD + scoped /db/{name}/*
src/DocumentForge.Cli/Commands/ServiceEndpoints.cs            /services + /routers
src/DocumentForge.Cli/Commands/RouterCommand.cs               dfdb router CLI verb
src/DocumentForge.Cli/Commands/RouterEndpoints.cs             Router REST surface
src/DocumentForge.Cli/Router/ClusterConfig.cs                 cluster.json schema
src/DocumentForge.Cli/Router/RingClient.cs                    HTTP client per shard ring
src/DocumentForge.Cli/Router/Router.cs                        Routing brain (hash/replicated)

admin-ui/app/topology/page.tsx                                /topology with 4 tabs
admin-ui/app/topology/db-node.tsx                             Custom DBNode react-flow renderer
admin-ui/app/databases/page.tsx                               /databases list view
admin-ui/app/connections/page.tsx                             /connections + spawn service/router
admin-ui/lib/api.ts                                           Extended with multi-DB + router verbs
admin-ui/app/Sidebar.tsx                                      Active-DB indicator + nav cleanup

tests/DocumentForge.Tests/DatabaseRegistryTests.cs            28 tests
tests/DocumentForge.Tests/DatabaseEndpointsHttpTests.cs       15 tests
tests/DocumentForge.Tests/ServiceManagerTests.cs              6 tests
tests/DocumentForge.Tests/RouterTests.cs                      10 tests (unit)
tests/DocumentForge.Tests/RouterIntegrationTests.cs           3 tests (full lifecycle)
```

## Architecture cheat-sheet

```
┌─── Service (one dfdb serve process, one HTTP port) ──────┐
│  DatabaseRegistry holds N DocumentForgeDb instances      │
│                                                            │
│   default (DB)    alpha (DB)    beta (DB)    ...          │
│      │              │              │                       │
│      └──── each has own WAL, lock, page cache, indexes ──┘
│
│  ServiceManager spawns child dfdb processes:
│    - dfdb serve children (more attached-DB hosts)
│    - dfdb router children (cluster gateways)
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

Rings can be backed by:
- A specific attached DB on a multi-DB host (`baseUrl + database`)
- A separate service's default DB (`baseUrl` only)
- Same router config works for either — peel a ring from local-dev to prod by editing one field.

## Open questions for tomorrow

- **Phase 6c** scope: should the cluster meta-node SAVE state somewhere? Studio localStorage = single-user, multi-tab fine; backend persistence = needs a new endpoint. Probably localStorage for v1.
- **Phase 6c** UX: drag a cluster onto canvas, draw lines to shard frames? Or right-click → "Assign to cluster X"? Drag is more delightful but more code.
- **Phase 4** auth shape: `db:foo` for one DB, `db:*` for all, plus the existing `*` admin? Standard JWT-style scopes or a custom claim format?
