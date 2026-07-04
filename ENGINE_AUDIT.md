# DocumentForge Engine Audit — 2026-07

A critical engineering review of the DocumentForge engine against its intended use:
a **high-performance, transactional, shardable, redundant** database for airline
reservation systems that is **also easy to deploy for beginners**.

Findings were produced by a code-level audit (reading the implementation, not the
README/comment claims) across three domains — durability/transactions,
replication/failover/sharding, and query/concurrency/security/operability. The
highest-consequence claims were re-verified by hand against source before filing.

> **Verdict:** the building blocks are well-made (SQL surface, indexing, the
> logical-replication wire protocol, the 2PC single-shard machinery, checksums,
> the fsync primitive itself). What's missing is the layer that ties them into
> **durability and consensus guarantees** — and today that layer is largely absent
> or unwired. The engine is a strong learning-grade / single-node-small-scale
> document DB; it is **not yet airline-grade** for bookings without the Critical
> items below.

## What "verified" means here

- **Verified in code** — I read the exact path and confirmed the behaviour (e.g.
  `Insert` returns with no fsync; `WritePageChange` has zero callers).
- **Reported by audit; consistent with code** — surfaced by the audit with
  file:line evidence and consistent with what I read, but not independently
  re-run end to end. These deserve a reproduction test before remediation (the
  split-brain and in-doubt-2PC items especially).

## Correction log (things the raw audit over- or under-stated)

- API keys at rest are **fine** — they're 32-byte random tokens under SHA-256;
  unsalted is not a rainbow-table risk at that entropy.
- Array/multikey indexing **works** — `flights[*].flightNumber` indexes each
  element, so multi-leg PNRs are indexable (composite multikey is not supported).
- Complex `SELECT`s take the **read** lock, not the write lock. The real
  concurrency limit is the single process-global reader/writer lock.

## Findings → GitHub issues

Severity key: 🔴 Critical (data loss / corruption / correctness under normal ops) ·
🟠 High · 🟡 Medium.

### Durability & transactions
| Sev | Finding | Issue |
|-----|---------|-------|
| 🔴 | Committed writes aren't durable until an explicit flush (no fsync-on-commit) | [#89](https://github.com/aerotoysio/documentforge/issues/89) |
| 🔴 | The "WAL" is dead code — page changes never logged, WAL never replayed | [#90](https://github.com/aerotoysio/documentforge/issues/90) |
| 🔴 | Multi-document transactions are not crash-atomic (in-memory rollback only) | [#91](https://github.com/aerotoysio/documentforge/issues/91) |
| 🟠 | Torn-page writes detected but not repaired; `ReadPage` returns corrupt buffers | [#92](https://github.com/aerotoysio/documentforge/issues/92) |
| 🟠 | Collection catalog is a single page that silently overflows past 8 KB | [#93](https://github.com/aerotoysio/documentforge/issues/93) |
| 🟡 | Free-page list never persisted; data file grows monotonically across restarts | [#94](https://github.com/aerotoysio/documentforge/issues/94) |

### Replication, failover & sharding
| Sev | Finding | Issue |
|-----|---------|-------|
| 🔴 | Async fire-and-forget replication → unbounded RPO / lost committed bookings | [#95](https://github.com/aerotoysio/documentforge/issues/95) |
| 🔴 | Auto-failover has no fencing/epoch → split-brain, two leaders accept writes | [#96](https://github.com/aerotoysio/documentforge/issues/96) |
| 🔴 | In-doubt 2PC txns never auto-recovered (`Recover()` unwired) + blind timeout rollback | [#97](https://github.com/aerotoysio/documentforge/issues/97) |
| 🟠 | 2PC coordinator + its decision log are a single point of failure | [#98](https://github.com/aerotoysio/documentforge/issues/98) |
| 🟠 | Cross-shard `AVG` wrong; cross-shard `JOIN` unsupported; `OR`/`IN` silently scatter | [#99](https://github.com/aerotoysio/documentforge/issues/99) |
| 🟡 | CLI router hash (FNV-1a) ≠ engine consistent-hash ring → misroute | [#100](https://github.com/aerotoysio/documentforge/issues/100) |

### Security, query & airline fitness
| Sev | Finding | Issue |
|-----|---------|-------|
| 🔴 | Server ships open by default — no auth = anonymous admin | [#101](https://github.com/aerotoysio/documentforge/issues/101) |
| 🟠 | SQL injection via unvalidated collection name in `GET /collections/{name}` | [#102](https://github.com/aerotoysio/documentforge/issues/102) |
| 🟠 | No atomic conditional-update/CAS; SQL `UPDATE` is ETag-blind (double-booking) | [#103](https://github.com/aerotoysio/documentforge/issues/103) |
| 🟠 | No TTL/expiry index — seat holds can't expire safely | [#104](https://github.com/aerotoysio/documentforge/issues/104) |
| 🟡 | `_system` DB (API-key hashes) reachable via the data plane with `db:*` scope | [#105](https://github.com/aerotoysio/documentforge/issues/105) |
| 🟡 | No schema / type / required-field / CHECK constraints beyond unique indexes | [#106](https://github.com/aerotoysio/documentforge/issues/106) |

## Recommended remediation order

The Critical durability and consensus items make "we won't lose or double-sell a
booking" a true statement; everything else builds on them.

1. **Durable commit path** — a real fsync-on-commit redo WAL with replay
   (#90 then #89), plus crash-atomic local transactions (#91). Group-commit to
   keep throughput.
2. **Overbooking safety** — an engine-side atomic conditional-update/decrement
   primitive (#103) and a TTL index for holds (#104).
3. **Consensus** — leader epochs + fencing + semi-sync replication (#96, #95);
   wire up `cluster.Recover()` and fix the blind 2PC timeout (#97); replicate the
   coordinator decision log (#98).
4. **Secure defaults** — deny-by-default auth (#101), block `_system` on the data
   plane (#105), validate identifiers (#102).
5. **Concurrency granularity** — per-collection or MVCC locking, once durability
   is solid.
6. **Correctness cleanups** — cross-shard `AVG`/reject unsupported shapes (#99),
   persist the free-page list (#94), spill the catalog past one page (#93),
   torn-page protection (#92), unify the router/engine hash (#100), optional
   schema validation (#106).

## Suggested next step

Before acting on the Criticals, land **demonstrator red tests** that make the two
worst gaps concrete and undeniable:

- Insert a booking, kill the process before a flush, reopen → show it's gone (#89/#90).
- Two clients decrement the last seat via the SQL `UPDATE` path → show both succeed (#103).
- Drive `AVG` across two shards → show the wrong number (#99).

These convert the audit into failing tests and make the fix order self-evident.
