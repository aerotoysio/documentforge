'use client';

// Cross-connection overview. Each registered connection contributes its
// default-DB stats (/stats), its attached-DB count (/databases) and its
// replication role (/replication/status). Per-connection failures are
// tolerated — an unreachable host shows as offline rather than sinking the
// whole page.

import { useEffect, useState } from 'react';
import Link from 'next/link';
import { useConnections } from '@/lib/connections-context';
import { getStats, listDatabases, getReplicationStatus } from '@/lib/api';
import type { Connection } from '@/lib/connections';

interface ConnSummary {
  conn: Connection;
  reachable: boolean;
  role?: string;
  readOnly?: boolean;
  fileSizeMb?: number;
  collections?: number;
  documents?: number;
  indexes?: number;
  dbCount?: number;
}

export default function Dashboard() {
  const { connections, active, setActive } = useConnections();
  const [summaries, setSummaries] = useState<ConnSummary[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;
    async function load() {
      const out = await Promise.all(connections.map(async (conn): Promise<ConnSummary> => {
        try {
          const [stats, dbs, repl] = await Promise.all([
            getStats({ connection: conn }),
            listDatabases({ connection: conn }).catch(() => null),
            getReplicationStatus({ connection: conn }).catch(() => null),
          ]);
          const collections = stats.collections ?? [];
          return {
            conn, reachable: true,
            role: repl?.role, readOnly: repl?.readOnly,
            fileSizeMb: stats.fileSizeMb,
            collections: collections.length,
            documents: collections.reduce((s: number, c: any) => s + (c.documentCount || 0), 0),
            indexes: collections.reduce((s: number, c: any) => s + (c.indexes?.length || 0), 0),
            dbCount: dbs?.count ?? dbs?.databases?.length ?? 1,
          };
        } catch {
          return { conn, reachable: false };
        }
      }));
      if (!cancelled) { setSummaries(out); setLoading(false); }
    }
    load();
    const t = window.setInterval(load, 5000);
    return () => { cancelled = true; window.clearInterval(t); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [connections.map((c) => c.id + c.baseUrl).join('|')]);

  if (connections.length === 0) {
    return (
      <>
        <div className="eyebrow">Dashboard</div>
        <h1 className="page-title">Overview</h1>
        <div className="card">
          <h3>No connections yet</h3>
          <p style={{ color: 'var(--gray-500)' }}>
            Add a DocumentForge endpoint on the <Link href="/connections" style={{ color: 'var(--red)' }}>Connections</Link> page,
            or spawn a local service there. Each one will show up here with its live stats.
          </p>
        </div>
      </>
    );
  }

  const reachable = summaries.filter((s) => s.reachable);
  const totalDbs = reachable.reduce((s, x) => s + (x.dbCount || 0), 0);
  const totalDocs = reachable.reduce((s, x) => s + (x.documents || 0), 0);
  const totalColls = reachable.reduce((s, x) => s + (x.collections || 0), 0);
  const totalSize = reachable.reduce((s, x) => s + (x.fileSizeMb || 0), 0);

  return (
    <>
      <div className="eyebrow">Dashboard</div>
      <h1 className="page-title">Overview</h1>

      <div className="grid grid-4">
        <div className="stat">
          <div className="label">Connections</div>
          <div className="num">{reachable.length}<span style={{ fontSize: 18, color: 'var(--gray-500)' }}> / {connections.length}</span></div>
          <div className="sub">{connections.length - reachable.length > 0 ? `${connections.length - reachable.length} unreachable` : 'all reachable'}</div>
        </div>
        <div className="stat">
          <div className="label">Databases</div>
          <div className="num">{totalDbs.toLocaleString()}</div>
          <div className="sub">attached across all services</div>
        </div>
        <div className="stat">
          <div className="label">Documents</div>
          <div className="num">{totalDocs.toLocaleString()}</div>
          <div className="sub">in default DBs · {totalColls} collection{totalColls === 1 ? '' : 's'}</div>
        </div>
        <div className="stat">
          <div className="label">Total size</div>
          <div className="num">{totalSize.toFixed(1)} <span style={{ fontSize: 18, color: 'var(--gray-500)' }}>MB</span></div>
          <div className="sub">default DB files</div>
        </div>
      </div>

      <h2>Connections</h2>
      {loading && summaries.length === 0 ? (
        <p style={{ color: 'var(--gray-500)' }}>Probing every connection…</p>
      ) : (
        <div className="grid" style={{ gap: 12 }}>
          {summaries.map(({ conn, reachable, role, readOnly, fileSizeMb, collections, documents, dbCount }) => {
            const isActive = conn.id === active?.id;
            return (
              <div key={conn.id} className="card" style={{ borderLeft: `4px solid ${reachable ? (conn.color || 'var(--red)') : 'var(--gray-200)'}`, opacity: reachable ? 1 : 0.7 }}>
                <div style={{ display: 'flex', alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
                  <span style={{ display: 'inline-block', width: 9, height: 9, borderRadius: '50%', background: reachable ? 'var(--green)' : 'var(--red)' }} title={reachable ? 'reachable' : 'unreachable'} />
                  <div style={{ flex: 1, minWidth: 200 }}>
                    <div style={{ fontSize: 16, fontWeight: 700 }}>
                      {conn.name}
                      {isActive && <span className="pill green" style={{ marginLeft: 8 }}>active</span>}
                      {role && role !== 'none' && <span className={`pill ${role === 'leader' ? 'red' : 'gray'}`} style={{ marginLeft: 6 }}>{role}</span>}
                      {readOnly && <span className="pill red" style={{ marginLeft: 6 }}>RO</span>}
                    </div>
                    <div style={{ fontFamily: 'var(--mono)', fontSize: 12, color: 'var(--gray-500)' }}>{conn.baseUrl.replace(/^https?:\/\//, '')}</div>
                  </div>
                  {reachable ? (
                    <div style={{ display: 'flex', gap: 20, fontSize: 13, flexWrap: 'wrap' }}>
                      <Metric label="databases" value={(dbCount ?? 1).toLocaleString()} />
                      <Metric label="collections" value={(collections ?? 0).toLocaleString()} />
                      <Metric label="docs (default)" value={(documents ?? 0).toLocaleString()} />
                      <Metric label="size" value={`${(fileSizeMb ?? 0).toFixed(1)} MB`} />
                    </div>
                  ) : (
                    <span style={{ color: 'var(--red)', fontSize: 12, fontFamily: 'var(--mono)' }}>offline</span>
                  )}
                  <div style={{ display: 'flex', gap: 6 }}>
                    {!isActive && (
                      <button onClick={() => setActive(conn.id)} style={{ background: 'var(--ink)', color: 'white', border: 'none', padding: '6px 12px', fontSize: 12, fontWeight: 600, cursor: 'pointer' }}>
                        Make active
                      </button>
                    )}
                    <Link href="/studio" style={{ background: 'transparent', color: 'var(--ink)', border: '1px solid var(--gray-200)', padding: '6px 12px', fontSize: 12, cursor: 'pointer', textDecoration: 'none' }}>Studio</Link>
                  </div>
                </div>
              </div>
            );
          })}
        </div>
      )}

      <div style={{ marginTop: 16, fontSize: 12, color: 'var(--gray-500)' }}>
        Per-connection document / collection counts reflect each service&apos;s <strong>default database</strong>.
        See the full per-DB tree in <Link href="/studio" style={{ color: 'var(--red)' }}>Studio</Link> and the
        cluster shape in <Link href="/topology" style={{ color: 'var(--red)' }}>Topology</Link>. Auto-refreshes every 5s.
      </div>
    </>
  );
}

function Metric({ label, value }: { label: string; value: string }) {
  return (
    <div style={{ textAlign: 'right' }}>
      <div style={{ fontWeight: 700 }}>{value}</div>
      <div style={{ fontSize: 10, color: 'var(--gray-500)', textTransform: 'uppercase', letterSpacing: '0.05em' }}>{label}</div>
    </div>
  );
}
