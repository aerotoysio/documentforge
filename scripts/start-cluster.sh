#!/usr/bin/env bash
# Launches a 3-node DocumentForge cluster locally.
# Each node: own port, own data directory, own log file.
#
# Usage (from repo root):
#     ./scripts/start-cluster.sh
#
# Stop with:
#     ./scripts/stop-cluster.sh

set -e
ROOT="$(pwd)"
CLUSTER="$ROOT/scripts/sample-cluster"
mkdir -p "$CLUSTER/logs"

echo ""
echo "=== Starting DocumentForge local cluster ==="
echo "  Shard A:  http://localhost:5001   data: $CLUSTER/data/shard-a"
echo "  Shard B:  http://localhost:5002   data: $CLUSTER/data/shard-b"
echo "  Shard C:  http://localhost:5003   data: $CLUSTER/data/shard-c"
echo ""

for node in a b c; do
  dotnet run --project "$ROOT/samples/DocumentForge.Api" --configuration Release -- --config "$CLUSTER/node-$node.json" \
    >"$CLUSTER/logs/shard-$node.log" 2>&1 &
  echo "  started shard-$node (PID $!, log: $CLUSTER/logs/shard-$node.log)"
done

echo ""
echo "Wait ~5 seconds for nodes to warm up, then check health:"
echo "  dotnet run --project samples/DocumentForge.Ctl -- health $CLUSTER/cluster.json"
echo ""
echo "Or point the admin UI at shard A:"
echo "  cd admin-ui"
echo "  NEXT_PUBLIC_DFDB_URL=http://localhost:5001 npm run dev"
echo ""
