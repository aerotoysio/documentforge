'use client';

import './studio.css';
import { useCallback, useMemo, useRef, useState } from 'react';
import { Explorer } from './Explorer';
import { QueryTab } from './tabs/QueryTab';
import { BrowseTab } from './tabs/BrowseTab';
import { InspectorTab } from './tabs/InspectorTab';
import { IndexesTab } from './tabs/IndexesTab';
import { flushDb } from '@/lib/api';
import type { Tab, StatusInfo, TabContext } from './studio-types';

export default function StudioPage() {
  const [tabs, setTabs] = useState<Tab[]>([
    { id: 't0', kind: 'query', title: 'Query', initialSql: 'SELECT * FROM orders LIMIT 50' } as Tab,
  ]);
  const [activeId, setActiveId] = useState<string>('t0');
  const [status, setStatus] = useState<StatusInfo>({});
  const [explorerKey, setExplorerKey] = useState(0);
  const tabSeq = useRef(1);

  const newTabId = () => `t${tabSeq.current++}`;

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
      if (existing) return prev.map(t => t.id === tab.id ? tab : t);
      return [...prev, tab];
    });
    if (focus) setActiveId(tab.id);
  }, []);

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
  const openBrowse = useCallback((collection: string) => {
    const id = `browse:${collection}`;
    openTab({ id, kind: 'browse', title: collection, collection } as Tab);
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
    notifyChanged,
  }), [setTabStatus, openInspector, closeTab, refreshTab, notifyChanged]);

  const activeTab = tabs.find(t => t.id === activeId);

  async function onFlush() {
    try { await flushDb(); refreshExplorer(); } catch (e) { /* swallow */ }
  }

  return (
    <div className="studio">
      {/* Toolbar */}
      <div className="studio-toolbar">
        <button onClick={newQuery} title="New SQL query tab">＋ New Query</button>
        <button onClick={refreshExplorer} title="Refresh tree">⟲ Refresh</button>
        <span className="sep" />
        <button onClick={onFlush} title="Flush dirty pages to disk">⤓ Flush</button>
        <span className="spacer" />
        <span className="endpoint">{status.endpoint ?? '…'}</span>
      </div>

      {/* Explorer (left) */}
      <Explorer
        refreshKey={explorerKey}
        onRefresh={refreshExplorer}
        onOpenBrowse={openBrowse}
        onOpenIndexes={openIndexes}
        onSetStatusMeta={setStatusMeta}
      />

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
              {tabs.map(t => (
                <div
                  key={t.id}
                  className={`tab ${t.id === activeId ? 'active' : ''}`}
                  onClick={() => setActiveId(t.id)}
                >
                  <span className="icon">{tabIcon(t.kind)}</span>
                  <span>{t.title}</span>
                  <span className="close" onClick={e => { e.stopPropagation(); closeTab(t.id); }} title="Close">×</span>
                </div>
              ))}
            </div>
            <div className="tab-body" key={activeTab?.id}>
              {activeTab && renderTab(activeTab, tabCtx)}
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

function renderTab(t: Tab, ctx: TabContext) {
  switch (t.kind) {
    case 'query':     return <QueryTab tab={t} ctx={ctx} />;
    case 'browse':    return <BrowseTab tab={t} ctx={ctx} />;
    case 'inspector': return <InspectorTab tab={t} ctx={ctx} />;
    case 'indexes':   return <IndexesTab tab={t} ctx={ctx} />;
  }
}
