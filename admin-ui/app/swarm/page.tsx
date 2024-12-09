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

type ViewMode = 'topology' | 'cards';

export default function SwarmPage() {
  const { connections, setActive } = useConnections();
  const [nodes, setNodes] = useState<NodeView[]>([]);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState(false);
  const [view, setView] = useState<ViewMode>('topology');

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
        <div style={{ display: 'inline-flex', marginLeft: 12, border: '1px solid var(--gray-200)' }}>
          <button onClick={() => setView('topology')}
            style={view === 'topology' ? toggleBtnActive() : toggleBtn()}>Topology</button>
          <button onClick={() => setView('cards')}
            style={view === 'cards' ? toggleBtnActive() : toggleBtn()}>Cards</button>
        </div>
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

      {view === 'topology' ? (
        <TopologyView nodes={nodes} selected={selected} toggle={toggle} setActive={setActive} />
      ) : (
        /* Cards */
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
      )}

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
function toggleBtn(): React.CSSProperties {
  return { background: 'transparent', color: 'var(--gray-500)', border: 'none', padding: '6px 14px', fontSize: 12, fontWeight: 600, cursor: 'pointer' };
}
function toggleBtnActive(): React.CSSProperties {
  return { background: 'var(--ink)', color: 'white', border: 'none', padding: '6px 14px', fontSize: 12, fontWeight: 600, cursor: 'pointer' };
}

// ----------------------------------------------------------------------------
// Topology view — shards horizontally, leader-then-followers vertically.
//
// Grouping priority (most reliable first):
//   1. Live peer info — for every leader we've probed, its followers[].httpEndpoint
//      tells us which connections belong in its column. The engine's #51 work is
//      what makes this possible; pre-#51 followers are matched via port-guess.
//   2. Manual `shard` field on the Connection — fallback for nodes the leader
//      can't see (different network, partition, mid-failover, etc.).
//   3. Unassigned — everything else, in a grey column on the right.
// ----------------------------------------------------------------------------

interface TopologyProps {
  nodes: NodeView[];
  selected: Set<string>;
  toggle: (id: string) => void;
  setActive: (id: string | null) => void;
}

function normUrl(u: string | null | undefined): string {
  return (u || '').trim().replace(/\/+$/, '');
}

/** Walk every leader's followers[] and build a baseUrl → leaderBaseUrl map.
 *  This is what gives us "live" shard membership without manual labels. */
function buildLiveLeaderMap(nodes: NodeView[]): Map<string, string> {
  const out = new Map<string, string>();
  // Index by host:port so we can match a follower's TCP source IP back to one
  // of our registered connections, in case httpEndpoint isn't reported.
  const connByHostPort = new Map<string, string>();
  for (const n of nodes) {
    try {
      const u = new URL(n.conn.baseUrl);
      const key = u.port ? `${u.hostname}:${u.port}` : u.hostname;
      connByHostPort.set(key, normUrl(n.conn.baseUrl));
    } catch { /* ignore malformed */ }
  }

  for (const leaderNode of nodes) {
    if (leaderNode.repl?.role !== 'leader') continue;
    const leaderUrl = normUrl(leaderNode.conn.baseUrl);
    const followers = leaderNode.repl?.leader?.followers || [];
    for (const f of followers) {
      // Prefer the httpEndpoint the engine reported (post-#51 follower).
      let followerUrl = normUrl(f.httpEndpoint);
      if (!followerUrl && f.endpoint) {
        // Pre-#51 fallback: replication endpoint host → match against our
        // known connections by host (port may differ — replication vs HTTP).
        const host = f.endpoint.split(':').slice(0, -1).join(':') || f.endpoint;
        for (const [hp, url] of connByHostPort) {
          if (hp.startsWith(host + ':') || hp === host) { followerUrl = url; break; }
        }
      }
      if (followerUrl) out.set(followerUrl, leaderUrl);
    }
  }
  return out;
}

function TopologyView({ nodes, selected, toggle, setActive }: TopologyProps) {
  const liveLeaderMap = buildLiveLeaderMap(nodes);

  // Compute shard key for each node.
  function shardKeyFor(n: NodeView): string {
    const myUrl = normUrl(n.conn.baseUrl);
    // I'm a leader → my own URL is the shard key.
    if (n.repl?.role === 'leader') return `live:${myUrl}`;
    // I'm in some leader's followers list (live).
    const live = liveLeaderMap.get(myUrl);
    if (live) return `live:${live}`;
    // Manual fallback.
    const manual = n.conn.shard?.trim();
    if (manual) return `manual:${manual}`;
    return '__unassigned__';
  }

  const groups = new Map<string, NodeView[]>();
  for (const n of nodes) {
    const key = shardKeyFor(n);
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key)!.push(n);
  }

  // Order: live shards first (alpha by leader name), then manual, then unassigned.
  function rank(k: string): number {
    if (k === '__unassigned__') return 2;
    if (k.startsWith('manual:')) return 1;
    return 0;
  }
  const labels = Array.from(groups.keys()).sort((a, b) => {
    const r = rank(a) - rank(b);
    if (r !== 0) return r;
    return a.localeCompare(b);
  });

  if (nodes.length === 0) return null;

  return (
    <div style={{ marginTop: 16, display: 'flex', gap: 24, overflowX: 'auto', paddingBottom: 8 }}>
      {labels.map(key => {
        const members = [...groups.get(key)!].sort((a, b) => roleOrder(a) - roleOrder(b));
        const { headerLabel, sublabel, isUnassigned, isLive } = describeGroup(key, members);
        const accentColor = isUnassigned ? 'var(--gray-200)' : isLive ? 'var(--red)' : 'var(--ink)';
        const titleColor = isUnassigned ? 'var(--gray-500)' : isLive ? 'var(--red)' : 'var(--ink)';
        return (
          <div key={key} style={{ minWidth: 320, flex: '0 0 320px' }}>
            <div style={{
              fontFamily: 'var(--mono)', fontSize: 11, letterSpacing: '0.08em', textTransform: 'uppercase',
              color: titleColor, fontWeight: 700, marginBottom: 8,
              borderBottom: `2px solid ${accentColor}`, paddingBottom: 6,
            }}>
              {headerLabel}
              <span style={{ color: 'var(--gray-500)', fontWeight: 400, marginLeft: 8 }}>
                ({members.length} node{members.length === 1 ? '' : 's'})
              </span>
              {sublabel && (
                <div style={{ fontSize: 10, color: 'var(--gray-500)', fontWeight: 400, marginTop: 2, textTransform: 'none', letterSpacing: 0 }}>
                  {sublabel}
                </div>
              )}
            </div>

            <div style={{ display: 'flex', flexDirection: 'column', gap: 0 }}>
              {members.map((n, i) => (
                <TopologyNodeBox
                  key={n.conn.id}
                  node={n}
                  selected={selected.has(n.conn.id)}
                  onToggle={() => toggle(n.conn.id)}
                  setActive={setActive}
                  showConnectorAbove={i > 0}
                />
              ))}
            </div>
          </div>
        );
      })}
    </div>
  );
}

