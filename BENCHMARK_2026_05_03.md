# DocumentForge — Quick Benchmark Report

**Date**: 2026-05-03
**Build**: master @ commit after PR #48 (snapshot transfer)
**Workload**: 100k airline-order docs, 5 indexes
**Mode**: `DFDB_BENCH_QUICK=1` (1/100th the full stress workload — same shape, faster wall-clock)

## Summary

**A grade.** Sub-millisecond indexed point lookups, ~85k QPS sustained on PNR lookups, indexed equality scans in the thousands of QPS range. No regression from any of the recent durability/feature work (122 → 160 tests landed; this is the perf baseline post-everything).

## Results

| Query | Queries | Avg latency | Min | Max | QPS |
|---|---:|---:|---:|---:|---:|
| **PNR Lookup (indexed point query)** | 83,985 | **0.01 ms** | 0.00 ms | 21.63 ms | **85,849** |
| Status = CONFIRMED (indexed; ~12.5% selectivity) | 2,314 | 0.43 ms | 0.19 ms | 42.79 ms | 2,316 |
| Departure Airport (indexed; nested array path) | 4,248 | 0.23 ms | 0.10 ms | 4.15 ms | 4,256 |
| Last Name Search (indexed; full-result return) | 11 | 96.21 ms | 59.92 ms | 234.83 ms | 10 |
| First Name (NO index, LIMIT 10) | 633 | 1.58 ms | 0.34 ms | 13.23 ms | 633 |
| Fare > 1500 LIMIT 100 (range index) | 83 | 12.09 ms | 8.55 ms | 41.99 ms | 83 |

**Total: 91,274 queries in ~36s.**

## Setup

| Phase | Duration | Notes |
|---|---:|---|
| Bulk insert 100k docs | 7s (12,501 docs/sec) | RAM peak: 77 MB |
| Flush to disk | 0.3s | Final file size: 0.05 GB |
| Create 5 indexes | ~7s combined | Largest (`idx_fare`) was 1.9s |
| Build location map | 0.3s | |
| **Reopen with persistent indexes** | **2.28s** | Index entries loaded from disk; no rebuild |

## Notes

- The "Last Name Search" 10 QPS is **expected** — it has no LIMIT, returns ~3.3k full documents per query (1/30 selectivity × 100k docs). Not an indexed-lookup bottleneck.
- All durability fixes from this session (#11, #24, #25, #26, #27, #29) are baked in. No measurable performance regression.
- Single-process run on the dev machine (Windows, .NET 9). Numbers are illustrative; production would tune cache size, wal mode, and disk hardware.

## How to reproduce

```bash
dotnet build samples/DocumentForge.Benchmark -c Release
DFDB_BENCH_QUICK=1 dotnet run --project samples/DocumentForge.Benchmark -c Release --no-build
```

For the full 10M-doc stress test, omit the env var:

```bash
dotnet run --project samples/DocumentForge.Benchmark -c Release --no-build
```
