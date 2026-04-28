'use client';

import Link from 'next/link';
import { useState } from 'react';
import { useConnections } from '@/lib/connections-context';

export function Sidebar() {
  const { connections, active, setActive } = useConnections();
  const [pickerOpen, setPickerOpen] = useState(false);

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

      <nav>
        <Link href="/">Dashboard</Link>
        <Link href="/studio" className="nav-headline">✦ Studio</Link>
        <Link href="/swarm">🐝 Swarm</Link>
        <Link href="/admin">⚙ Admin</Link>
        <Link href="/connections">⇋ Connections</Link>
        <div className="nav-divider" />
        <Link href="/cluster">Cluster topology</Link>
        <Link href="/rebalance">Rebalance guide</Link>
      </nav>

      <div style={{
        marginTop: 'auto', fontSize: 11, color: '#666',
        fontFamily: 'var(--mono)', letterSpacing: '0.05em', paddingTop: 40
      }}>
        v0.1.0 · MIT
      </div>
    </aside>
  );
}
