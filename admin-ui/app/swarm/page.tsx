'use client';

import { useEffect, useState } from 'react';
import Link from 'next/link';
import { useConnections } from '@/lib/connections-context';
import { getHealth, getStats, getReplicationStatus, flushDb, checkpointDb } from '@/lib/api';
import type { Connection } from '@/lib/connections';

interface NodeView {
  conn: Connection;
  loading: boolean;
  health?: any;
  stats?: any;
  repl?: any;
  error?: string;
  rttMs?: number;
}

export default function SwarmPage() {
  const { connections, setActive } = useConnections();
  const [nodes, setNodes] = useState<NodeView[]>([]);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState(false);

  async function probe(c: Connection): Promise<NodeView> {
    const t0 = performance.now();
    try {
      const [health, stats, repl] = await Promise.all([
        getHealth({ connection: c }),
        getStats({ connection: c }).catch(() => null),
        getReplicationStatus({ connection: c }).catch(() => null),
      ]);
      const rttMs = Math.round(performance.now() - t0);
      return { conn: c, loading: false, health, stats, repl, rttMs };
    } catch (e: any) {
      return { conn: c, loading: false, error: e.message || String(e), rttMs: Math.round(performance.now() - t0) };
    }
  }

  async function probeAll() {
    setNodes(connections.map(c => ({ conn: c, loading: true })));
    const results = await Promise.all(connections.map(probe));
    setNodes(results);
  }

  useEffect(() => { probeAll(); /* eslint-disable-next-line */ }, [connections.length]);

  function toggle(id: string) {
    setSelected(prev => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id); else next.add(id);
      return next;
    });
  }
  function selectAll() { setSelected(new Set(connections.map(c => c.id))); }
  function clearSel()  { setSelected(new Set()); }

  async function broadcast(label: string, fn: (c: Connection) => Promise<any>) {
    if (selected.size === 0) { alert('Select at least one connection.'); return; }
    if (!confirm(`Run "${label}" on ${selected.size} connection(s)?`)) return;
    setBusy(true);
    const targets = connections.filter(c => selected.has(c.id));
    const results = await Promise.allSettled(targets.map(c => fn(c).then(r => ({ conn: c, r }))));
    const ok = results.filter(r => r.status === 'fulfilled').length;
    const fail = results.length - ok;
    alert(`${label}: ${ok} succeeded, ${fail} failed.`);
    setBusy(false);
    probeAll();
  }

  if (connections.length === 0) {
    return (
      <>
        <h1 className="page-title">Swarm</h1>
        <div className="card">
          <p>No connections registered yet.</p>
          <p>Head over to <Link href="/connections">Connections</Link> to add your first endpoint.</p>
        </div>
      </>
    );
  }

  // Roll-up
  const totalDocs = nodes.reduce((s, n) => s + sumDocs(n), 0);
  const totalSize = nodes.reduce((s, n) => s + (n.stats?.fileSizeMb ?? 0), 0);
  const onlineCount = nodes.filter(n => n.health?.status === 'ok').length;

  return (
    <>
      <div className="eyebrow">Fleet</div>
      <h1 className="page-title">Swarm <span style={{ color: 'var(--gray-500)', fontSize: 18, fontWeight: 500, marginLeft: 12 }}>{connections.length} node{connections.length === 1 ? '' : 's'}</span></h1>

      {/* Roll-up */}
      <div className="grid grid-4" style={{ marginBottom: 24 }}>
        <div className="stat">
          <div className="label">Online</div>
          <div className="num" style={{ color: onlineCount === connections.length ? 'var(--green)' : 'var(--red)' }}>
            {onlineCount} / {connections.length}
          </div>
        </div>
        <div className="stat">
          <div className="label">Total docs</div>
          <div className="num">{totalDocs.toLocaleString()}</div>
        </div>
        <div className="stat">
          <div className="label">Total size</div>
          <div className="num">{totalSize.toFixed(1)} <span style={{ fontSize: 16 }}>MB</span></div>
        </div>
        <div className="stat">
          <div className="label">Avg RTT</div>
          <div className="num">{avgRtt(nodes)} <span style={{ fontSize: 16 }}>ms</span></div>
        </div>
      </div>

      {/* Toolbar */}
      <div className="toolbar" style={{ flexWrap: 'wrap' }}>
        <button onClick={probeAll} disabled={busy} style={primaryBtn()}>
          ⟲ Refresh all
        </button>
        <span style={{ color: 'var(--gray-500)', fontSize: 13, marginLeft: 16 }}>
          Selected: <strong>{selected.size}</strong>
        </span>
        <button onClick={selectAll} style={ghostBtn()}>Select all</button>
        <button onClick={clearSel} style={ghostBtn()}>Clear</button>
        <span className="spacer" />
        <span style={{ color: 'var(--gray-500)', fontSize: 12, marginRight: 8 }}>Run on selected:</span>
        <button onClick={() => broadcast('Flush', c => flushDb({ connection: c }))} disabled={busy || selected.size === 0} style={primaryBtn()}>⤓ Flush</button>
        <button onClick={() => broadcast('Checkpoint', c => checkpointDb({ connection: c }))} disabled={busy || selected.size === 0} style={primaryBtn()}>● Checkpoint</button>
      </div>

      {/* Cards */}
      <div className="grid grid-2" style={{ gap: 16, marginTop: 16 }}>
        {nodes.map(n => {
          const sel = selected.has(n.conn.id);
          const ok = n.health?.status === 'ok';
          return (
            <div
              key={n.conn.id}
              className="card"
              style={{
                borderLeft: `4px solid ${n.conn.color || 'var(--red)'}`,
                outline: sel ? '2px solid var(--red)' : 'none',
                outlineOffset: -1,
                cursor: 'pointer',
                position: 'relative',
              }}
              onClick={() => toggle(n.conn.id)}
            >
              <div style={{ position: 'absolute', top: 16, right: 16, fontSize: 18, fontWeight: 700, color: sel ? 'var(--red)' : 'var(--gray-200)' }}>
                {sel ? '☑' : '☐'}
              </div>
              <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                <span style={{ display: 'inline-block', width: 10, height: 10, borderRadius: '50%', background: ok ? 'var(--green)' : 'var(--red)' }} />
                <h3 style={{ margin: 0 }}>{n.conn.name}</h3>
              </div>
              <div style={{ fontFamily: 'var(--mono)', fontSize: 11, color: 'var(--gray-500)', marginTop: 2 }}>
                {n.conn.baseUrl}
              </div>

              {n.loading && <p style={{ color: 'var(--gray-500)' }}>Pinging…</p>}
              {n.error && (
                <div style={{ marginTop: 10, padding: 8, background: 'rgba(217,4,41,0.06)', color: 'var(--red)', fontFamily: 'var(--mono)', fontSize: 11 }}>
                  ✗ {n.error}
                </div>
              )}
              {!n.loading && !n.error && (
                <div style={{ marginTop: 12 }}>
                  <div className="setting-row"><span className="key">Node</span><span className="val">{n.health?.node ?? '—'}</span></div>
                  <div className="setting-row">
                    <span className="key">Role</span>
                    <span className="val">
                      {n.repl?.role
                        ? <span className={`pill ${n.repl.role === 'leader' ? 'green' : 'gray'}`}>{n.repl.role}</span>
                        : '—'}
                      {n.health?.readOnly && <span className="pill red" style={{ marginLeft: 6 }}>RO</span>}
                    </span>
                  </div>
                  <div className="setting-row"><span className="key">Documents</span><span className="val">{sumDocs(n).toLocaleString()}</span></div>
                  <div className="setting-row"><span className="key">Size</span><span className="val">{n.stats?.fileSizeMb ?? '—'} MB</span></div>
                  <div className="setting-row"><span className="key">Uptime</span><span className="val">{secondsToHuman(n.health?.uptimeSeconds)}</span></div>
                  <div className="setting-row"><span className="key">RTT</span><span className="val">{n.rttMs ?? '—'} ms</span></div>
                </div>
              )}

              <div className="toolbar" style={{ marginTop: 14, justifyContent: 'flex-end' }} onClick={e => e.stopPropagation()}>
                <Link href="/studio" onClick={() => setActive(n.conn.id)}>
                  <button style={ghostBtn()}>Open in Studio</button>
                </Link>
                <Link href="/admin" onClick={() => setActive(n.conn.id)}>
                  <button style={ghostBtn()}>Admin</button>
                </Link>
              </div>
            </div>
          );
        })}
      </div>

      <div style={{ color: 'var(--gray-500)', fontSize: 12, marginTop: 32, lineHeight: 1.6 }}>
        💡 Tip — broadcast operations like Flush hit every selected node in parallel. For sharded
        deployments, register each shard once and use this page to verify every node is healthy
        before/after a rebalance.
      </div>
    </>
  );
}

function sumDocs(n: NodeView): number {
  return n.stats?.collections?.reduce((s: number, c: any) => s + (c.documentCount ?? 0), 0) ?? 0;
}
function avgRtt(nodes: NodeView[]): number {
  const rs = nodes.map(n => n.rttMs).filter((x): x is number => typeof x === 'number');
  if (!rs.length) return 0;
  return Math.round(rs.reduce((a, b) => a + b, 0) / rs.length);
}
function secondsToHuman(s?: number): string {
  if (s == null) return '—';
  if (s < 60) return `${Math.round(s)}s`;
  if (s < 3600) return `${Math.round(s / 60)}m`;
  if (s < 86400) return `${(s / 3600).toFixed(1)}h`;
  return `${(s / 86400).toFixed(1)}d`;
}
function primaryBtn(): React.CSSProperties {
  return { background: 'var(--ink)', color: 'white', border: 'none', padding: '7px 14px', fontSize: 12, fontWeight: 600, cursor: 'pointer' };
}
function ghostBtn(): React.CSSProperties {
  return { background: 'transparent', color: 'var(--ink)', border: '1px solid var(--gray-200)', padding: '6px 12px', fontSize: 12, cursor: 'pointer' };
}
