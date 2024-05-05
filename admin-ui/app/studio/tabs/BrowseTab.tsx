'use client';

import { useEffect, useState } from 'react';
import { listDocs } from '@/lib/api';
import type { TabContext } from '../studio-types';

export function BrowseTab({ tab, ctx }: { tab: any; ctx: TabContext }) {
  const [docs, setDocs] = useState<any[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [limit, setLimit] = useState(100);
  const [filter, setFilter] = useState('');
  const collection: string = tab.collection;

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    listDocs(collection, limit)
      .then(r => { if (!cancelled) { setDocs(r.documents ?? []); ctx.setStatus({ rows: r.count ?? 0, plan: 'COLLECTION_SCAN', executionMs: undefined }); } })
      .catch(e => { if (!cancelled) setError(e.message || String(e)); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [collection, limit, tab.refreshKey]);

  const visible = filter
    ? docs.filter(d => JSON.stringify(d).toLowerCase().includes(filter.toLowerCase()))
    : docs;

  const columns = visible.length ? Object.keys(visible[0]) : [];

  return (
    <div className="browse-tab">
      <div className="browse-toolbar">
        <strong style={{ fontFamily: 'var(--mono)', fontSize: 12 }}>{collection}</strong>
        <input
          placeholder="Client-side filter…"
          value={filter}
          onChange={e => setFilter(e.target.value)}
        />
        <label style={{ fontSize: 12, color: 'var(--gray-500)' }}>
          Limit:
          <select
            value={limit}
            onChange={e => setLimit(Number(e.target.value))}
            style={{ marginLeft: 6, fontFamily: 'var(--mono)', fontSize: 12 }}
          >
            {[50, 100, 500, 1000].map(n => <option key={n} value={n}>{n}</option>)}
          </select>
        </label>
        <button
          onClick={() => ctx.refreshTab(tab.id)}
          style={{ background: 'transparent', border: '1px solid var(--studio-line-strong)', padding: '4px 10px', cursor: 'pointer', fontSize: 12, borderRadius: 3 }}
        >
          ⟲ Refresh
        </button>
        <span className="count">{visible.length} of {docs.length} docs</span>
      </div>
      <div className="results">
        {loading && <div className="empty">Loading…</div>}
        {error && <div className="err">{error}</div>}
        {!loading && !error && visible.length === 0 && (
          <div className="empty">{docs.length === 0 ? 'Collection is empty.' : 'Nothing matches the filter.'}</div>
        )}
        {!loading && !error && visible.length > 0 && (
          <table>
            <thead>
              <tr>
                {columns.map(c => <th key={c}>{c}</th>)}
              </tr>
            </thead>
            <tbody>
              {visible.map((d, i) => (
                <tr key={d._id ?? i} onDoubleClick={() => ctx.openInspector(collection, d)} title="Double-click to inspect / edit">
                  {columns.map(c => <td key={c} title={cellText(d[c])}>{cellRender(d[c])}</td>)}
                </tr>
              ))}
            </tbody>
          </table>
        )}
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
