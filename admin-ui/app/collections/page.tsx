'use client';

import { useEffect, useState } from 'react';
import { getStats, query, createIndex } from '@/lib/api';

export default function CollectionsPage() {
  const [stats, setStats] = useState<any>(null);
  const [selected, setSelected] = useState<string | null>(null);
  const [docs, setDocs] = useState<any[]>([]);
  const [newIdxPath, setNewIdxPath] = useState('');
  const [newIdxUnique, setNewIdxUnique] = useState(false);

  useEffect(() => { getStats().then(setStats); }, []);

  async function loadDocs(name: string) {
    setSelected(name);
    const r = await query(`SELECT * FROM ${name} LIMIT 50`);
    setDocs(r.documents || []);
  }

  async function addIndex() {
    if (!selected || !newIdxPath) return;
    const idxName = `idx_${selected}_${newIdxPath.replace(/[.\[\]]/g, '_')}`;
    await createIndex(selected, newIdxPath, idxName, newIdxUnique);
    setNewIdxPath('');
    setNewIdxUnique(false);
    setStats(await getStats());
  }

  const collection = stats?.collections?.find((c: any) => c.name === selected);

  return (
    <>
      <div className="eyebrow">Collections</div>
      <h1 className="page-title">Browse &amp; manage</h1>

      <div className="grid grid-2" style={{ gridTemplateColumns: '280px 1fr' }}>
        <div className="card">
          <h3>Collections</h3>
          {stats?.collections?.length === 0 && <p style={{ color: 'var(--gray-500)' }}>None yet</p>}
          {stats?.collections?.map((c: any) => (
            <div
              key={c.name}
              onClick={() => loadDocs(c.name)}
              style={{
                padding: '10px 12px', cursor: 'pointer',
                background: selected === c.name ? 'var(--gray-100)' : 'transparent',
                borderLeft: selected === c.name ? '3px solid var(--red)' : '3px solid transparent',
                marginLeft: -12, marginRight: -12
              }}
            >
              <div style={{ fontWeight: 600 }}>{c.name}</div>
              <div style={{ fontSize: 12, color: 'var(--gray-500)', fontFamily: 'var(--mono)' }}>
                {c.documentCount?.toLocaleString()} docs · {c.indexes?.length || 0} indexes
              </div>
            </div>
          ))}
        </div>

        <div>
          {selected && collection && (
            <>
              <div className="card">
                <h3>Indexes on {selected}</h3>
                {collection.indexes?.length === 0 ? (
                  <p style={{ color: 'var(--gray-500)' }}>No indexes yet</p>
                ) : (
                  <table className="table">
                    <thead><tr><th>Name</th><th>Path</th><th>Entries</th><th></th></tr></thead>
                    <tbody>
                      {collection.indexes?.map((i: any) => (
                        <tr key={i.name}>
                          <td>{i.name}</td>
                          <td>{i.path}</td>
                          <td>{i.entries?.toLocaleString?.() || i.entryCount?.toLocaleString?.() || '-'}</td>
                          <td>{i.unique && <span className="pill red">UNIQUE</span>}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                )}
                <div className="toolbar" style={{ marginTop: 16 }}>
                  <input
                    type="text"
                    placeholder="JSON path (e.g. passenger.lastName)"
                    value={newIdxPath}
                    onChange={(e) => setNewIdxPath(e.target.value)}
                    style={{ flex: 1 }}
                  />
                  <label style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: 13 }}>
                    <input type="checkbox" checked={newIdxUnique} onChange={(e) => setNewIdxUnique(e.target.checked)} />
                    Unique
                  </label>
                  <button className="btn" onClick={addIndex} disabled={!newIdxPath}>Add index</button>
                </div>
              </div>

              <div className="card">
                <h3>First {docs.length} document(s)</h3>
                {docs.length === 0 ? <p style={{ color: 'var(--gray-500)' }}>No documents.</p> :
                  <pre className="code-block">{JSON.stringify(docs, null, 2)}</pre>}
              </div>
            </>
          )}
          {!selected && <p style={{ color: 'var(--gray-500)' }}>Pick a collection on the left</p>}
        </div>
      </div>
    </>
  );
}
