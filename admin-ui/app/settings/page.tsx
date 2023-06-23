'use client';

import { useEffect, useState } from 'react';
import { API_URL, getApiKey, setApiKey } from '@/lib/api';

export default function SettingsPage() {
  const [key, setKey] = useState('');
  const [saved, setSaved] = useState(false);

  useEffect(() => { setKey(getApiKey() || ''); }, []);

  function save() {
    setApiKey(key.trim() || null);
    setSaved(true);
    setTimeout(() => setSaved(false), 2000);
  }

  return (
    <>
      <div className="eyebrow">Settings</div>
      <h1 className="page-title">Connection &amp; security</h1>

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
        <h3>API key (bearer token)</h3>
        <p style={{ fontSize: 13, color: 'var(--gray-900)', margin: '8px 0 16px' }}>
          If your nodes have an API key configured, paste it here. It&apos;s stored in your browser&apos;s
          localStorage and attached as <code>Authorization: Bearer &lt;key&gt;</code> on every request.
        </p>
        <input
          type="password"
          value={key}
          onChange={(e) => setKey(e.target.value)}
          placeholder="paste or leave empty for no-auth dev mode"
        />
        <div className="toolbar" style={{ marginTop: 12 }}>
          <button className="btn" onClick={save}>Save</button>
          {saved && <span style={{ color: 'var(--red)', fontSize: 13 }}>✓ Saved</span>}
          {getApiKey() && (
            <button className="btn ghost" onClick={() => { setKey(''); setApiKey(null); }}>
              Clear
            </button>
          )}
        </div>
      </div>

      <div className="card">
        <h3>About DocumentForge</h3>
        <p style={{ fontSize: 14, lineHeight: 1.6 }}>
          An embedded JSON document database for .NET with SQL-like queries, persistent B-tree
          indexes, WAL-based durability, logical replication, auto-failover, consistent-hash
          sharding, and online rebalance. Zero external dependencies.
        </p>
        <div className="setting-row"><span className="key">Version</span><span className="val">0.1.0</span></div>
        <div className="setting-row"><span className="key">License</span><span className="val">MIT</span></div>
        <div className="setting-row"><span className="key">Tests</span><span className="val">47 passing</span></div>
      </div>

      <div className="card">
        <h3>Useful commands</h3>
        <pre className="code-block">{`# Start a node with an API key
dotnet run --project samples/DocumentForge.Api -- \\
    --node-name shard-a --port 5001 \\
    --data-dir ./data/shard-a \\
    --api-key sk_prod_abc123

# Or as env var
DFDB_API_KEY=sk_prod_abc123 dotnet run --project samples/DocumentForge.Api

# Check health of an entire cluster
dfctl health cluster.json

# Launch local 3-shard cluster
./scripts/start-cluster.sh`}</pre>
      </div>
    </>
  );
}
