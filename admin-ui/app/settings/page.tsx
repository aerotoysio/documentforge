import { API_URL } from '@/lib/api';

export default function SettingsPage() {
  return (
    <>
      <div className="eyebrow">Settings</div>
      <h1 className="page-title">Connection &amp; info</h1>

      <div className="card">
        <h3>API connection</h3>
        <div className="setting-row">
          <span className="key">URL</span><span className="val">{API_URL}</span>
        </div>
        <div className="setting-row">
          <span className="key">Override via env</span><span className="val">NEXT_PUBLIC_DFDB_URL</span>
        </div>
      </div>

      <div className="card">
        <h3>About DocumentForge</h3>
        <p style={{ fontSize: 14, lineHeight: 1.6 }}>
          DocumentForge is an embedded JSON document database for .NET with SQL-like
          queries, persistent B-tree indexes, WAL-based durability, logical replication,
          auto-failover, and consistent-hash sharding. Zero external dependencies.
        </p>
        <div className="setting-row"><span className="key">Version</span><span className="val">0.1.0</span></div>
        <div className="setting-row"><span className="key">License</span><span className="val">MIT</span></div>
        <div className="setting-row"><span className="key">Docs</span><span className="val"><a href="/docs/index.html">Open documentation</a></span></div>
      </div>

      <div className="card">
        <h3>Useful commands</h3>
        <pre className="code-block">{`# Start the API backing this UI
dotnet run --project samples/DocumentForge.Api

# Inspect a database file
dfctl inspect airline.dfdb

# Run a quick query
dfctl query airline.dfdb "SELECT * FROM orders LIMIT 5"

# Plan a rebalance
dfctl rebalance old-cluster.json new-cluster.json --plan-only`}</pre>
      </div>
    </>
  );
}
