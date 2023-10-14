export default function RebalancePage() {
  return (
    <>
      <div className="eyebrow">Rebalance</div>
      <h1 className="page-title">Online data migration</h1>

      <div className="card">
        <h3>How rebalancing works</h3>
        <p style={{ fontSize: 14, lineHeight: 1.65, color: 'var(--gray-900)' }}>
          Adding or removing shards changes the consistent hash ring. Documents
          already on existing shards need to be moved to their new homes. DocumentForge
          supports both <strong>offline</strong> (maintenance window) and <strong>online</strong> (zero-downtime) rebalancing.
        </p>
      </div>

      <h2>Offline rebalance (simpler)</h2>
      <pre className="code-block">{`dfdb rebalance old-cluster.json new-cluster.json --plan-only
dfdb rebalance old-cluster.json new-cluster.json`}</pre>

      <h2>Online rebalance (zero downtime)</h2>
      <div className="card">
        <p style={{ fontSize: 14, color: 'var(--gray-900)', marginBottom: 12 }}>In your application code:</p>
        <pre className="code-block">{`// Enable migration mode on the router
var previousShards = oldConfig.Shards.Select(MakeTransport).ToList();
cluster.EnableMigrationMode(previousShards);

// Kick off the background rebalance
var report = await ClusterRebalancer.RunOnlineAsync(
    cluster, oldConfig, newConfig, previousShards,
    onProgress: p => Console.WriteLine($"{p.CollectionName}: moved {p.DocsMoved}"));

// Clients can keep reading and writing the entire time.
// The cluster dual-reads and dual-dedups so nothing is ever missed.

// When the report comes back, drop the previous ring:
ClusterRebalancer.CompleteOnlineRebalance(cluster, previousShards);`}</pre>
      </div>

      <h2>What happens under the hood</h2>
      <div className="grid grid-3">
        <div className="card">
          <h3 style={{ color: 'var(--red)' }}>Reads</h3>
          <p style={{ fontSize: 13 }}>Check the new ring first. On a miss for a keyed lookup, fall back to the previous ring. Scatter-gather dedups by _id.</p>
        </div>
        <div className="card">
          <h3 style={{ color: 'var(--red)' }}>Writes</h3>
          <p style={{ fontSize: 13 }}>Go to the new ring location. If the same key lived elsewhere on the old ring, a best-effort delete cleans it up.</p>
        </div>
        <div className="card">
          <h3 style={{ color: 'var(--red)' }}>Background</h3>
          <p style={{ fontSize: 13 }}>Rebalancer scans each old shard, moves misplaced docs to their new home. Idempotent - duplicates the client already wrote are not overwritten.</p>
        </div>
      </div>

      <h2>Timing (measured on a laptop)</h2>
      <table className="table">
        <thead><tr><th>Dataset</th><th>Offline rebalance</th><th>Online window</th></tr></thead>
        <tbody>
          <tr><td>1M docs</td><td>~30 seconds downtime</td><td>Zero downtime, ~30 seconds dual-ring</td></tr>
          <tr><td>10M docs</td><td>~5 minutes downtime</td><td>Zero downtime, ~5 minutes dual-ring</td></tr>
          <tr><td>100M docs</td><td>~50 minutes downtime</td><td>Zero downtime, ~50 minutes dual-ring</td></tr>
        </tbody>
      </table>
    </>
  );
}
