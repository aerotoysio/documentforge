# DocumentForge Admin UI

A separate Next.js 15 app for managing DocumentForge clusters — kept out of the
main C# codebase so the database stays a clean library.

## Run it

```bash
cd admin-ui
npm install
npm run dev
# → http://localhost:3000
```

You'll also need the DocumentForge REST API running somewhere:

```bash
# From the repo root
cd ..
dotnet run --project samples/DocumentForge.Api
# → http://localhost:5000
```

The UI defaults to `http://localhost:5000`. Override via:

```bash
NEXT_PUBLIC_DFDB_URL=https://dfdb.internal:5500 npm run dev
```

## Pages

| Page | What it does |
|---|---|
| **Dashboard** | Live stats (size, pages, docs, indexes). Auto-refreshes every 3 s. |
| **Query console** | Run any SQL, see the plan + execution time. Has examples. |
| **Collections** | Browse documents, view/create indexes per collection. |
| **Cluster** | Build a multi-shard topology. Download as `cluster.json`. |
| **Rebalance** | Explains online vs offline migration with code snippets. |
| **Settings** | API endpoint, version, useful CLI commands. |

## Design

Swiss international style (red/gray/white, strong typography, asymmetric grid) —
matches the main documentation site.

## Stack

- Next.js 15 (App Router)
- React 19
- TypeScript
- Zero UI libraries — raw HTML/CSS for zero bloat
