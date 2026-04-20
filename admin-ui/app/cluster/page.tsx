'use client';

import { useState } from 'react';

type Shard = { name: string; endpoint: string };
type Collection = { name: string; strategy: 'hash' | 'replicated'; shardKeyPath?: string };

export default function ClusterPage() {
  const [shards, setShards] = useState<Shard[]>([
    { name: 'A', endpoint: 'http://localhost:5000' },
  ]);
  const [collections, setCollections] = useState<Collection[]>([]);
  const [newShardName, setNewShardName] = useState('');
  const [newShardEndpoint, setNewShardEndpoint] = useState('');
  const [newCollName, setNewCollName] = useState('');
  const [newCollStrategy, setNewCollStrategy] = useState<'hash' | 'replicated'>('hash');
  const [newCollKey, setNewCollKey] = useState('');

  const config = {
    version: 1,
    virtualNodesPerShard: 150,
    shards,
    collections: Object.fromEntries(
      collections.map((c) => [
        c.name,
        c.strategy === 'hash'
          ? { strategy: 'Hash', shardKeyPath: c.shardKeyPath }
          : { strategy: 'Replicated' },
      ])
    ),
  };

  function addShard() {
    if (!newShardName || !newShardEndpoint) return;
    setShards([...shards, { name: newShardName, endpoint: newShardEndpoint }]);
    setNewShardName(''); setNewShardEndpoint('');
  }

  function addCollection() {
    if (!newCollName) return;
    const c: Collection = newCollStrategy === 'hash'
      ? { name: newCollName, strategy: 'hash', shardKeyPath: newCollKey || '_id' }
      : { name: newCollName, strategy: 'replicated' };
    setCollections([...collections, c]);
    setNewCollName(''); setNewCollKey('');
  }

  return (
    <>
      <div className="eyebrow">Cluster</div>
      <h1 className="page-title">Topology builder</h1>

      <div className="card">
        <h3>How this works</h3>
        <p style={{ fontSize: 14, color: 'var(--gray-900)', lineHeight: 1.6 }}>
          Each shard runs a <code>DocumentForge.Api</code> process. Configure the shards here, then
          download the generated <code>cluster.json</code> and deploy it to every node.
          The router uses a consistent-hash ring (150 virtual nodes per shard) to route each document.
        </p>
      </div>

      <h2>Shards ({shards.length})</h2>
      <table className="table">
        <thead><tr><th>Name</th><th>Endpoint</th><th></th></tr></thead>
        <tbody>
          {shards.map((s, i) => (
            <tr key={s.name}>
              <td>{s.name}</td>
              <td>{s.endpoint}</td>
              <td>
                <button className="btn ghost" style={{ fontSize: 11, padding: '4px 10px' }}
                  onClick={() => setShards(shards.filter((_, j) => j !== i))}>
                  Remove
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <div className="toolbar" style={{ marginTop: 12 }}>
        <input type="text" placeholder="Shard name (e.g. dubai)" value={newShardName}
               onChange={(e) => setNewShardName(e.target.value)} style={{ width: 200 }} />
        <input type="text" placeholder="http://host:5000" value={newShardEndpoint}
               onChange={(e) => setNewShardEndpoint(e.target.value)} style={{ flex: 1 }} />
        <button className="btn" onClick={addShard}>Add shard</button>
      </div>

      <h2>Collections ({collections.length})</h2>
      <table className="table">
        <thead><tr><th>Name</th><th>Strategy</th><th>Shard key</th><th></th></tr></thead>
        <tbody>
          {collections.map((c, i) => (
            <tr key={c.name}>
              <td>{c.name}</td>
              <td><span className={`pill ${c.strategy === 'hash' ? 'green' : 'gray'}`}>{c.strategy}</span></td>
              <td>{c.shardKeyPath || '-'}</td>
              <td>
                <button className="btn ghost" style={{ fontSize: 11, padding: '4px 10px' }}
                  onClick={() => setCollections(collections.filter((_, j) => j !== i))}>
                  Remove
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      <div className="toolbar" style={{ marginTop: 12 }}>
        <input type="text" placeholder="Collection name" value={newCollName}
               onChange={(e) => setNewCollName(e.target.value)} style={{ width: 180 }} />
        <select value={newCollStrategy} onChange={(e) => setNewCollStrategy(e.target.value as any)}
                style={{ padding: '10px 12px', border: '1px solid var(--gray-200)' }}>
          <option value="hash">Hash (sharded)</option>
          <option value="replicated">Replicated (every shard)</option>
        </select>
        {newCollStrategy === 'hash' && (
          <input type="text" placeholder="Shard key path (e.g. pnr)" value={newCollKey}
                 onChange={(e) => setNewCollKey(e.target.value)} style={{ flex: 1 }} />
        )}
        <button className="btn" onClick={addCollection}>Add collection</button>
      </div>

      <h2>Generated cluster.json</h2>
      <pre className="code-block">{JSON.stringify(config, null, 2)}</pre>
      <div className="toolbar">
        <button className="btn" onClick={() => {
          const blob = new Blob([JSON.stringify(config, null, 2)], { type: 'application/json' });
          const url = URL.createObjectURL(blob);
          const a = document.createElement('a');
          a.href = url; a.download = 'cluster.json'; a.click();
          URL.revokeObjectURL(url);
        }}>Download cluster.json</button>
      </div>
    </>
  );
}
