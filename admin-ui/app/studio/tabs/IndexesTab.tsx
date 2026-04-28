'use client';

import { useEffect, useState } from 'react';
import { getIndexes, createIndex, rebuildIndex, rebuildIndexes } from '@/lib/api';
import type { TabContext } from '../studio-types';

export function IndexesTab({ tab, ctx }: { tab: any; ctx: TabContext }) {
  const collection: string = tab.collection;
  const [indexes, setIndexes] = useState<any[]>([]);
  const [loading, setLoading] = useState(true);
  const [msg, setMsg] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null);

  // Create-index form
  const [path, setPath] = useState('');
  const [unique, setUnique] = useState(false);
  const [name, setName] = useState('');

  async function refresh() {
    setLoading(true);
    try {
      const r = await getIndexes(collection);
      setIndexes(r.indexes ?? []);
    } catch (e: any) { setMsg({ kind: 'err', text: e.message || String(e) }); }
    finally { setLoading(false); }
  }

  useEffect(() => { refresh(); /* eslint-disable-next-line */ }, [collection, tab.refreshKey]);

  async function add() {
    if (!path) return;
    const idxName = name || `idx_${collection}_${path.replace(/[.\[\]]/g, '_')}`;
    try {
      await createIndex(collection, path, idxName, unique);
      setPath(''); setUnique(false); setName('');
      setMsg({ kind: 'ok', text: `Created ${idxName}.` });
      ctx.notifyChanged(collection);
      await refresh();
    } catch (e: any) { setMsg({ kind: 'err', text: e.message || String(e) }); }
  }

  async function rebuild(idxName: string) {
    try {
      const r = await rebuildIndex(collection, idxName);
      setMsg({ kind: 'ok', text: `Rebuilt ${idxName} in ${r.timeMs?.toFixed?.(1) ?? '?'}ms.` });
    } catch (e: any) { setMsg({ kind: 'err', text: e.message || String(e) }); }
  }

  async function rebuildAll() {
    try {
      const r = await rebuildIndexes(collection);
      setMsg({ kind: 'ok', text: `Rebuilt ${r.indexesRebuilt} indexes in ${r.timeMs?.toFixed?.(1) ?? '?'}ms.` });
    } catch (e: any) { setMsg({ kind: 'err', text: e.message || String(e) }); }
  }

  return (
    <div className="indexes-tab">
      <div className="hdr-row">
        <h3>Indexes</h3>
        <span className="coll">on {collection}</span>
        <button
          onClick={rebuildAll}
          style={{ marginLeft: 'auto', fontSize: 12, padding: '5px 12px', border: '1px solid var(--studio-line-strong)', background: 'var(--studio-panel)', borderRadius: 3, cursor: 'pointer' }}
          title="Rebuild every index on this collection"
        >
          ⟲ Rebuild all
        </button>
      </div>

      <div className="add-form">
        <input type="text" placeholder="JSON path (e.g. passenger.lastName)" value={path} onChange={e => setPath(e.target.value)} style={{ flex: 1 }} />
        <input type="text" placeholder="Index name (auto if empty)" value={name} onChange={e => setName(e.target.value)} style={{ width: 220 }} />
        <label><input type="checkbox" checked={unique} onChange={e => setUnique(e.target.checked)} /> unique</label>
        <button onClick={add} disabled={!path}>+ Create</button>
      </div>

      {msg && (
        <div style={{ marginBottom: 12, fontFamily: 'var(--mono)', fontSize: 12, color: msg.kind === 'ok' ? '#10b981' : 'var(--red)' }}>
          {msg.text}
        </div>
      )}

      {loading ? (
        <div style={{ color: 'var(--gray-500)' }}>Loading…</div>
      ) : indexes.length === 0 ? (
        <div style={{ color: 'var(--gray-500)' }}>No indexes on this collection. Add one above.</div>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Name</th>
              <th>Path</th>
              <th>Type</th>
              <th>Actions</th>
            </tr>
          </thead>
          <tbody>
            {indexes.map(idx => (
              <tr key={idx.name}>
                <td>{idx.name}</td>
                <td>{idx.path}</td>
                <td>
                  <span className={`pill ${idx.unique ? 'unique' : 'normal'}`}>
                    {idx.unique ? 'unique' : 'b-tree'}
                  </span>
                </td>
                <td>
                  <div className="row-actions">
                    <button onClick={() => rebuild(idx.name)}>⟲ Rebuild</button>
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <p style={{ marginTop: 16, color: 'var(--gray-500)', fontSize: 12 }}>
        Drop is not exposed via the REST API yet — use SQL <code>DROP INDEX</code> from the Query tab.
      </p>
    </div>
  );
}
