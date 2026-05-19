'use client';

import { useEffect, useRef, useState } from 'react';
import { query as runQuery, listDatabases } from '@/lib/api';
import type { Connection } from '@/lib/connections';
import type { TabContext } from '../studio-types';

const SQL_HEIGHT_KEY = 'dfdb_studio_query_sql_height';

export function QueryTab({ tab, ctx, connection }: { tab: any; ctx: TabContext; connection: Connection | null }) {
  const [sql, setSql] = useState<string>(tab.initialSql ?? 'SELECT * FROM orders LIMIT 50');
  const [result, setResult] = useState<any | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [running, setRunning] = useState(false);

  // Issue #66 Phase 3a — per-tab DB picker. Empty string = "use the
  // service's default DB" (the back-compat flat /query route). Anything
  // else routes through /db/{name}/query so each Studio tab can target
  // a different attached database without flipping the service default.
  const [availableDbs, setAvailableDbs] = useState<string[]>([]);
  const [defaultDb, setDefaultDb] = useState<string | null>(null);
  const [selectedDb, setSelectedDb] = useState<string>('');
  const [dbsAvailable, setDbsAvailable] = useState(false);

  useEffect(() => {
    if (!connection) { setAvailableDbs([]); setDefaultDb(null); setDbsAvailable(false); return; }
    let cancelled = false;
    listDatabases({ connection })
      .then(r => {
        if (cancelled) return;
        setAvailableDbs(r.databases.map(d => d.name));
        setDefaultDb(r.default);
        setDbsAvailable(true);
      })
      .catch(() => {
        // Service predates multi-DB or is unreachable. Hide the picker;
        // the tab silently uses the flat /query route as before.
        if (cancelled) return;
        setAvailableDbs([]);
        setDefaultDb(null);
        setDbsAvailable(false);
      });
    return () => { cancelled = true; };
  }, [connection?.id, connection?.baseUrl]);

  // Resizable SQL/results split — value stored in pixels, persisted globally
  // (all QueryTabs share the same default split so muscle memory holds across tabs).
  const containerRef = useRef<HTMLDivElement>(null);
  const [sqlHeight, setSqlHeight] = useState<number | null>(null);
  useEffect(() => {
    const saved = window.localStorage.getItem(SQL_HEIGHT_KEY);
    if (saved) {
      const n = parseInt(saved, 10);
      if (Number.isFinite(n) && n >= 60) setSqlHeight(n);
    }
  }, []);

  function startRowResize(e: React.MouseEvent) {
    if (!containerRef.current) return;
    e.preventDefault();
    const handle = e.currentTarget as HTMLElement;
    handle.classList.add('dragging');
    const rect = containerRef.current.getBoundingClientRect();
    const startY = e.clientY;
    const startHeight = sqlHeight ?? Math.round(rect.height * 0.35);
    function onMove(ev: MouseEvent) {
      const max = (containerRef.current?.getBoundingClientRect().height ?? 600) - 100;
      const next = Math.max(60, Math.min(max, startHeight + (ev.clientY - startY)));
      setSqlHeight(next);
    }
    function onUp() {
      handle.classList.remove('dragging');
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
      // Read final height from state via ref-trick: read from inline style.
      const styleVal = containerRef.current?.style.getPropertyValue('--query-sql-height');
      if (styleVal) window.localStorage.setItem(SQL_HEIGHT_KEY, styleVal.replace('px', ''));
    }
    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
  }

  async function execute() {
    setRunning(true);
    setError(null);
    try {
      const r = await runQuery(sql, { connection, database: selectedDb || undefined });
      setResult(r);
      ctx.setStatus({
        plan: r.plan,
        executionMs: r.executionTimeMs,
        rows: r.count,
        affected: r.affected,
      });
    } catch (e: any) {
      setError(e.message || String(e));
      setResult(null);
      ctx.setStatus({ plan: undefined, executionMs: undefined, rows: 0 });
    } finally {
      setRunning(false);
    }
  }

  function onKeyDown(e: React.KeyboardEvent<HTMLTextAreaElement>) {
    if ((e.key === 'Enter' && (e.ctrlKey || e.metaKey)) || e.key === 'F5') {
      e.preventDefault();
      execute();
    }
  }

  function inspectRow(doc: any) {
    if (!doc?._id) return;
    // Best effort: figure out which collection if the SQL says "FROM x".
    const m = sql.match(/FROM\s+([a-zA-Z_][a-zA-Z0-9_.]*)/i);
    const coll = m?.[1];
    if (coll) ctx.openInspector(coll, doc);
  }

  const docs = result?.documents ?? [];
  const columns = docs.length ? Object.keys(docs[0]) : [];

  return (
    <div
      className="query-tab"
      ref={containerRef}
      style={sqlHeight ? { ['--query-sql-height' as any]: sqlHeight + 'px' } : undefined}
    >
      <textarea
        className="sql-editor"
        value={sql}
        onChange={e => setSql(e.target.value)}
        onKeyDown={onKeyDown}
        spellCheck={false}
        placeholder="SELECT * FROM orders LIMIT 100"
      />
      <div className="splitter-row" onMouseDown={startRowResize} title="Drag to resize" />
      <div>
        <div className="query-toolbar">
          <button className="run-btn" onClick={execute} disabled={running}>
            {running ? 'Running…' : '▶  Run  (Ctrl+Enter)'}
          </button>
          {dbsAvailable && availableDbs.length > 0 && (
            <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6, marginLeft: 10 }}>
              <span style={{ fontFamily: 'var(--mono)', fontSize: 10, letterSpacing: '0.08em', textTransform: 'uppercase', color: 'var(--gray-500)', fontWeight: 700 }}>
                DB
              </span>
              <select
                value={selectedDb}
                onChange={e => setSelectedDb(e.target.value)}
                title="Target database for this query. Empty = use the service's default DB."
                style={{ padding: '3px 6px', fontSize: 12, border: '1px solid var(--gray-200)', background: 'white', fontFamily: 'var(--mono)' }}
              >
                <option value="">
                  (default{defaultDb ? `: ${defaultDb}` : ''})
                </option>
                {availableDbs.map(name => (
                  <option key={name} value={name}>{name}</option>
                ))}
              </select>
            </span>
          )}
          {result?.plan && (
            <span className="plan">
              {result.plan} · {result.executionTimeMs?.toFixed(2)}ms · {result.count ?? 0} row{result.count === 1 ? '' : 's'}
              {result.database && (
                <span style={{ marginLeft: 6, fontFamily: 'var(--mono)', color: 'var(--red)', fontWeight: 600 }}>
                  · {result.database}
                </span>
              )}
            </span>
          )}
        </div>
        <div className="results">
          {error && <div className="err">{error}</div>}
          {!error && !result && <div className="empty">Run a query to see results.</div>}
          {!error && result && docs.length === 0 && (
            <div className="empty">
              Query executed in {result.executionTimeMs?.toFixed(2)}ms · 0 rows · {result.plan}
              {result.affected != null && result.affected > 0 && ` · ${result.affected} affected`}
              {result.message && ` · ${result.message}`}
            </div>
          )}
          {!error && docs.length > 0 && (
            <table>
              <thead>
                <tr>
                  {columns.map(c => <th key={c}>{c}</th>)}
                </tr>
              </thead>
              <tbody>
                {docs.map((d: any, i: number) => (
                  <tr key={d._id ?? i} onDoubleClick={() => inspectRow(d)} title={d._id ? `Double-click to inspect _id ${d._id}` : ''}>
                    {columns.map(c => <td key={c} title={cellText(d[c])}>{cellRender(d[c])}</td>)}
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      </div>
    </div>
  );
}

function cellRender(v: any) {
  if (v === null || v === undefined) return <span className="nullv">null</span>;
  if (typeof v === 'object') return JSON.stringify(v);
  return String(v);
}

function cellText(v: any) {
  if (v === null || v === undefined) return 'null';
  if (typeof v === 'object') return JSON.stringify(v);
  return String(v);
}
