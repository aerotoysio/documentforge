'use client';

import { useState } from 'react';
import { useConnections } from '@/lib/connections-context';
import { getHealth } from '@/lib/api';
import type { Connection } from '@/lib/connections';

const PALETTE = ['#d90429', '#ef476f', '#f59e0b', '#10b981', '#3b82f6', '#8b5cf6', '#ec4899', '#0ea5e9'];

export default function ConnectionsPage() {
  const { connections, active, setActive, add, update, remove } = useConnections();
  const [draft, setDraft] = useState<Partial<Connection>>({ name: '', baseUrl: '', apiKey: '', color: PALETTE[0] });
  const [editingId, setEditingId] = useState<string | null>(null);
  const [testing, setTesting] = useState<Record<string, { ok?: boolean; msg?: string; pinging?: boolean }>>({});

  function startEdit(c: Connection) {
    setEditingId(c.id);
    setDraft({ name: c.name, baseUrl: c.baseUrl, apiKey: c.apiKey ?? '', color: c.color, tags: c.tags });
  }

  function cancelEdit() {
    setEditingId(null);
    setDraft({ name: '', baseUrl: '', apiKey: '', color: PALETTE[0] });
  }

  function save() {
    if (!draft.name || !draft.baseUrl) return;
    if (editingId) {
      update(editingId, draft);
    } else {
      add({
        name: draft.name!,
        baseUrl: draft.baseUrl!,
        apiKey: draft.apiKey || undefined,
        color: draft.color,
        tags: draft.tags,
      });
    }
    cancelEdit();
  }

  async function ping(c: Connection) {
    setTesting(prev => ({ ...prev, [c.id]: { pinging: true } }));
    try {
      const r = await getHealth({ connection: c });
      setTesting(prev => ({ ...prev, [c.id]: { ok: true, msg: `${r.node} · v${r.version} · ${r.uptimeSeconds?.toFixed?.(0)}s up` } }));
    } catch (e: any) {
      setTesting(prev => ({ ...prev, [c.id]: { ok: false, msg: e.message || String(e) } }));
    }
  }

  function copy(c: Connection) {
    add({
      name: c.name + ' (copy)',
      baseUrl: c.baseUrl,
      apiKey: c.apiKey,
      color: c.color,
      tags: c.tags,
    });
  }

  return (
    <>
      <div className="eyebrow">Setup</div>
      <h1 className="page-title">Connections</h1>
      <p style={{ maxWidth: 720, color: 'var(--gray-500)', marginBottom: 32 }}>
        Register every DocumentForge endpoint you want to manage from this UI — your dev box, prod
        on Render, individual cluster shards, replication followers. Switch between them from the
        sidebar dropdown. Run swarm-wide commands from <a href="/swarm">Swarm</a>.
      </p>

      {/* Add / edit form */}
      <div className="card">
        <h3>{editingId ? 'Edit connection' : 'Add connection'}</h3>
        <div className="grid grid-2" style={{ gap: 16, marginTop: 12 }}>
          <div>
            <div className="setting-row" style={{ borderBottom: 'none', display: 'block', padding: 0 }}>
              <div className="key">Name</div>
              <input
                type="text"
                placeholder='e.g. "Production" or "shard-a"'
                value={draft.name || ''}
                onChange={e => setDraft(d => ({ ...d, name: e.target.value }))}
              />
            </div>
          </div>
          <div>
            <div className="setting-row" style={{ borderBottom: 'none', display: 'block', padding: 0 }}>
              <div className="key">Base URL</div>
              <input
                type="text"
                placeholder="https://documentforge.onrender.com"
                value={draft.baseUrl || ''}
                onChange={e => setDraft(d => ({ ...d, baseUrl: e.target.value }))}
              />
            </div>
          </div>
          <div>
            <div className="setting-row" style={{ borderBottom: 'none', display: 'block', padding: 0 }}>
              <div className="key">API key (optional)</div>
              <input
                type="text"
                placeholder="bearer token, leave blank if --api-key not set"
                value={draft.apiKey || ''}
                onChange={e => setDraft(d => ({ ...d, apiKey: e.target.value }))}
              />
            </div>
          </div>
          <div>
            <div className="setting-row" style={{ borderBottom: 'none', display: 'block', padding: 0 }}>
              <div className="key">Color tag</div>
              <div style={{ display: 'flex', gap: 8, marginTop: 6 }}>
                {PALETTE.map(c => (
                  <button
                    key={c}
                    onClick={() => setDraft(d => ({ ...d, color: c }))}
                    style={{
                      width: 24, height: 24, border: 'none', borderRadius: '50%',
                      background: c, cursor: 'pointer',
                      outline: draft.color === c ? '2px solid var(--ink)' : 'none',
                      outlineOffset: 2,
                    }}
                  />
                ))}
              </div>
            </div>
          </div>
        </div>
        <div className="toolbar" style={{ marginTop: 16 }}>
          <button className="btn" onClick={save} disabled={!draft.name || !draft.baseUrl}
            style={{ background: 'var(--red)', color: 'white', border: 'none', padding: '8px 18px', fontWeight: 700, cursor: 'pointer' }}>
            {editingId ? 'Save changes' : '+ Add connection'}
          </button>
          {editingId && (
            <button className="btn" onClick={cancelEdit}
              style={{ background: 'transparent', color: 'var(--gray-500)', border: '1px solid var(--gray-200)', padding: '8px 18px', cursor: 'pointer' }}>
              Cancel
            </button>
          )}
        </div>
      </div>

      {/* Existing connections */}
      <h2>Registered connections ({connections.length})</h2>
      {connections.length === 0 ? (
        <div className="card"><p style={{ color: 'var(--gray-500)' }}>None yet. Add one above to get started.</p></div>
      ) : (
        <div className="grid" style={{ gap: 12 }}>
          {connections.map(c => {
            const t = testing[c.id];
            const isActive = c.id === active?.id;
            return (
              <div key={c.id} className="card" style={{ borderLeft: `4px solid ${c.color || 'var(--red)'}` }}>
                <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
                  <span style={{ display: 'inline-block', width: 12, height: 12, borderRadius: '50%', background: c.color || 'var(--red)' }} />
                  <div style={{ flex: 1 }}>
                    <div style={{ fontSize: 16, fontWeight: 700 }}>
                      {c.name}
                      {isActive && <span className="pill green" style={{ marginLeft: 10 }}>active</span>}
                      {c.apiKey && <span className="pill gray" style={{ marginLeft: 6 }}>auth</span>}
                    </div>
                    <div style={{ fontFamily: 'var(--mono)', fontSize: 12, color: 'var(--gray-500)' }}>{c.baseUrl}</div>
                  </div>
                  <div style={{ display: 'flex', gap: 6 }}>
                    {!isActive && (
                      <button onClick={() => setActive(c.id)}
                        style={{ background: 'var(--ink)', color: 'white', border: 'none', padding: '6px 12px', fontSize: 12, fontWeight: 600, cursor: 'pointer' }}>
                        Make active
                      </button>
                    )}
                    <button onClick={() => ping(c)} disabled={t?.pinging}
                      style={{ background: 'transparent', color: 'var(--ink)', border: '1px solid var(--gray-200)', padding: '6px 12px', fontSize: 12, cursor: 'pointer' }}>
                      {t?.pinging ? 'Pinging…' : 'Ping'}
                    </button>
                    <button onClick={() => startEdit(c)}
                      style={{ background: 'transparent', color: 'var(--ink)', border: '1px solid var(--gray-200)', padding: '6px 12px', fontSize: 12, cursor: 'pointer' }}>
                      Edit
                    </button>
                    <button onClick={() => copy(c)}
                      style={{ background: 'transparent', color: 'var(--ink)', border: '1px solid var(--gray-200)', padding: '6px 12px', fontSize: 12, cursor: 'pointer' }}>
                      Copy
                    </button>
                    <button onClick={() => { if (confirm(`Remove "${c.name}"?`)) remove(c.id); }}
                      style={{ background: 'transparent', color: 'var(--red)', border: '1px solid var(--red)', padding: '6px 12px', fontSize: 12, cursor: 'pointer' }}>
                      Remove
                    </button>
                  </div>
                </div>
                {t?.msg && (
                  <div style={{
                    marginTop: 10, padding: '8px 12px', fontFamily: 'var(--mono)', fontSize: 12,
                    background: t.ok ? 'rgba(16,185,129,0.08)' : 'rgba(217,4,41,0.08)',
                    color: t.ok ? 'var(--green)' : 'var(--red)',
                    borderLeft: `3px solid ${t.ok ? 'var(--green)' : 'var(--red)'}`,
                  }}>
                    {t.ok ? '✓ ' : '✗ '}{t.msg}
                  </div>
                )}
              </div>
            );
          })}
        </div>
      )}
    </>
  );
}
