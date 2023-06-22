# Scripts

Helpers for running DocumentForge locally across multiple nodes.

## Quick start: launch a 3-shard cluster on localhost

```bash
# Windows / PowerShell
.\scripts\start-cluster.ps1

# macOS / Linux
./scripts/start-cluster.sh
```

This launches three `DocumentForge.Api` processes, each with its own port and data directory:

| Node | URL | Data folder |
|---|---|---|
| shard-a | http://localhost:5001 | scripts/sample-cluster/data/shard-a |
| shard-b | http://localhost:5002 | scripts/sample-cluster/data/shard-b |
| shard-c | http://localhost:5003 | scripts/sample-cluster/data/shard-c |

All three nodes share `scripts/sample-cluster/cluster.json` which describes
the full topology. Check that every node is up:

```bash
dotnet run --project samples/DocumentForge.Ctl -- health scripts/sample-cluster/cluster.json
```

You should see three green dots.

## Point the admin UI at a node

```bash
cd admin-ui

# Windows
$env:NEXT_PUBLIC_DFDB_URL = "http://localhost:5001"
npm run dev

# macOS / Linux
NEXT_PUBLIC_DFDB_URL=http://localhost:5001 npm run dev
```

Then browse to http://localhost:3000

## Write through the cluster

Anywhere in your C# code (or a test):

```csharp
using DocumentForge.Engine.Cluster;

var config = ClusterConfig.Load("scripts/sample-cluster/cluster.json");
using var cluster = DocumentForgeCluster.FromConfig(config,
    desc => new HttpShardTransport(desc.Name, desc.Endpoint));

// Writes now route via consistent hashing across the three shards
cluster.Insert("orders", """{"pnr": "ABC123", "passenger": {"lastName": "Smith"}}""");

// Reads with the shard key route to exactly one node
var r = cluster.Execute("SELECT * FROM orders WHERE pnr = 'ABC123'");
```

## Stop it all

```bash
# Windows
.\scripts\stop-cluster.ps1

# macOS / Linux
./scripts/stop-cluster.sh
```

## Node config file format

Each node reads a small JSON file on startup:

```json
{
  "nodeName": "shard-a",
  "port": 5001,
  "dataDir": "./scripts/sample-cluster/data/shard-a"
}
```

Start a node manually:

```bash
dotnet run --project samples/DocumentForge.Api -- --config scripts/sample-cluster/node-a.json
```

Or with explicit flags instead of a file:

```bash
dotnet run --project samples/DocumentForge.Api -- \
    --node-name shard-a \
    --port 5001 \
    --data-dir ./data/shard-a
```

Or with env vars:

```bash
DFDB_NODE_NAME=shard-a DFDB_PORT=5001 DFDB_DATA_DIR=./data/shard-a \
    dotnet run --project samples/DocumentForge.Api
```

## Setting up replication between local nodes

The nodes above are bare — no replication between them yet. To configure
shard-a as a leader and spin up shard-a-replica as a follower:

```csharp
// In a small bootstrap program or through the REST API (future):

using var leader   = DocumentForgeDb.Open("scripts/sample-cluster/data/shard-a/data.dfdb");
leader.StartLogicalReplicationServer(port: 6001);

using var replica  = DocumentForgeDb.Create("scripts/sample-cluster/data/shard-a-replica/data.dfdb");
replica.StartLogicalReplicationFollower("localhost", 6001);
replica.EnableAutoFailover(newLeaderPort: 6001, silenceTimeout: TimeSpan.FromSeconds(10));
```

(Wiring replication into the API startup as a config option is on the roadmap.)
