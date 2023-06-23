'use client';

import { useState } from 'react';

type Shard = {
  name: string;
  leader: string;
  followers: string[];
};
type Collection = {
  name: string;
  strategy: 'hash' | 'replicated';
  shardKeyPath?: string;
};

function emptyShard(name: string, basePort: number): Shard {
  return {
    name,
    leader: `http://localhost:${basePort}`,
    followers: [],
  };
}

export default function ClusterPage() {
  const [shards, setShards] = useState<Shard[]>([emptyShard('shard-a', 5001)]);
  const [collections, setCollections] = useState<Collection[]>([
    { name: 'orders', strategy: 'hash', shardKeyPath: 'pnr' },
  ]);
  const [apiKey, setApiKey] = useState('');
  const [replicationSecret, setReplicationSecret] = useState('');
  const [tlsOn, setTlsOn] = useState(false);

  function addShard() {
    const nextLetter = String.fromCharCode('a'.charCodeAt(0) + shards.length);
    const nextPort = 5001 + shards.length * 10;
    setShards([...shards, emptyShard(`shard-${nextLetter}`, nextPort)]);
  }

  function removeShard(i: number) { setShards(shards.filter((_, j) => j !== i)); }

  function addFollower(i: number) {
    const s = shards[i];
    const base = parseInt(s.leader.split(':').pop() || '5000', 10);
    const nextPort = base + 1 + s.followers.length;
    const newShards = [...shards];
    newShards[i] = { ...s, followers: [...s.followers, `http://localhost:${nextPort}`] };
    setShards(newShards);
  }

  function removeFollower(shardIdx: number, folIdx: number) {
    const newShards = [...shards];
    newShards[shardIdx] = {
      ...newShards[shardIdx],
      followers: newShards[shardIdx].followers.filter((_, j) => j !== folIdx),
    };
    setShards(newShards);
  }

  function updateShardName(i: number, name: string) {
    const newShards = [...shards]; newShards[i].name = name; setShards(newShards);
  }
  function updateShardLeader(i: number, url: string) {
    const newShards = [...shards]; newShards[i].leader = url; setShards(newShards);
  }
  function updateFollower(shardIdx: number, folIdx: number, url: string) {
    const newShards = [...shards];
    newShards[shardIdx].followers[folIdx] = url;
    setShards(newShards);
  }

  const config = {
    Version: 1,
    VirtualNodesPerShard: 150,
    Shards: shards.map((s) => ({
      Name: s.name,
      Leader: s.leader,
      Followers: s.followers,
    })),
    Collections: Object.fromEntries(
      collections.map((c) => [
        c.name,
        c.strategy === 'hash'
          ? { Strategy: 'Hash', ShardKeyPath: c.shardKeyPath }
          : { Strategy: 'Replicated' },
      ])
    ),
  };

  function downloadJson(filename: string, content: any) {
    const blob = new Blob([JSON.stringify(content, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url; a.download = filename; a.click();
    URL.revokeObjectURL(url);
  }

  function downloadAll() {
    downloadJson('cluster.json', config);
    // Per-node config files — one per leader + each follower
    shards.forEach((s) => {
      const leaderPort = parseInt(s.leader.split(':').pop() || '5000', 10);
      downloadJson(`node-${s.name}-leader.json`, {
        nodeName: `${s.name}-leader`,
        port: leaderPort,
        dataDir: `./data/${s.name}/leader`,
        security: (apiKey || replicationSecret) ? {
          apiKey: apiKey || undefined,
          replicationSecret: replicationSecret || undefined,
        } : undefined,
      });
      s.followers.forEach((f, i) => {
        const port = parseInt(f.split(':').pop() || '5000', 10);
        downloadJson(`node-${s.name}-follower-${i + 1}.json`, {
          nodeName: `${s.name}-follower-${i + 1}`,
          port,
          dataDir: `./data/${s.name}/follower-${i + 1}`,
          security: (apiKey || replicationSecret) ? {
            apiKey: apiKey || undefined,
            replicationSecret: replicationSecret || undefined,
          } : undefined,
        });
      });
    });
  }

  return (
    <>
      <div className="eyebrow">Cluster</div>
      <h1 className="page-title">Topology builder</h1>

      <div className="card">
        <h3>How it reads</h3>
        <p style={{ fontSize: 14, lineHeight: 1.65, color: 'var(--gray-900)' }}>
          Each <strong>shard</strong> is a mini-cluster: a <strong>leader</strong> that accepts writes, plus optional <strong>followers</strong>
          for high-availability and read scale. Documents are distributed across shards using a consistent hash on the shard-key field.
          Small reference collections can be marked <em>replicated</em> so every shard has a full copy (joins stay local).
        </p>
      </div>

      <h2>Shards</h2>

      <div className="grid grid-2" style={{ gap: 20 }}>
        {shards.map((s, i) => (
          <div key={i} className="card" style={{ borderLeft: '3px solid var(--red)' }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 12 }}>
              <input
                type="text"
                value={s.name}
                onChange={(e) => updateShardName(i, e.target.value)}
                style={{ fontSize: 17, fontWeight: 700, flex: 1, border: 'none', padding: 4, background: 'transparent' }}
              />
              <button className="btn ghost" style={{ fontSize: 11, padding: '4px 10px' }} onClick={() => removeShard(i)}>
                Remove shard
              </button>
            </div>

            <div style={{ marginBottom: 10 }}>
              <label style={{ display: 'block', fontFamily: 'var(--mono)', fontSize: 11, letterSpacing: '0.06em', color: 'var(--red)', marginBottom: 4 }}>
                LEADER
              </label>
              <input type="text" value={s.leader} onChange={(e) => updateShardLeader(i, e.target.value)} />
            </div>

            <div>
              <label style={{ display: 'block', fontFamily: 'var(--mono)', fontSize: 11, letterSpacing: '0.06em', color: 'var(--gray-500)', marginBottom: 4 }}>
                FOLLOWERS ({s.followers.length})
              </label>
              {s.followers.map((f, fi) => (
                <div key={fi} style={{ display: 'flex', gap: 6, marginBottom: 6 }}>
                  <input
                    type="text"
                    value={f}
                    onChange={(e) => updateFollower(i, fi, e.target.value)}
                    style={{ flex: 1 }}
                  />
                  <button className="btn ghost" style={{ fontSize: 11, padding: '4px 10px' }} onClick={() => removeFollower(i, fi)}>
                    ×
                  </button>
                </div>
              ))}
              <button className="btn ghost" style={{ fontSize: 12, padding: '6px 12px' }} onClick={() => addFollower(i)}>
                + Add follower
              </button>
            </div>
          </div>
        ))}
      </div>

      <div className="toolbar" style={{ marginTop: 16 }}>
        <button className="btn" onClick={addShard}>+ Add shard</button>
      </div>

      <h2>Collections</h2>
      <div className="card">
        <table className="table">
          <thead><tr><th>Name</th><th>Strategy</th><th>Shard key</th><th></th></tr></thead>
          <tbody>
            {collections.map((c, i) => (
              <tr key={i}>
                <td><input type="text" value={c.name}
                  onChange={(e) => { const n = [...collections]; n[i].name = e.target.value; setCollections(n); }}
                  style={{ background: 'transparent', border: 'none', padding: 0 }} /></td>
                <td>
                  <select value={c.strategy}
                    onChange={(e) => { const n = [...collections]; n[i].strategy = e.target.value as any; setCollections(n); }}
                    style={{ padding: 6, border: '1px solid var(--gray-200)', fontSize: 13, fontFamily: 'var(--mono)' }}>
                    <option value="hash">hash (sharded)</option>
                    <option value="replicated">replicated</option>
                  </select>
                </td>
                <td>
                  {c.strategy === 'hash' && (
                    <input type="text" value={c.shardKeyPath || ''}
                      onChange={(e) => { const n = [...collections]; n[i].shardKeyPath = e.target.value; setCollections(n); }}
                      placeholder="e.g. pnr"
                      style={{ background: 'transparent', border: 'none', padding: 0 }} />
                  )}
                </td>
                <td>
                  <button className="btn ghost" style={{ fontSize: 11, padding: '4px 10px' }}
                    onClick={() => setCollections(collections.filter((_, j) => j !== i))}>Remove</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        <div className="toolbar" style={{ marginTop: 12 }}>
          <button className="btn" onClick={() => setCollections([...collections, { name: '', strategy: 'hash', shardKeyPath: '' }])}>
            + Add collection
          </button>
        </div>
      </div>

      <h2>Security (optional, recommended in production)</h2>
      <div className="card">
        <div style={{ marginBottom: 12 }}>
          <label style={{ display: 'block', fontFamily: 'var(--mono)', fontSize: 11, letterSpacing: '0.06em', color: 'var(--gray-500)', marginBottom: 4 }}>
            API KEY (bearer token)
          </label>
          <input type="text" value={apiKey} onChange={(e) => setApiKey(e.target.value)}
            placeholder="e.g. sk_prod_abc123..." />
        </div>
        <div style={{ marginBottom: 12 }}>
          <label style={{ display: 'block', fontFamily: 'var(--mono)', fontSize: 11, letterSpacing: '0.06em', color: 'var(--gray-500)', marginBottom: 4 }}>
            REPLICATION SECRET (shared between leader and followers)
          </label>
          <input type="text" value={replicationSecret} onChange={(e) => setReplicationSecret(e.target.value)}
            placeholder="e.g. rp_xyz789..." />
        </div>
        <label style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: 13 }}>
          <input type="checkbox" checked={tlsOn} onChange={(e) => setTlsOn(e.target.checked)} />
          Enable TLS (you'll need to provide certPath + password at deploy time)
        </label>
      </div>

      <h2>Visual summary</h2>
      <div className="card" style={{ fontFamily: 'var(--mono)', fontSize: 13 }}>
        {shards.map((s, i) => (
          <div key={i} style={{ marginBottom: 16 }}>
            <div style={{ color: 'var(--red)', fontWeight: 700 }}>┌─ {s.name}</div>
            <div style={{ marginLeft: 16 }}>├─ leader     → {s.leader}</div>
            {s.followers.map((f, fi) => (
              <div key={fi} style={{ marginLeft: 16, color: 'var(--gray-500)' }}>
                {fi === s.followers.length - 1 ? '└─' : '├─'} follower {fi + 1} → {f}
              </div>
            ))}
            {s.followers.length === 0 && (
              <div style={{ marginLeft: 16, color: 'var(--gray-500)', fontStyle: 'italic' }}>
                └─ (no followers - single point of failure)
              </div>
            )}
          </div>
        ))}
      </div>

      <h2>Generated cluster.json</h2>
      <pre className="code-block" style={{ maxHeight: 400, overflow: 'auto' }}>{JSON.stringify(config, null, 2)}</pre>

      <div className="toolbar">
        <button className="btn" onClick={() => downloadJson('cluster.json', config)}>
          Download cluster.json
        </button>
        <button className="btn" onClick={downloadAll}>
          Download everything (cluster.json + all node configs)
        </button>
      </div>

      <h2>How to deploy</h2>
      <div className="card">
        <pre className="code-block">{`# 1. Copy cluster.json to every node
# 2. On each node, copy its matching node-*.json
# 3. Start each node:
dotnet run --project samples/DocumentForge.Api -- --config node-shard-a-leader.json
# ... one command per node, across your datacenters

# 4. Wire replication (followers connect to their leader's replication port).
#    This can be done in a small C# bootstrap that reads node.json.

# 5. Your application code:
using DocumentForge.Engine.Cluster;
var cfg = ClusterConfig.Load("cluster.json");
using var cluster = DocumentForgeCluster.FromConfig(cfg,
    desc => new HttpShardTransport(desc.Name, desc.LeaderEndpoint, apiKey: "sk_prod_abc123"));`}</pre>
      </div>
    </>
  );
}
