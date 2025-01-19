'use client';

import './studio.css';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Explorer } from './Explorer';
import { QueryTab } from './tabs/QueryTab';
import { BrowseTab } from './tabs/BrowseTab';
import { InspectorTab } from './tabs/InspectorTab';
import { IndexesTab } from './tabs/IndexesTab';
import { flushDb } from '@/lib/api';
import { useConnections } from '@/lib/connections-context';
import type { Connection } from '@/lib/connections';
import type { Tab, StatusInfo, TabContext } from './studio-types';

export default function StudioPage() {
  const { connections, active, setActive } = useConnections();
  const [tabs, setTabs] = useState<Tab[]>([
    { id: 't0', kind: 'query', title: 'Query', initialSql: 'SELECT * FROM orders LIMIT 50' } as Tab,
  ]);
  const [activeId, setActiveId] = useState<string>('t0');
  const [status, setStatus] = useState<StatusInfo>({});
  const [explorerKey, setExplorerKey] = useState(0);
  const tabSeq = useRef(1);
  const [connSwitcherOpen, setConnSwitcherOpen] = useState(false);

  // When the global active connection changes, only the Explorer re-fetches.
  // Existing tabs stay pinned to whatever connection they were opened against
  // (see openTab() — every new tab is stamped with active?.id at creation
  // time). Per-tab retargeting goes through setTabConnection().
  useEffect(() => {
    setExplorerKey(k => k + 1);
  }, [active?.id]);

  const newTabId = () => `t${tabSeq.current++}`;

  // Resolve a tab's pinned connection back to a real Connection object.
  // Falls back to the global active connection when the tab has no pin
  // (legacy tabs from before this change) or when the pinned connection has
  // since been deleted.
  function resolveConnection(tab: Tab | undefined): Connection | null {
    if (!tab?.connectionId) return active;
    return connections.find(c => c.id === tab.connectionId) ?? active;
  }

  // ----- Tab management -----
  const setTabStatus = useCallback((s: Partial<StatusInfo>) => {
    setStatus(prev => ({ ...prev, ...s }));
  }, []);

  const setStatusMeta = useCallback((m: Partial<StatusInfo>) => {
    setStatus(prev => ({ ...prev, ...m }));
  }, []);

  const openTab = useCallback((tab: Tab, focus = true) => {
    setTabs(prev => {
      const existing = prev.find(t => t.id === tab.id);
      // Newly created tabs inherit the global active connection. Existing
      // tabs keep whatever they were already pinned to.
      const stamped: Tab = { ...tab, connectionId: tab.connectionId ?? active?.id };
      if (existing) return prev.map(t => t.id === tab.id ? stamped : t);
      return [...prev, stamped];
    });
    if (focus) setActiveId(tab.id);
  }, [active?.id]);

  const closeTab = useCallback((id: string) => {
    setTabs(prev => {
      const next = prev.filter(t => t.id !== id);
      setActiveId(curActive => {
        if (curActive !== id) return curActive;
        return next.length ? next[next.length - 1].id : '';
      });
      return next;
    });
  }, []);

  const refreshTab = useCallback((id: string) => {
    setTabs(prev => prev.map(t => t.id === id ? { ...t, refreshKey: (t.refreshKey ?? 0) + 1 } : t));
  }, []);

  const setTabConnection = useCallback((tabId: string, connectionId: string) => {
    setTabs(prev => prev.map(t => t.id === tabId
      ? { ...t, connectionId, refreshKey: (t.refreshKey ?? 0) + 1 }
      : t));
  }, []);

  const notifyChanged = useCallback((collection: string) => {
    // Bump anything browsing this collection so they re-fetch.
    setTabs(prev => prev.map(t => {
      if ((t.kind === 'browse' || t.kind === 'indexes') && (t as any).collection === collection) {
        return { ...t, refreshKey: (t.refreshKey ?? 0) + 1 };
      }
      return t;
    }));
    setExplorerKey(k => k + 1);
  }, []);

  // ----- Explorer actions -----
  // Double-clicking a collection (or its "📄 Documents" sub-node) opens a
  // QueryTab pre-filled with `SELECT * FROM <coll> LIMIT 100` rather than a
  // BrowseTab. This keeps the SQL editor visible so the user can tweak the
  // query (add WHERE / ORDER BY / etc.) without losing the results pane.
  const openBrowse = useCallback((collection: string) => {
    const id = `query:${collection}`;
    openTab({
      id,
      kind: 'query',
      title: collection,
      initialSql: `SELECT * FROM ${collection} LIMIT 100`,
    } as Tab);
  }, [openTab]);

  const openIndexes = useCallback((collection: string) => {
    const id = `indexes:${collection}`;
    openTab({ id, kind: 'indexes', title: `🔑 ${collection}`, collection } as Tab);
  }, [openTab]);

  const openInspector = useCallback((collection: string, doc: any) => {
    const docId = doc?._id;
    if (!docId) return;
    const id = `inspect:${collection}:${docId}`;
    const initialJson = JSON.stringify(doc, null, 2);
    openTab({ id, kind: 'inspector', title: `${collection}/${shortId(docId)}`, collection, docId, initialJson } as Tab);
  }, [openTab]);

  const newQuery = useCallback(() => {
    openTab({ id: newTabId(), kind: 'query', title: 'Query', initialSql: '' } as Tab);
  }, [openTab]);

  const refreshExplorer = useCallback(() => setExplorerKey(k => k + 1), []);

  const tabCtx: TabContext = useMemo(() => ({
    setStatus: setTabStatus,
    openInspector,
    closeTab,
    refreshTab,
    setTabConnection,
    notifyChanged,
  }), [setTabStatus, openInspector, closeTab, refreshTab, setTabConnection, notifyChanged]);

  const activeTab = tabs.find(t => t.id === activeId);
  const activeTabConn = resolveConnection(activeTab);
  const activeTabConnDangling = !!activeTab?.connectionId
    && !connections.find(c => c.id === activeTab.connectionId);

  // Persisted Explorer column width (resizable via splitter)
  const studioRef = useRef<HTMLDivElement>(null);
  const [explorerWidth, setExplorerWidth] = useState<number>(260);
  useEffect(() => {
    const saved = window.localStorage.getItem('dfdb_studio_explorer_width');
    if (saved) {
      const n = parseInt(saved, 10);
      if (Number.isFinite(n) && n >= 180 && n <= 600) setExplorerWidth(n);
    }
  }, []);
  useEffect(() => {
    if (studioRef.current) studioRef.current.style.setProperty('--studio-explorer-width', explorerWidth + 'px');
  }, [explorerWidth]);

  function startColResize(e: React.MouseEvent) {
    e.preventDefault();
    const target = e.currentTarget as HTMLElement;
    target.classList.add('dragging');
    const startX = e.clientX;
    const startWidth = explorerWidth;
    function onMove(ev: MouseEvent) {
      const next = Math.max(180, Math.min(600, startWidth + (ev.clientX - startX)));
      setExplorerWidth(next);
    }
    function onUp() {
      target.classList.remove('dragging');
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
      // Persist final value (avoid hammering localStorage on every mousemove).
      const w = (studioRef.current?.style.getPropertyValue('--studio-explorer-width') ?? '').replace('px', '');
      if (w) window.localStorage.setItem('dfdb_studio_explorer_width', w);
    }
    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
  }

  async function onFlush() {
    try { await flushDb(); refreshExplorer(); } catch (e) { /* swallow */ }
  }

  return (
    <div className="studio" ref={studioRef}>
      {/* Toolbar */}
      <div className="studio-toolbar">
        <button onClick={newQuery} title="New SQL query tab">＋ New Query</button>
        <button onClick={refreshExplorer} title="Refresh tree">⟲ Refresh</button>
        <span className="sep" />
        <button onClick={onFlush} title="Flush dirty pages to disk">⤓ Flush</button>
        <span className="spacer" />
        {connections.length > 1 ? (
          <select
            value={active?.id ?? ''}
            onChange={e => setActive(e.target.value)}
            style={{
              fontFamily: 'var(--mono)', fontSize: 11, padding: '3px 6px',
              border: '1px solid var(--studio-line-strong)', background: 'var(--studio-panel)',
              color: 'var(--ink)', borderRadius: 3,
            }}
            title="Active connection"
          >
            {connections.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
          </select>
        ) : null}
        <span
          className="endpoint"
          style={active?.color ? { borderLeft: `3px solid ${active.color}`, paddingLeft: 8 } : undefined}
        >
          {active ? `${active.name} · ${active.baseUrl.replace(/^https?:\/\//, '')}` : (status.endpoint ?? '…')}
        </span>
      </div>

      {/* Explorer (left) */}
      <Explorer
        refreshKey={explorerKey}
        onRefresh={refreshExplorer}
        onOpenBrowse={openBrowse}
        onOpenIndexes={openIndexes}
        onSetStatusMeta={setStatusMeta}
      />

      {/* Vertical splitter between Explorer and Workspace */}
      <div className="studio-splitter-col" onMouseDown={startColResize} title="Drag to resize" />

      {/* Workspace (center) */}
      <div className="studio-workspace">
        {tabs.length === 0 ? (
          <div className="workspace-empty">
            <div className="em">Nothing open</div>
            <div className="hint">⌃ + click a collection in the explorer · or hit "New Query"</div>
          </div>
        ) : (
          <>
            <div className="tab-strip">
              {tabs.map(t => {
                const tabConn = resolveConnection(t);
                const dot = tabConn?.color || 'var(--gray-200)';
                return (
                  <div
                    key={t.id}
                    className={`tab ${t.id === activeId ? 'active' : ''}`}
                    onClick={() => setActiveId(t.id)}
                    title={tabConn ? `Connected to ${tabConn.name} · ${tabConn.baseUrl}` : 'No connection'}
                  >
                    <span style={{ display: 'inline-block', width: 8, height: 8, borderRadius: '50%', background: dot, marginRight: 6 }} />
                    <span className="icon">{tabIcon(t.kind)}</span>
                    <span>{t.title}</span>
                    <span className="close" onClick={e => { e.stopPropagation(); closeTab(t.id); }} title="Close">×</span>
                  </div>
                );
              })}
            </div>

            {/* Per-tab connection badge: shows what the active tab is talking
                to, and lets you retarget it without affecting other tabs. */}
            {activeTab && (
              <div style={{
                display: 'flex', alignItems: 'center', gap: 10,
                padding: '6px 12px',
                borderBottom: '1px solid var(--studio-line)',
                background: 'var(--studio-panel)',
                fontSize: 12, position: 'relative',
              }}>
                <span style={{ color: 'var(--gray-500)', fontFamily: 'var(--mono)', fontSize: 10, letterSpacing: '0.08em' }}>
                  THIS TAB →
                </span>
                <span style={{
                  display: 'inline-block', width: 10, height: 10, borderRadius: '50%',
                  background: activeTabConn?.color || 'var(--gray-200)',
                }} />
                <strong style={{ fontSize: 12 }}>{activeTabConn?.name ?? 'No connection'}</strong>
                <span style={{ color: 'var(--gray-500)', fontFamily: 'var(--mono)', fontSize: 11 }}>
                  {activeTabConn ? activeTabConn.baseUrl.replace(/^https?:\/\//, '') : ''}
                </span>
                {activeTabConnDangling && (
                  <span className="pill red" style={{ fontSize: 10 }}>
                    pinned connection deleted — using global active
                  </span>
                )}
                <span className="spacer" />
                {connections.length > 1 && (
                  <>
                    <button
                      onClick={() => setConnSwitcherOpen(o => !o)}
                      style={{
                        background: 'transparent', border: '1px solid var(--studio-line-strong)',
                        padding: '3px 10px', cursor: 'pointer', fontSize: 11, borderRadius: 3,
                      }}
                      title="Retarget this tab to a different connection"
                    >
                      Switch ▾
                    </button>
                    {connSwitcherOpen && (
                      <div style={{
                        position: 'absolute', right: 12, top: 'calc(100% + 2px)',
                        background: 'white', border: '1px solid var(--studio-line-strong)',
                        boxShadow: '0 4px 12px rgba(0,0,0,0.08)',
                        zIndex: 10, minWidth: 240,
                      }}>
                        {connections.map(c => (
                          <div
                            key={c.id}
                            onClick={() => {
                              setTabConnection(activeTab.id, c.id);
                              setConnSwitcherOpen(false);
                            }}
                            style={{
                              display: 'flex', alignItems: 'center', gap: 8,
                              padding: '8px 12px', cursor: 'pointer',
                              background: c.id === activeTab.connectionId ? 'var(--studio-panel)' : 'transparent',
                              fontSize: 12,
                            }}
                          >
                            <span style={{ display: 'inline-block', width: 8, height: 8, borderRadius: '50%', background: c.color || 'var(--red)' }} />
                            <strong>{c.name}</strong>
                            <span style={{ color: 'var(--gray-500)', fontFamily: 'var(--mono)', fontSize: 10, marginLeft: 'auto' }}>
                              {c.baseUrl.replace(/^https?:\/\//, '')}
                            </span>
                          </div>
                        ))}
                      </div>
                    )}
                  </>
                )}
              </div>
            )}

            <div className="tab-body" key={activeTab?.id}>
              {activeTab && renderTab(activeTab, tabCtx, activeTabConn)}
            </div>
          </>
        )}
      </div>

      {/* Status bar (bottom) */}
      <div className="studio-status">
        <span className="pill">
          <span className={`dot ${status.role && status.role !== 'none' ? 'ok' : ''}`} />
          {status.node ?? '—'}
        </span>
        <span className="pill">role: <strong>{status.role ?? '—'}</strong></span>
        {status.readOnly && <span className="pill warn">⚠ read-only</span>}
        {status.plan && <span className="pill">plan: {status.plan}</span>}
        {status.executionMs != null && <span className="pill">{status.executionMs.toFixed(2)} ms</span>}
        {status.rows != null && <span className="pill">{status.rows} row{status.rows === 1 ? '' : 's'}</span>}
        {status.affected != null && status.affected > 0 && <span className="pill">{status.affected} affected</span>}
        <span className="spacer" />
        <span className="pill">DocumentForge Studio · v0.1</span>
      </div>
    </div>
  );
}

function shortId(id: string) {
  if (!id || id.length < 12) return id;
  return id.slice(0, 6) + '…' + id.slice(-4);
}

function tabIcon(k: string) {
  switch (k) {
    case 'query':     return '▶';
    case 'browse':    return '▤';
    case 'inspector': return '📄';
    case 'indexes':   return '🔑';
    default:          return '·';
  }
}

function renderTab(t: Tab, ctx: TabContext, connection: Connection | null) {
  switch (t.kind) {
    case 'query':     return <QueryTab tab={t} ctx={ctx} connection={connection} />;
    case 'browse':    return <BrowseTab tab={t} ctx={ctx} connection={connection} />;
    case 'inspector': return <InspectorTab tab={t} ctx={ctx} connection={connection} />;
    case 'indexes':   return <IndexesTab tab={t} ctx={ctx} connection={connection} />;
  }
}
