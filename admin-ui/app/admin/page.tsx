'use client';

import { useEffect, useState } from 'react';
import { useConnections } from '@/lib/connections-context';
import {
  getStats, getReplicationStatus, listDatabases,
  flushDb, checkpointDb, compactCollection, rebuildIndexes, rebuildIndex,
  dropCollection,
  setReadOnly, promoteToLeader, enableAutoFailover, disableAutoFailover,
  startLeader, startFollower,
} from '@/lib/api';

export default function AdminPage() {
  const { connections, active, setActive } = useConnections();
  const conn = active;
  const [stats, setStats] = useState<any>(null);
  const [repl, setRepl] = useState<any>(null);
  const [dbs, setDbs] = useState<any[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [log, setLog] = useState<{ ts: string; ok: boolean; line: string }[]>([]);

  const refresh = async () => {
    setError(null);
    try {
      const [s, r, d] = await Promise.all([
        getStats().catch(() => null),
        getReplicationStatus().catch(() => null),
        listDatabases().catch(() => null),
      ]);
      setStats(s);
      setRepl(r);
      setDbs(d?.databases ?? []);
    } catch (e: any) { setError(e.message || String(e)); }
  };

  useEffect(() => { refresh(); }, [conn?.id]);

  function pushLog(ok: boolean, line: string) {
    setLog(l => [{ ts: new Date().toLocaleTimeString(), ok, line }, ...l].slice(0, 50));
  }

  async function run<T>(label: string, fn: () => Promise<T>) {
    setBusy(true);
    try {
      const r = await fn();
      const summary = (r as any)?.timeMs != null ? `${label} (${(r as any).timeMs.toFixed(1)}ms)` : label;
      pushLog(true, summary);
      await refresh();
    } catch (e: any) {
      pushLog(false, `${label}: ${e.message || String(e)}`);
    } finally { setBusy(false); }
  }

  if (!conn) {
    return (
      <>
        <h1 className="page-title">Admin</h1>
        <div className="card">
          <p>Select or add a <a href="/connections">connection</a> first.</p>
        </div>
      </>
    );
  }

  return (
    <>
      <div className="eyebrow">Management</div>
      <h1 className="page-title">Admin · {conn.name}</h1>
      <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginBottom: 24 }}>
        {connections.length > 1 && (
          <select
            value={conn.id}
            onChange={(e) => setActive(e.target.value)}
            title="Administer a different connection"
            style={{ fontFamily: 'var(--mono)', fontSize: 12, padding: '4px 8px', border: '1px solid var(--gray-200)', background: 'white' }}
          >
            {connections.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
        )}
        <span style={{ fontFamily: 'var(--mono)', color: 'var(--gray-500)', fontSize: 13 }}>{conn.baseUrl}</span>
      </div>

      {error && (
        <div className="card" style={{ borderLeft: '3px solid var(--red)' }}>
          <strong>Error:</strong> {error}
        </div>
      )}

      <div className="grid grid-2" style={{ gap: 16, alignItems: 'start' }}>
        {/* Database panel */}
        <div className="card">
          <h3>Database</h3>
          <div className="setting-row">
            <span className="key">File size</span>
            <span className="val">{stats?.fileSizeMb != null ? `${stats.fileSizeMb} MB` : '—'}</span>
          </div>
          <div className="setting-row">
            <span className="key">Pages</span>
            <span className="val">{stats?.pageCount ?? '—'}</span>
          </div>
          <div className="setting-row">
            <span className="key">Cached / dirty</span>
            <span className="val">{stats?.cachedPages ?? '—'} / {stats?.dirtyPages ?? '—'}</span>
          </div>
          <div className="setting-row">
            <span className="key">Collections</span>
            <span className="val">{stats?.collections?.length ?? 0}</span>
          </div>
          <div className="toolbar" style={{ marginTop: 12, gap: 6 }}>
            <button onClick={() => run('flush', () => flushDb())} disabled={busy}
              style={btnStyle()}>⤓ Flush</button>
            <button onClick={() => run('checkpoint', () => checkpointDb())} disabled={busy}
              style={btnStyle()}>● Checkpoint</button>
            <button onClick={refresh} disabled={busy}
              style={btnStyle('ghost')}>⟲ Refresh</button>
          </div>
        </div>

        {/* Replication panel */}
        <div className="card">
          <h3>Replication</h3>
          <div className="setting-row">
            <span className="key">Role</span>
            <span className="val">
              {repl?.role ? <span className={`pill ${repl.role === 'leader' ? 'green' : repl.role === 'follower' ? 'gray' : 'gray'}`}>{repl.role}</span> : '—'}
            </span>
          </div>
          <div className="setting-row">
            <span className="key">Read-only</span>
            <span className="val">{repl?.readOnly ? <span className="pill red">YES</span> : 'no'}</span>
          </div>
          {repl?.role === 'leader' && (
            <>
              <div className="setting-row">
                <span className="key">Current seq</span>
                <span className="val">{repl?.leader?.currentSeq ?? '—'}</span>
              </div>
              <div className="setting-row">
                <span className="key">Followers</span>
                <span className="val">{repl?.leader?.followerCount ?? 0}</span>
              </div>
            </>
          )}
          {repl?.role === 'follower' && (
            <>
              <div className="setting-row">
                <span className="key">Last applied seq</span>
                <span className="val">{repl?.follower?.lastAppliedSeq ?? '—'}</span>
              </div>
              <div className="setting-row">
                <span className="key">Gaps detected</span>
                <span className="val">{repl?.follower?.gapsDetected ?? 0}</span>
              </div>
              <div className="setting-row">
                <span className="key">Auto-failed-over</span>
                <span className="val">{String(repl?.follower?.autoFailoverPromoted)}</span>
              </div>
            </>
          )}

          <ReplicationControls repl={repl} run={run} busy={busy} />
        </div>

        {/* Databases on this connection */}
        <div className="card" style={{ gridColumn: '1 / -1' }}>
          <h3>Databases <span style={{ fontWeight: 400, fontSize: 13, color: 'var(--gray-500)' }}>({dbs.length} attached)</span></h3>
          <p style={{ color: 'var(--gray-500)', fontSize: 13, marginTop: 0 }}>
            This service hosts {dbs.length} attached database{dbs.length === 1 ? '' : 's'}. The admin operations below act on the
            <strong> default</strong> database; create / drop / replication for the others lives on the{' '}
            <a href="/topology" style={{ color: 'var(--red)' }}>Topology</a> canvas.
          </p>
          {dbs.length > 0 && (
            <table className="table">
              <thead><tr><th>Name</th><th>Default</th><th>File</th></tr></thead>
              <tbody>
                {dbs.map((d: any) => (
                  <tr key={d.name}>
                    <td><strong>{d.name}</strong></td>
                    <td>{d.isDefault ? <span className="pill red">default</span> : '—'}</td>
                    <td style={{ fontFamily: 'var(--mono)', fontSize: 11, color: 'var(--gray-500)', wordBreak: 'break-all' }}>{d.filePath}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>

        {/* Collection ops */}
        <div className="card" style={{ gridColumn: '1 / -1' }}>
          <h3>Collections <span style={{ fontWeight: 400, fontSize: 13, color: 'var(--gray-500)' }}>· default DB</span></h3>
          <p style={{ color: 'var(--gray-500)', fontSize: 13, marginTop: 0 }}>
            Per-collection maintenance. Compact reclaims space from deletes; rebuild fixes index drift; drop is destructive.
          </p>
          {!stats?.collections?.length ? (
            <p style={{ color: 'var(--gray-500)' }}>No collections yet.</p>
          ) : (
            <table className="table">
              <thead>
                <tr>
                  <th>Name</th>
                  <th>Documents</th>
                  <th>Indexes</th>
                  <th>Actions</th>
                </tr>
              </thead>
              <tbody>
                {stats.collections.map((c: any) => (
                  <tr key={c.name}>
                    <td><strong>{c.name}</strong></td>
                    <td>{c.documentCount?.toLocaleString?.() ?? c.documentCount}</td>
                    <td>{c.indexes?.length ?? 0}</td>
                    <td>
                      <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
                        <button onClick={() => run(`compact ${c.name}`, () => compactCollection(c.name))} disabled={busy} style={btnStyle('xs')}>Compact</button>
                        <button onClick={() => run(`rebuild indexes ${c.name}`, () => rebuildIndexes(c.name))} disabled={busy} style={btnStyle('xs')}>Rebuild indexes</button>
                        {(c.indexes ?? []).map((idx: any) => (
                          <button key={idx.name} onClick={() => run(`rebuild ${idx.name}`, () => rebuildIndex(c.name, idx.name))} disabled={busy} style={btnStyle('xs', 'ghost')}>
                            ⟲ {idx.name}
                          </button>
                        ))}
                        <button onClick={() => {
                          if (confirm(`Drop "${c.name}" and its ${c.indexes?.length ?? 0} index(es)?\nThis cannot be undone.`)) {
                            run(`drop ${c.name}`, () => dropCollection(c.name));
                          }
                        }} disabled={busy} style={btnStyle('xs', 'danger')}>Drop</button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>

        {/* Activity log */}
        <div className="card" style={{ gridColumn: '1 / -1' }}>
          <h3>Activity</h3>
          {log.length === 0 ? (
            <p style={{ color: 'var(--gray-500)' }}>Operations performed in this session will appear here.</p>
          ) : (
            <div style={{ fontFamily: 'var(--mono)', fontSize: 12, maxHeight: 300, overflow: 'auto' }}>
              {log.map((l, i) => (
                <div key={i} style={{
                  padding: '6px 0', borderBottom: '1px solid var(--gray-100)',
                  color: l.ok ? 'var(--ink)' : 'var(--red)',
                }}>
                  <span style={{ color: 'var(--gray-500)', marginRight: 12 }}>{l.ts}</span>
                  <span>{l.ok ? '✓' : '✗'} {l.line}</span>
                </div>
              ))}
            </div>
          )}
        </div>
      </div>
    </>
  );
}

function ReplicationControls({ repl, run, busy }: { repl: any; run: any; busy: boolean }) {
  const role = repl?.role ?? 'none';
  const [showStart, setShowStart] = useState(false);
  const [leaderPort, setLeaderPort] = useState(5500);
  const [host, setHost] = useState('');
  const [followerPort, setFollowerPort] = useState(5500);
  const [silenceSeconds, setSilenceSeconds] = useState(10);

  return (
    <>
      <div className="toolbar" style={{ marginTop: 12, gap: 6, flexWrap: 'wrap' }}>
        <button onClick={() => run(repl?.readOnly ? 'exit read-only' : 'enter read-only', () => setReadOnly(!repl?.readOnly))} disabled={busy} style={btnStyle()}>
          {repl?.readOnly ? 'Exit read-only' : 'Enter read-only'}
        </button>
        {role === 'follower' && (
          <button onClick={() => run('promote follower', () => promoteToLeader(leaderPort))} disabled={busy} style={btnStyle('danger')}>
            Promote to leader
          </button>
        )}
        {role === 'follower' && (
          <button onClick={() => run('enable auto-failover', () => enableAutoFailover(silenceSeconds, leaderPort))} disabled={busy} style={btnStyle()}>
            Enable auto-failover ({silenceSeconds}s)
          </button>
        )}
        {repl?.follower?.autoFailoverPromoted === false && role === 'follower' && (
          <button onClick={() => run('disable auto-failover', () => disableAutoFailover())} disabled={busy} style={btnStyle('ghost')}>
            Disable auto-failover
          </button>
        )}
        {role === 'none' && (
          <button onClick={() => setShowStart(s => !s)} style={btnStyle('ghost')}>Start replication…</button>
        )}
      </div>

      {showStart && (
        <div style={{ marginTop: 12, padding: 12, background: 'var(--gray-100)', borderLeft: '3px solid var(--red)' }}>
          <div className="grid grid-2" style={{ gap: 12 }}>
            <div>
              <strong>Start as leader</strong>
              <div style={{ display: 'flex', gap: 6, marginTop: 8, alignItems: 'center' }}>
                <span style={{ fontSize: 12, color: 'var(--gray-500)' }}>port</span>
                <input type="number" value={leaderPort} onChange={e => setLeaderPort(parseInt(e.target.value) || 5500)} style={{ width: 90, padding: '4px 8px' }} />
                <button onClick={() => run('start as leader', () => startLeader(leaderPort))} disabled={busy} style={btnStyle('xs')}>Start</button>
              </div>
            </div>
            <div>
              <strong>Start as follower</strong>
              <div style={{ display: 'flex', gap: 6, marginTop: 8, alignItems: 'center' }}>
                <input type="text" placeholder="leader host" value={host} onChange={e => setHost(e.target.value)} style={{ flex: 1, padding: '4px 8px' }} />
                <input type="number" value={followerPort} onChange={e => setFollowerPort(parseInt(e.target.value) || 5500)} style={{ width: 80, padding: '4px 8px' }} />
                <button onClick={() => run(`follow ${host}:${followerPort}`, () => startFollower(host, followerPort))} disabled={busy || !host} style={btnStyle('xs')}>Follow</button>
              </div>
            </div>
          </div>
        </div>
      )}
    </>
  );
}

function btnStyle(variant: 'primary' | 'ghost' | 'danger' | 'xs' = 'primary', extra?: 'ghost' | 'danger'): React.CSSProperties {
  // Two call patterns:
  //   btnStyle()                  -> primary md
  //   btnStyle('ghost')           -> ghost md
  //   btnStyle('danger')          -> danger md
  //   btnStyle('xs')              -> primary xs
  //   btnStyle('xs', 'ghost')     -> ghost xs
  //   btnStyle('xs', 'danger')    -> danger xs
  const isXs = variant === 'xs';
  const v: 'primary' | 'ghost' | 'danger' =
    extra ?? (variant === 'xs' ? 'primary' : (variant as any));
  const padding = isXs ? '4px 10px' : '7px 14px';
  const fontSize = isXs ? 11 : 12;
  if (v === 'ghost') {
    return { padding, fontSize, background: 'transparent', color: 'var(--ink)', border: '1px solid var(--gray-200)', cursor: 'pointer', fontWeight: 500 };
  }
  if (v === 'danger') {
    return { padding, fontSize, background: 'transparent', color: 'var(--red)', border: '1px solid var(--red)', cursor: 'pointer', fontWeight: 600 };
  }
  return { padding, fontSize, background: 'var(--ink)', color: 'white', border: 'none', cursor: 'pointer', fontWeight: 600 };
}
