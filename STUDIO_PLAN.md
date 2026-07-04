# DocumentForge Studio — Implementation Plan

**Status: APPROVED 2026-07-04 — Phase 1 in progress**

An installable Windows desktop application (WPF, .NET 9) for managing DocumentForge —
the SSMS of DF. Replaces the Node-based admin-ui as the primary management tool.

## Goals

- SSMS-style layout: Object Explorer tree on the left, tabbed working area, menus/toolbar.
- Connect three ways: **direct to a .dfdb file** (embedded engine), **local dfdb service**, or **remote endpoint** (HTTP + Bearer key, TLS-aware).
- Full feature parity with what DF can do: databases, collections, documents (CRUD), SQL queries, indexes, stats/health, backups/restore/PITR, WAL archiving, API keys, replication, cluster/sharding, rebalance, service orchestration.
- Default data directory: `C:\data\documentForge`.
- All settings/connections/keys persisted on the box (never re-enter), secrets DPAPI-encrypted, everything exportable/importable as plain-text JSON.
- Distributed via Inno Setup installer, self-contained (no .NET runtime required on target).

## Decisions already made (with Andrew, 2026-07-04)

| Decision | Choice |
|---|---|
| UI framework | WPF (.NET 9, matches engine target) |
| Location | This repo — `src/DocumentForge.Studio` added to `DocumentForge.sln` |
| Installer | Inno Setup, self-contained publish |
| Settings/secrets | JSON in `%AppData%\DocumentForge Studio\`; secrets DPAPI per-user; export = plain JSON with warning |

## Architecture

```
src/
  DocumentForge.Studio/          WPF app (views, view models, dialogs)
  DocumentForge.Studio.Core/     UI-free logic: connection abstraction, HTTP client,
                                 settings store, import/export — unit-testable
