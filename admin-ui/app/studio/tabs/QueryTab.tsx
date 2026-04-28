'use client';

import { useState } from 'react';
import { query as runQuery } from '@/lib/api';
import type { TabContext } from '../studio-types';

export function QueryTab({ tab, ctx }: { tab: any; ctx: TabContext }) {
  const [sql, setSql] = useState<string>(tab.initialSql ?? 'SELECT * FROM orders LIMIT 50');
  const [result, setResult] = useState<any | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [running, setRunning] = useState(false);

  async function execute() {
    setRunning(true);
    setError(null);
    try {
      const r = await runQuery(sql);
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
    <div className="query-tab">
      <textarea
        className="sql-editor"
        value={sql}
        onChange={e => setSql(e.target.value)}
        onKeyDown={onKeyDown}
        spellCheck={false}
        placeholder="SELECT * FROM orders LIMIT 100"
      />
      <div>
        <div className="query-toolbar">
          <button className="run-btn" onClick={execute} disabled={running}>
            {running ? 'Running…' : '▶  Run  (Ctrl+Enter)'}
          </button>
          {result?.plan && (
            <span className="plan">
              {result.plan} · {result.executionTimeMs?.toFixed(2)}ms · {result.count ?? 0} row{result.count === 1 ? '' : 's'}
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
