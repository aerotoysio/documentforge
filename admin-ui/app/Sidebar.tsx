'use client';

import Link from 'next/link';
import { useEffect, useState } from 'react';
import { useConnections } from '@/lib/connections-context';
import { listDatabases } from '@/lib/api';

export function Sidebar() {
  const { connections, active, setActive } = useConnections();
  const [pickerOpen, setPickerOpen] = useState(false);

  // Issue #66 Phase 2: peek at the active connection's database registry
  // so the sidebar can show "you're working on DB X" alongside the
  // connection. Best-effort — old single-DB services pre-#66 will 404 on
  // /databases and we just hide the indicator.
  const [defaultDb, setDefaultDb] = useState<string | null>(null);
  const [dbCount, setDbCount] = useState<number | null>(null);
  useEffect(() => {
    if (!active) { setDefaultDb(null); setDbCount(null); return; }
    let cancelled = false;
    listDatabases({ connection: active })
      .then(r => { if (!cancelled) { setDefaultDb(r.default); setDbCount(r.count); } })
      .catch(() => { if (!cancelled) { setDefaultDb(null); setDbCount(null); } });
    return () => { cancelled = true; };
  }, [active?.id, active?.baseUrl]);

  return (
    <aside className="sidebar">
      <div className="brand">
        <span className="brand-dot" />
        DocumentForge
      </div>

      {/* Connection picker */}
      <div className="conn-picker">
        <div className="conn-picker-label">CONNECTION</div>
        {connections.length === 0 ? (
          <Link href="/connections" className="conn-picker-empty">
            + Add a connection
          </Link>
        ) : (
          <div className="conn-picker-current" onClick={() => setPickerOpen(o => !o)}>
            <span
              className="conn-dot"
              style={{ background: active?.color || 'var(--red)' }}
            />
            <span className="conn-name">{active?.name ?? 'No connection'}</span>
            <span className="conn-caret">{pickerOpen ? '▲' : '▼'}</span>
          </div>
        )}
        {pickerOpen && (
          <div className="conn-picker-menu">
            {connections.map(c => (
              <div
                key={c.id}
                className={`conn-picker-option ${c.id === active?.id ? 'active' : ''}`}
                onClick={() => { setActive(c.id); setPickerOpen(false); }}
              >
                <span className="conn-dot" style={{ background: c.color || 'var(--red)' }} />
                <span className="conn-name">{c.name}</span>
                <span className="conn-url">{c.baseUrl.replace(/^https?:\/\//, '')}</span>
              </div>
            ))}
            <Link href="/connections" className="conn-picker-manage" onClick={() => setPickerOpen(false)}>
              Manage connections →
            </Link>
          </div>
        )}
        {active && !pickerOpen && (
          <div className="conn-picker-url">{active.baseUrl.replace(/^https?:\/\//, '')}</div>
        )}
      </div>

      {/* Issue #66 Phase 2 — active database indicator. Quietly absent
          when the connected service predates multi-DB or returns 0 DBs. */}
      {active && defaultDb && (
        <Link href="/databases" className="db-indicator" title="Manage attached databases on this service">
          <div className="db-indicator-label">DATABASE</div>
          <div className="db-indicator-row">
            <span className="db-indicator-dot" />
            <span className="db-indicator-name">{defaultDb}</span>
            {dbCount !== null && dbCount > 1 && (
              <span className="db-indicator-count">+{dbCount - 1}</span>
            )}
          </div>
        </Link>
      )}

      {/* Consolidation pass (#66 follow-up):
          - /swarm and /databases live on as pages but their entry point is
            now the unified Topology page (List + Across-services tabs lift
            their content). Old bookmarks still work.
          - /cluster (static config wizard) and /rebalance (pure docs) are
            destination pages, not operational views — they move to the
            developer portal (#68) and out of the sidebar. */}
      <nav>
        <Link href="/">Dashboard</Link>
        <Link href="/studio" className="nav-headline">✦ Studio</Link>
        <Link href="/topology">⌬ Topology</Link>
        <Link href="/admin">⚙ Admin</Link>
        <Link href="/connections">⇋ Connections</Link>
      </nav>

      <div style={{
        marginTop: 'auto', fontSize: 11, color: '#666',
        fontFamily: 'var(--mono)', letterSpacing: '0.05em', paddingTop: 40
      }}>
        v1.0.0 · MIT
      </div>
    </aside>
  );
}