```

### The connection abstraction (the heart of the app)

One interface, two implementations, so every screen works against either transport:

- `IDfConnection` — OpenDatabase(s), ListCollections, Execute(sql), Insert/Replace/Delete (ETag-aware),
  Indexes, Stats, Health, capabilities discovery.
- **`DirectFileConnection`** — project-references `DocumentForge.Engine`, opens the `.dfdb` in-process.
  Honors the single-writer OS lock: if a service holds the file, Studio surfaces the lock-holder info
  (PID/host from the `.lock` JSON) and offers "connect via service instead". Full read/write when lock acquired.
- **`HttpConnection`** — typed client over the full REST surface (`/databases`, `/query` incl. NDJSON
  streaming, `/collections/*`, `/admin/*`, `/replication/*`, keys, PITR…). Bearer auth, `X-Database`
  targeting, 412 ETag-conflict handling.

**Capability flags**: direct-file mode has no server-side features (API keys, replication control,
service spawn, server backup registry). The tree and menus grey out / hide what the connection can't do.

### Settings & connection store

- `%AppData%\DocumentForge Studio\settings.json` — app prefs, default data dir (`C:\data\documentForge`), window layout.
- `%AppData%\DocumentForge Studio\connections.json` — named connections: `{ name, kind: file|http, path|url, databaseName?, apiKeyRef?, color? }`.
- Secrets (API keys) DPAPI-encrypted in `secrets.dat`, referenced by id from connections.json.
- **Export/Import**: File ▸ Export Settings… produces one plain-text JSON bundle (secrets decrypted,
  with an explicit warning dialog); Import merges or replaces. Also serves as the backup format.

### Key packages (Studio only — engine stays zero-dependency)

- `CommunityToolkit.Mvvm` — MVVM plumbing
- `AvalonEdit` — SQL editor with syntax highlighting
- `Dirkster.AvalonDock` — SSMS-style docking/tabbed layout

## Phases

### Phase 1 — Shell, connections, Object Explorer
The skeleton everything else hangs off.
- Solution wiring: two new projects, added to sln; app icon/branding "DocumentForge Studio".
- Main window: menu bar (File/View/Tools/Help), toolbar, AvalonDock layout (left tree + tab area), status bar (connection, version, health dot).
- Connect dialog: file picker (defaults to `C:\data\documentForge`) or URL + API key; test-connection button; save as named connection.
- Connection store + DPAPI secrets + export/import.
- `IDfConnection` + both implementations (core operations: list DBs/collections/indexes, execute SQL, stats, health).
- Object Explorer: Server/File ▸ Databases ▸ Collections ▸ Indexes, with refresh, context menus (stubs where later phases fill in), multi-connection (several servers in one tree, like SSMS).
- Create database (direct: `Create()` file in data dir; HTTP: `POST /databases`), drop/detach.

**Exit criteria:** connect to a file and to a running `dfdb serve`, browse tree, create a DB, settings survive restart, export/import round-trips.

### Phase 2 — Query workbench
- Query tab per connection+database (Ctrl+N "New Query" like SSMS): AvalonEdit with DF SQL highlighting (keywords, scalar functions `NEWID/GETDATE/LOWER/UPPER/LEN/SUBSTRING`).
- Execute (F5), cancel, execution time + plan display (`COLLECTION_SCAN`, `INDEX_SCAN`, …).
- Results: virtualized grid (flattened columns) **and** JSON document view; NDJSON streaming over HTTP for large results; LIMIT-guard prompt for unbounded SELECTs.
- Affected-count reporting for INSERT/UPDATE/DELETE; error panel with DF error detail (400/409/412 bodies).
- Query history (persisted per connection) + saved snippets.

### Phase 3 — Documents & indexes (the CRUD experience)
- Collection browser tab: paged document list, filter bar (field = value builder → SQL), open document.
- Document editor: JSON editor with validation; save uses ETag If-Match (HTTP) / ReplaceIfEtag (direct); 412 conflict dialog (theirs/mine diff-lite).
- Insert document (template from a sampled doc), delete (single + by-field), bulk import from `.json`/`.ndjson` file (atomic toggle), bulk export of a collection/query result.
- **Editable results grid** (Andrew's request, 2026-07-04): edit a cell in the query/browse grid → writes back via ETag-safe `ReplaceIfEtag`/`PUT If-Match`; only editable when the result carries `_id` (single-collection SELECT). Non-editable (joins/aggregates) stay read-only with a hint. Doubles as the inline-edit path for the collection browser.
- Index manager: list with entry counts, create (path, composite, unique), drop, rebuild one/all.
- Collection ops: create (implicit via first insert — surfaced honestly), drop, compact (with bytes-reclaimed result).

### Phase 4 — Server administration
- Database dashboard: stats (file size, pages, cache, per-collection doc/index counts), health panel wired to `GET /databases/{name}/health` incl. recommendation states (`rebuild-catalog`, `recovery-pending`, `engine-degraded`).
- Multi-DB management: attach/detach/drop, set-default, discover unattached files, **catalog rebuild wizard** (preview → confirm → rebuild, showing recovered chains).
- Backups: list/take/delete/restore-as-new; backup config (dir, retention, cron); **PITR wizard**: pick base backup → target time → preview (feasibility, gaps, bytes) → restore.
- WAL archiving: enable/disable per DB, status + segment list.
- API key manager: list/create (scopes picker `db:name`, `admin`)/revoke; created key shown once with copy button.
- Maintenance actions: flush/checkpoint, rebuild indexes, seed sample data (dev convenience).

### Phase 5 — Replication & cluster
- Replication panel per DB: role, seq/lag, followers; actions: start leader (port), start follower (leader host/port), promote (with force confirm), read-only/read-write toggle, auto-failover enable/disable.
- Topology view: visual leader→followers graph built from `/replication/status` across saved connections.
- Cluster config editor: open/create `cluster.json`, add shards, add collections (hash + shard key / replicated), validate; health check across all shards (dfdb `health` equivalent over HTTP).
- Rebalance: plan preview (old vs new config, move counts) → execute → progress.
- Service orchestration (swarm): spawn/list/kill child nodes via `/services/*` — dev/test convenience.

### Phase 6 — Packaging & polish
- `dotnet publish` self-contained win-x64; Inno Setup script (`scripts/build-studio-installer.ps1` + `.iss`): Start Menu shortcut, `.dfdb` file association → opens Studio direct-file connect, per-user install, upgrade-in-place.
- First-run experience: create `C:\data\documentForge` if missing, offer a default local connection.
- Version bump + STATUS.md entry; smoke-test checklist against a real served node.

Each phase lands as its own PR against `master`, keeping the sln green throughout.

## Resolved questions (Andrew, 2026-07-04)

1. **Name**: DocumentForge Studio (`DocumentForgeStudio.exe`).
2. **Bundle the service**: yes — installer ships `dfdb.exe` alongside Studio, and Studio can start/stop a local service.
3. **admin-ui**: keep for now; revisit after Phase 4 parity.
4. **Direct-file mode**: full read/write. Embedded connect is for local app testing; anything at scale uses the service.
5. **Dependencies**: approved (all free/OSS: MIT/MS-PL).
6. **Rebalance**: extend the DF service with HTTP rebalance endpoints (plan preview + execute) — DF services must be fully capable. This is a service-side work item scheduled just before Phase 5; Studio never shells out to the CLI.
