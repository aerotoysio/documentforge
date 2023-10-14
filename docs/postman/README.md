# DocumentForge Postman collection

Drop `DocumentForge.postman_collection.json` into Postman (File → Import) to get
two folders:

- **Application** — CRUD + query examples as an app would use them (seed, insert,
  find-by-id, bulk insert, SQL queries, create index, delete).
- **Database Operations** — admin (flush, checkpoint, compact, drop), replication
  control (status, start leader/follower, promote, read-only toggle, auto-failover
  on/off), and a sharding-via-CLI reference note.

## Variables

Set these on the collection once after importing:

| Variable         | Default                 | Meaning                                                                 |
|------------------|-------------------------|-------------------------------------------------------------------------|
| `baseUrl`        | `http://localhost:5000` | URL of the `dfdb serve` node.                                           |
| `apiKey`         | *(empty)*               | Bearer token; only needed if the node was started with `--api-key`.     |
| `pnr`            | `DEMO01`                | Shared across examples so Insert → Query → Delete chains work.          |
| `orderId`        | *(auto)*                | Filled by the Insert-order request's test script.                       |
| `replicationPort`| `5500`                  | Default replication port used in the replication folder.                |
| `leaderHost`     | `localhost`             |                                                                         |
| `leaderPort`     | `5500`                  |                                                                         |

## Typical first-run flow

1. **Application → Health check** — confirms the node is up.
2. **Application → Seed sample airline data** — populates `orders` and `flights`.
3. **Application → Insert an order** — saves the returned id to `{{orderId}}`.
4. **Application → Find document by id** — fetches it back.
5. **Application → Query: point lookup by PNR** — should be sub-ms with `plan=INDEX_SCAN`.
6. **Database Operations → Admin → Flush** — sanity check the write path.
7. **Database Operations → Replication → Status** — see the current role.

## Pairing with a live cluster

Start a local 3-shard cluster (`./scripts/start-cluster.sh`), then import this
collection three times with different `baseUrl` values (`:5001`, `:5002`, `:5003`)
— or just edit the variable per request. Every endpoint in this collection is
per-node: stats, admin ops, and replication status are all scoped to the node
you hit.
