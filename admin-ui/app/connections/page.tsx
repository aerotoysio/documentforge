'use client';

import { useMemo, useState } from 'react';
import { useConnections } from '@/lib/connections-context';
import { getHealth } from '@/lib/api';
import { discoverNetwork, normalizeUrl, type DiscoveredNode, type DiscoveryResult } from '@/lib/discovery';
import type { Connection } from '@/lib/connections';

const PALETTE = ['#d90429', '#ef476f', '#f59e0b', '#10b981', '#3b82f6', '#8b5cf6', '#ec4899', '#0ea5e9'];

export default function ConnectionsPage() {
  const { connections, active, setActive, add, update, remove } = useConnections();
  const [draft, setDraft] = useState<Partial<Connection>>({ name: '', baseUrl: '', apiKey: '', color: PALETTE[0] });
  const [editingId, setEditingId] = useState<string | null>(null);
  const [testing, setTesting] = useState<Record<string, { ok?: boolean; msg?: string; pinging?: boolean }>>({});

  // Discover-network panel state
  const [discoverSeedUrl, setDiscoverSeedUrl] = useState('');
  const [discoverApiKey, setDiscoverApiKey] = useState('');
  const [discovering, setDiscovering] = useState(false);
  const [discovery, setDiscovery] = useState<DiscoveryResult | null>(null);
  const [discoverError, setDiscoverError] = useState<string | null>(null);
  const [importChecks, setImportChecks] = useState<Set<string>>(new Set());

  const existingBaseUrls = useMemo(() => new Set(connections.map(c => normalizeUrl(c.baseUrl))), [connections]);

  function startEdit(c: Connection) {
    setEditingId(c.id);
    setDraft({ name: c.name, baseUrl: c.baseUrl, apiKey: c.apiKey ?? '', color: c.color, tags: c.tags, shard: c.shard ?? '' });
  }

  function cancelEdit() {
    setEditingId(null);
    setDraft({ name: '', baseUrl: '', apiKey: '', color: PALETTE[0], shard: '' });
  }

  function save() {
    if (!draft.name || !draft.baseUrl) return;
    const shard = (draft.shard || '').trim() || undefined;
    if (editingId) {
      update(editingId, { ...draft, shard });
    } else {
      add({
        name: draft.name!,
        baseUrl: draft.baseUrl!,
        apiKey: draft.apiKey || undefined,
        color: draft.color,
        tags: draft.tags,
        shard,
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

  async function runDiscover() {
    if (!discoverSeedUrl.trim()) return;
    setDiscovering(true);
    setDiscoverError(null);
    setDiscovery(null);
    try {
      const result = await discoverNetwork(discoverSeedUrl, discoverApiKey || undefined);
      setDiscovery(result);
      // Default-check anything reachable + auth-OK that isn't already registered.
      const initial = new Set<string>();
      for (const n of result.nodes) {
        if (n.reachable && n.authOk && !existingBaseUrls.has(normalizeUrl(n.baseUrl))) {
          initial.add(n.baseUrl);
        }
      }
      setImportChecks(initial);
    } catch (e: any) {
      setDiscoverError(e?.message || String(e));
    } finally {
      setDiscovering(false);
    }
  }

  function toggleImport(baseUrl: string) {
    setImportChecks(prev => {
      const next = new Set(prev);
      if (next.has(baseUrl)) next.delete(baseUrl); else next.add(baseUrl);
      return next;
    });
  }

  function importSelected() {
    if (!discovery || importChecks.size === 0) return;
    // Group nodes by shard leader so we can derive a shard label per node.
    // Leader's own URL is the natural shard key — short, stable, and humans
    // can rename in the form afterward.
    for (const node of discovery.nodes) {
      if (!importChecks.has(node.baseUrl)) continue;
      if (existingBaseUrls.has(normalizeUrl(node.baseUrl))) continue; // safety: don't dupe
      const shard = node.shardLeaderUrl ? shardLabelFromLeaderUrl(node.shardLeaderUrl) : undefined;
      add({
        name: node.status?.node || hostFromBaseUrl(node.baseUrl) || node.baseUrl,
        baseUrl: node.baseUrl,
        apiKey: discoverApiKey || undefined,
        color: PALETTE[0],
        shard,
      });
    }
    setDiscovery(null);
    setImportChecks(new Set());
    setDiscoverSeedUrl('');
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

      {/* Discover network */}
      <div className="card" style={{ borderTop: '3px solid var(--red)', marginBottom: 24 }}>
        <h3 style={{ marginTop: 0 }}>Discover network</h3>
        <p style={{ fontSize: 13, color: 'var(--gray-500)', marginTop: -8 }}>
          Paste any node's URL + key — we'll walk the replication graph and find every peer in
          its shard. Works best when seeded with a leader. For multi-shard clusters, run discovery
          once per shard.
        </p>
        <div className="grid grid-2" style={{ gap: 16 }}>
          <div>
            <div className="key">Seed URL</div>
            <input
              type="text"
              placeholder="http://leader.example.com:5000"
              value={discoverSeedUrl}
              onChange={e => setDiscoverSeedUrl(e.target.value)}
              disabled={discovering}
            />
          </div>
          <div>
            <div className="key">API key (optional)</div>
            <input
              type="text"
              placeholder="bearer token, leave blank for unauthenticated nodes"
              value={discoverApiKey}
              onChange={e => setDiscoverApiKey(e.target.value)}
              disabled={discovering}
            />
          </div>
        </div>
        <div className="toolbar" style={{ marginTop: 14 }}>
          <button
            onClick={runDiscover}
            disabled={!discoverSeedUrl.trim() || discovering}
            style={{ background: 'var(--red)', color: 'white', border: 'none', padding: '8px 18px', fontWeight: 700, cursor: discovering ? 'wait' : 'pointer' }}
          >
            {discovering ? 'Walking the graph…' : '⌕ Discover'}
          </button>
          {discovery && (
            <button
              onClick={() => { setDiscovery(null); setImportChecks(new Set()); setDiscoverError(null); }}
              style={{ background: 'transparent', color: 'var(--gray-500)', border: '1px solid var(--gray-200)', padding: '8px 18px', cursor: 'pointer' }}
            >
              Clear
            </button>
          )}
        </div>

        {discoverError && (
          <div style={{ marginTop: 14, padding: '10px 12px', background: 'rgba(217,4,41,0.08)', color: 'var(--red)', fontFamily: 'var(--mono)', fontSize: 12, borderLeft: '3px solid var(--red)' }}>
            ✗ {discoverError}
          </div>
        )}

        {discovery && <DiscoveryResultsTable
          result={discovery}
          existingBaseUrls={existingBaseUrls}
          checks={importChecks}
          onToggle={toggleImport}
          onImport={importSelected}
        />}
      </div>

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
          <div>
            <div className="setting-row" style={{ borderBottom: 'none', display: 'block', padding: 0 }}>
              <div className="key">Shard <span style={{ color: 'var(--gray-500)', fontWeight: 400 }}>(optional — groups nodes in the Swarm topology view)</span></div>
              <input
                type="text"
                placeholder='e.g. "shard-a", "orders", "eu-west"'
                value={draft.shard || ''}
                onChange={e => setDraft(d => ({ ...d, shard: e.target.value }))}
              />
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
                      {c.shard && <span className="pill gray" style={{ marginLeft: 6 }}>shard: {c.shard}</span>}
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

// ----------------------------------------------------------------------------
// Discovery preview table
// ----------------------------------------------------------------------------

interface DiscoveryResultsTableProps {
  result: DiscoveryResult;
  existingBaseUrls: Set<string>;
  checks: Set<string>;
  onToggle: (baseUrl: string) => void;
  onImport: () => void;
}

function DiscoveryResultsTable({ result, existingBaseUrls, checks, onToggle, onImport }: DiscoveryResultsTableProps) {
  const importable = result.nodes.filter(n => n.reachable && n.authOk && !existingBaseUrls.has(normalizeUrl(n.baseUrl)));
  const total = result.nodes.length;

  return (
    <div style={{ marginTop: 18 }}>
      <div style={{ fontSize: 13, color: 'var(--gray-500)', marginBottom: 8 }}>
        Found <strong style={{ color: 'var(--ink)' }}>{total}</strong> node{total === 1 ? '' : 's'} ·{' '}
        <strong style={{ color: 'var(--ink)' }}>{importable.length}</strong> importable ·{' '}
        <strong style={{ color: 'var(--ink)' }}>{checks.size}</strong> selected
      </div>
      <table className="table" style={{ width: '100%', fontSize: 13 }}>
        <thead>
          <tr>
            <th style={{ width: 32 }}></th>
            <th>Node</th>
            <th>Role</th>
            <th>URL</th>
            <th>Source</th>
            <th>Status</th>
          </tr>
        </thead>
        <tbody>
          {result.nodes.map(node => {
            const already = existingBaseUrls.has(normalizeUrl(node.baseUrl));
            const checkable = node.reachable && node.authOk && !already;
            const checked = checks.has(node.baseUrl);
            return (
              <tr key={node.baseUrl} style={{ opacity: checkable ? 1 : 0.55 }}>
                <td>
                  <input
                    type="checkbox"
                    disabled={!checkable}
                    checked={checked}
                    onChange={() => onToggle(node.baseUrl)}
                  />
                </td>
                <td style={{ fontWeight: 600 }}>{node.status?.node || hostFromBaseUrl(node.baseUrl) || '—'}</td>
                <td>
                  {node.status?.role
                    ? <span className={`pill ${node.status.role === 'leader' ? 'green' : 'gray'}`}>{node.status.role}</span>
                    : <span style={{ color: 'var(--gray-500)' }}>—</span>}
                  {node.status?.readOnly && <span className="pill red" style={{ marginLeft: 4 }}>RO</span>}
                </td>
                <td style={{ fontFamily: 'var(--mono)', fontSize: 11, wordBreak: 'break-all' }}>{node.baseUrl}</td>
                <td>
                  <span className="pill gray" style={{ fontSize: 10 }}>{node.source}</span>
                  {node.source === 'port-guess' && (
                    <span style={{ color: 'var(--gray-500)', fontSize: 10, marginLeft: 6 }}>verify port</span>
                  )}
                </td>
                <td>
                  {already ? (
                    <span className="pill gray">already registered</span>
                  ) : node.reachable && node.authOk ? (
                    <span style={{ color: 'var(--green)', fontSize: 12 }}>✓ ok ({node.rttMs} ms)</span>
                  ) : node.reachable && !node.authOk ? (
                    <span style={{ color: 'var(--red)', fontSize: 12 }}>✗ auth failed</span>
                  ) : (
                    <span style={{ color: 'var(--red)', fontSize: 12 }}>✗ unreachable</span>
                  )}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
      <div className="toolbar" style={{ marginTop: 14 }}>
        <button
          onClick={onImport}
          disabled={checks.size === 0}
          style={{ background: 'var(--ink)', color: 'white', border: 'none', padding: '8px 18px', fontWeight: 700, cursor: checks.size === 0 ? 'not-allowed' : 'pointer', opacity: checks.size === 0 ? 0.5 : 1 }}
        >
          Import {checks.size} selected
        </button>
      </div>
    </div>
  );
}

// ----------------------------------------------------------------------------
// Small helpers
// ----------------------------------------------------------------------------

function hostFromBaseUrl(u: string): string | null {
  try { return new URL(u).hostname; } catch { return null; }
}

/** Pick a short, stable shard label from the leader's URL — host:port works
 * well as a default; users can rename via the connection form afterwards. */
function shardLabelFromLeaderUrl(leaderUrl: string): string {
  try {
    const u = new URL(leaderUrl);
    return u.port ? `${u.hostname}:${u.port}` : u.hostname;
  } catch {
    return leaderUrl;
  }
}