function describeGroup(key: string, members: NodeView[]): {
  headerLabel: string;
  sublabel?: string;
  isUnassigned: boolean;
  isLive: boolean;
} {
  if (key === '__unassigned__') {
    return { headerLabel: 'Unassigned', isUnassigned: true, isLive: false };
  }
  if (key.startsWith('manual:')) {
    return { headerLabel: `Shard · ${key.slice('manual:'.length)}`, sublabel: 'manual label', isUnassigned: false, isLive: false };
  }
  // live:<leaderBaseUrl>
  const leaderUrl = key.slice('live:'.length);
  const leaderNode = members.find(m => normUrl(m.conn.baseUrl) === leaderUrl);
  const label = leaderNode?.health?.node || leaderNode?.conn.name || hostnameOnly(leaderUrl);
  return { headerLabel: `Shard · ${label}`, sublabel: leaderUrl, isUnassigned: false, isLive: true };
}

function hostnameOnly(u: string): string {
  try { return new URL(u).hostname; } catch { return u; }
}

function roleOrder(n: NodeView): number {
  const r = n.repl?.role;
  if (r === 'leader') return 0;
  if (r === 'follower') return 1;
  return 2; // 'none' / unknown / errored
}

interface NodeBoxProps {
  node: NodeView;
  selected: boolean;
  onToggle: () => void;
  setActive: (id: string | null) => void;
  showConnectorAbove: boolean;
}

function TopologyNodeBox({ node, selected, onToggle, setActive, showConnectorAbove }: NodeBoxProps) {
  const ok = node.health?.status === 'ok';
  const role = node.repl?.role;
  const isLeader = role === 'leader';
  const accent = isLeader ? 'var(--red)' : 'var(--gray-200)';

  return (
    <div style={{ position: 'relative' }}>
      {showConnectorAbove && (
        <div style={{
          position: 'absolute', top: -1, left: '50%', width: 1, height: 14,
          background: 'var(--gray-200)', transform: 'translateX(-0.5px)',
        }} />
      )}
      <div
        onClick={onToggle}
        style={{
          marginTop: showConnectorAbove ? 14 : 0,
          background: 'white',
          border: '1px solid var(--gray-200)',
          borderTop: `3px solid ${accent}`,
          padding: '12px 14px',
          cursor: 'pointer',
          outline: selected ? '2px solid var(--red)' : 'none',
          outlineOffset: -1,
          position: 'relative',
        }}
      >
        <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <span style={{
            display: 'inline-block', width: 8, height: 8, borderRadius: '50%',
            background: node.loading ? 'var(--gray-200)' : ok ? 'var(--green)' : 'var(--red)',
          }} />
          <strong style={{ fontSize: 14, flex: 1 }}>{node.conn.name}</strong>
          {role && (
            <span className={`pill ${isLeader ? 'green' : 'gray'}`} style={{ fontSize: 10 }}>{role}</span>
          )}
          {node.health?.readOnly && <span className="pill red" style={{ fontSize: 10 }}>RO</span>}
          <span style={{ fontSize: 14, fontWeight: 700, color: selected ? 'var(--red)' : 'var(--gray-200)' }}>
            {selected ? '☑' : '☐'}
          </span>
        </div>
        <div style={{ fontFamily: 'var(--mono)', fontSize: 10, color: 'var(--gray-500)', marginTop: 4, wordBreak: 'break-all' }}>
          {node.conn.baseUrl}
        </div>

        {node.loading && <div style={{ marginTop: 8, fontSize: 11, color: 'var(--gray-500)' }}>Pinging…</div>}
        {node.error && (
          <div style={{ marginTop: 8, padding: 6, background: 'rgba(217,4,41,0.06)', color: 'var(--red)', fontFamily: 'var(--mono)', fontSize: 10 }}>
            ✗ {node.error}
          </div>
        )}
        {!node.loading && !node.error && (
          <div style={{ marginTop: 8, display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '2px 12px', fontSize: 11 }}>
            <span style={{ color: 'var(--gray-500)' }}>docs</span>
            <span style={{ textAlign: 'right', fontFamily: 'var(--mono)' }}>{sumDocs(node).toLocaleString()}</span>
            <span style={{ color: 'var(--gray-500)' }}>size</span>
            <span style={{ textAlign: 'right', fontFamily: 'var(--mono)' }}>{node.stats?.fileSizeMb ?? '—'} MB</span>
            <span style={{ color: 'var(--gray-500)' }}>uptime</span>
            <span style={{ textAlign: 'right', fontFamily: 'var(--mono)' }}>{secondsToHuman(node.health?.uptimeSeconds)}</span>
            <span style={{ color: 'var(--gray-500)' }}>rtt</span>
            <span style={{ textAlign: 'right', fontFamily: 'var(--mono)' }}>{node.rttMs ?? '—'} ms</span>
            {isLeader && node.repl?.leader?.followerCount !== undefined && (
              <>
                <span style={{ color: 'var(--gray-500)' }}>followers</span>
                <span style={{ textAlign: 'right', fontFamily: 'var(--mono)' }}>{node.repl.leader.followerCount}</span>
              </>
            )}
            {role === 'follower' && node.repl?.follower?.lastAppliedSeq !== undefined && (
              <>
                <span style={{ color: 'var(--gray-500)' }}>last seq</span>
                <span style={{ textAlign: 'right', fontFamily: 'var(--mono)' }}>{node.repl.follower.lastAppliedSeq}</span>
              </>
            )}
          </div>
        )}

        <div style={{ marginTop: 10, display: 'flex', gap: 6, justifyContent: 'flex-end' }} onClick={e => e.stopPropagation()}>
          <Link href="/studio" onClick={() => setActive(node.conn.id)}>
            <button style={ghostBtn()}>Studio</button>
          </Link>
          <Link href="/admin" onClick={() => setActive(node.conn.id)}>
            <button style={ghostBtn()}>Admin</button>
          </Link>
        </div>
      </div>
    </div>
  );
}
