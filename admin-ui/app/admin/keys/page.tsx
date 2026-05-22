'use client';

// Issue #73 Phase 7b — Studio API key management page.
//
// Mint / list / revoke the keys held in the connected service's
// _system.keys collection (via the /admin/keys REST surface that
// Phase 7a built). The flow mirrors GitHub's PAT page: generated
// secret is shown exactly once with a "copy now" warning, then
// it's redacted forever.
//
// All endpoints require the connection's admin Bearer (the legacy
// apiKey or any key carrying the * scope). Tenant-scoped keys hit
// 403 on every call here — that's working as designed.

import { useEffect, useMemo, useState } from 'react';
import { useConnections } from '@/lib/connections-context';
import {
  listAdminKeys,
  createAdminKey,
  deleteAdminKey,
  listDatabases,
  type AdminKeyInfo,
  type MintedKey,
  type DatabaseEntry,
} from '@/lib/api';

type ScopeMode = 'admin' | 'all-dbs' | 'single-db' | 'single-db-read' | 'custom';

export default function KeysPage() {
  const { active } = useConnections();

  const [keys, setKeys] = useState<AdminKeyInfo[]>([]);
  const [databases, setDatabases] = useState<DatabaseEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // Generate form state.
  const [showGenForm, setShowGenForm] = useState(false);
  const [scopeMode, setScopeMode] = useState<ScopeMode>('single-db');
  const [scopeDb, setScopeDb] = useState('');
  const [customScopes, setCustomScopes] = useState('');
  const [description, setDescription] = useState('');
  const [generating, setGenerating] = useState(false);
  const [genError, setGenError] = useState<string | null>(null);

  // Just-minted secret. Shown ONCE; cleared the next time the user
  // navigates away or generates another key.
  const [minted, setMinted] = useState<MintedKey | null>(null);
  const [copied, setCopied] = useState(false);

  // Loading + busy state per-row (Revoke button per row).
  const [revoking, setRevoking] = useState<string | null>(null);

  async function refresh() {
    if (!active) return;
    setLoading(true);
    setError(null);
    try {
      const [k, dbs] = await Promise.all([
        listAdminKeys({ connection: active }),
        listDatabases({ connection: active }).catch(() => ({ databases: [], default: null, count: 0 })),
      ]);
      setKeys(k.keys);
      setDatabases(dbs.databases);
    } catch (e: any) {
      setError(e.message || String(e));
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => { refresh(); /* eslint-disable-next-line */ }, [active?.id]);

  // Compute the actual scope strings from the picker state.
  const resolvedScopes = useMemo<string[]>(() => {
    switch (scopeMode) {
      case 'admin': return ['*'];
      case 'all-dbs': return ['db:*'];
      case 'single-db': return scopeDb ? [`db:${scopeDb}`] : [];
      case 'single-db-read': return scopeDb ? [`db:${scopeDb}:read`] : [];
      case 'custom':
        return customScopes
          .split(/[\n,]/)
          .map(s => s.trim())
          .filter(s => s.length > 0);
    }
  }, [scopeMode, scopeDb, customScopes]);

  async function handleGenerate(e?: React.FormEvent) {
    e?.preventDefault();
    if (resolvedScopes.length === 0) {
      setGenError('Pick at least one scope.');
      return;
    }
    setGenError(null);
    setGenerating(true);
    try {
      const result = await createAdminKey(
        resolvedScopes,
        description.trim() || null,
        { connection: active ?? undefined },
      );
      setMinted(result);
      setCopied(false);
      // Reset the form for the next mint.
      setShowGenForm(false);
      setDescription('');
      setCustomScopes('');
      // Refresh the list — the new key shows up at the bottom.
      await refresh();
    } catch (e: any) {
      setGenError(e.message || String(e));
    } finally {
      setGenerating(false);
    }
  }

  async function handleRevoke(key: AdminKeyInfo) {
    if (!confirm(
      `Revoke key ${key.id.slice(0, 8)}? Any app using it will get 401 on the next request.\n\n` +
      `Scopes: ${key.scopes.join(', ')}\nDescription: ${key.description ?? '(none)'}\n\n` +
      `This cannot be undone — the only path back is to mint a new key with the same scopes.`,
    )) return;
    setRevoking(key.id);
    try {
      await deleteAdminKey(key.id, { connection: active ?? undefined });
      await refresh();
    } catch (e: any) {
      alert(`Revoke failed: ${e.message || String(e)}`);
    } finally {
      setRevoking(null);
    }
  }

  function copySecret() {
    if (!minted) return;
    navigator.clipboard.writeText(minted.secret).then(
      () => { setCopied(true); setTimeout(() => setCopied(false), 2500); },
      () => alert('Copy failed — select + copy manually.'),
    );
  }

  if (!active) {
    return (
      <>
        <h1 className="page-title">API Keys</h1>
        <div className="card">
          <p>No connection selected. Pick one from the sidebar.</p>
        </div>
      </>
    );
  }

  return (
    <>
      <div className="eyebrow">{active.name}</div>
      <h1 className="page-title">
        API Keys
        <span style={{ color: 'var(--gray-500)', fontSize: 14, fontWeight: 500, marginLeft: 10 }}>
          {keys.length} stored
        </span>
      </h1>
      <p style={{ maxWidth: 720, color: 'var(--gray-500)', marginBottom: 24 }}>
        Mint scoped keys for each tenant or service that needs access to this DocumentForge node.
        Keys live in the auto-attached <code>_system.keys</code> collection — survive restarts,
        replicate with the database. The bootstrap admin key in <code>node.json</code> is what
        unlocks this page.
      </p>

      {/* Just-minted secret card — shown ONCE. Disappears on next mint or refresh. */}
      {minted && (
        <div
          className="card"
          style={{
            borderTop: '3px solid var(--red)',
            background: 'linear-gradient(135deg, rgba(217,4,41,0.04), rgba(245,158,11,0.04))',
            marginBottom: 24,
          }}
        >
          <div style={{ fontFamily: 'var(--mono)', fontSize: 11, letterSpacing: '0.08em', textTransform: 'uppercase', color: 'var(--red)', fontWeight: 700, marginBottom: 8 }}>
            ⚠ Copy this now — it's shown only once
          </div>
          <div style={{ display: 'grid', gridTemplateColumns: '1fr auto', gap: 12, alignItems: 'center', marginBottom: 12 }}>
            <code style={{
              fontFamily: 'var(--mono)',
              fontSize: 13,
              padding: '12px 14px',
              background: '#1a1a1a',
              color: '#fff',
              borderRadius: 2,
              userSelect: 'all',
              wordBreak: 'break-all',
            }}>
              {minted.secret}
            </code>
            <button
              onClick={copySecret}
              style={{
                background: copied ? 'var(--green)' : 'var(--red)',
                color: 'white',
                border: 'none',
                padding: '10px 18px',
                fontSize: 13,
                fontWeight: 600,
                cursor: 'pointer',
                minHeight: 44,
              }}
            >
              {copied ? '✓ Copied' : '📋 Copy'}
            </button>
          </div>
          <div className="setting-row"><span className="key">ID</span><span className="val" style={{ fontFamily: 'var(--mono)' }}>{minted.id}</span></div>
          <div className="setting-row"><span className="key">Scopes</span>
            <span className="val">
              {minted.scopes.map(s => <ScopePill key={s} scope={s} />)}
            </span>
          </div>
          {minted.description && (
            <div className="setting-row"><span className="key">Description</span><span className="val">{minted.description}</span></div>
          )}
          <div style={{ marginTop: 12, display: 'flex', justifyContent: 'flex-end' }}>
            <button onClick={() => setMinted(null)} style={ghostBtn()}>I've copied it — dismiss</button>
          </div>
        </div>
      )}

      {/* Generate-key card (toggles between collapsed button + expanded form) */}
      {!showGenForm ? (
        <div className="toolbar" style={{ marginBottom: 24 }}>
          <button onClick={() => { setShowGenForm(true); setGenError(null); }} style={primaryBtn()}>
            ⚡ Generate new key
          </button>
          <button onClick={refresh} disabled={loading} style={ghostBtn()}>⟲ Refresh</button>
        </div>
      ) : (
        <div className="card" style={{ borderTop: '3px solid var(--red)', marginBottom: 24 }}>
          <h3 style={{ marginTop: 0 }}>New key</h3>
          <form onSubmit={handleGenerate}>
            <div className="key" style={{ marginBottom: 4 }}>Scope</div>
            <div style={{ display: 'grid', gap: 6, marginBottom: 12 }}>
              <ScopeRadio
                mode="single-db" current={scopeMode} onChange={setScopeMode}
                label="Single database (read + write)"
                detail="db:{name} — full access to one DB only. Most common for tenant apps."
              />
              <ScopeRadio
                mode="single-db-read" current={scopeMode} onChange={setScopeMode}
                label="Single database, read-only"
                detail="db:{name}:read — Grafana scrapes, reporting jobs."
              />
              <ScopeRadio
                mode="all-dbs" current={scopeMode} onChange={setScopeMode}
                label="All databases (data plane)"
                detail="db:* — cluster-wide migrations, backup tools. Cannot manage DBs/services."
              />
              <ScopeRadio
                mode="admin" current={scopeMode} onChange={setScopeMode}
                label="Admin"
                detail="* — full access. Treat like a god key; rotate often."
              />
              <ScopeRadio
                mode="custom" current={scopeMode} onChange={setScopeMode}
                label="Custom (advanced)"
                detail="Comma- or newline-separated. e.g. db:tenant_a,db:lookup:read"
              />
            </div>

            {(scopeMode === 'single-db' || scopeMode === 'single-db-read') && (
              <div style={{ marginBottom: 12 }}>
                <div className="key">Database</div>
                <select value={scopeDb} onChange={e => setScopeDb(e.target.value)} style={{ padding: '8px 10px', fontSize: 14, border: '1px solid var(--gray-200)', background: 'white', minWidth: 240 }}>
                  <option value="">(pick a database)</option>
                  {databases.map(d => (
                    <option key={d.name} value={d.name}>{d.name}{d.isDefault ? ' (default)' : ''}</option>
                  ))}
                </select>
                <div style={{ fontSize: 11, color: 'var(--gray-500)', marginTop: 4 }}>
                  Will resolve to: <code style={{ fontFamily: 'var(--mono)' }}>{resolvedScopes[0] || '(pick a DB)'}</code>
                </div>
              </div>
            )}

            {scopeMode === 'custom' && (
              <div style={{ marginBottom: 12 }}>
                <div className="key">Scopes <span style={{ color: 'var(--gray-500)', fontWeight: 400 }}>(one per line or comma-separated)</span></div>
                <textarea
                  value={customScopes}
                  onChange={e => setCustomScopes(e.target.value)}
                  rows={4}
                  placeholder={"db:tenant_a\ndb:lookup_global:read"}
                  style={{ width: '100%', padding: 10, fontSize: 13, fontFamily: 'var(--mono)', border: '1px solid var(--gray-200)', background: '#fafafa' }}
                />
              </div>
            )}

            <div style={{ marginBottom: 12 }}>
              <div className="key">Description <span style={{ color: 'var(--gray-500)', fontWeight: 400 }}>(optional)</span></div>
              <input
                type="text"
                value={description}
                onChange={e => setDescription(e.target.value)}
                placeholder="e.g. 'tenant A production app'"
                style={{ width: '100%', padding: '8px 10px', fontSize: 14, border: '1px solid var(--gray-200)' }}
              />
            </div>

            <div style={{ display: 'flex', gap: 8, justifyContent: 'space-between', alignItems: 'center' }}>
              <div style={{ fontSize: 12, color: 'var(--gray-500)' }}>
                Will mint: <code style={{ fontFamily: 'var(--mono)' }}>{resolvedScopes.length === 0 ? '(no scopes)' : resolvedScopes.join(', ')}</code>
              </div>
              <div style={{ display: 'flex', gap: 8 }}>
                <button type="button" onClick={() => setShowGenForm(false)} style={ghostBtn()}>Cancel</button>
                <button
                  type="submit"
                  disabled={generating || resolvedScopes.length === 0}
                  style={primaryBtn(generating || resolvedScopes.length === 0)}
                >
                  {generating ? 'Minting…' : '⚡ Generate'}
                </button>
              </div>
            </div>

            {genError && (
              <div style={{ marginTop: 12, padding: '10px 12px', background: 'rgba(217,4,41,0.08)', color: 'var(--red)', fontFamily: 'var(--mono)', fontSize: 12, borderLeft: '3px solid var(--red)' }}>
                ✗ {genError}
              </div>
            )}
          </form>
        </div>
      )}

      {/* Existing keys */}
      {error && (
        <div className="card" style={{ borderLeft: '4px solid var(--red)', background: 'rgba(217,4,41,0.04)' }}>
          <strong style={{ color: 'var(--red)' }}>Could not load keys.</strong>
          <div style={{ fontFamily: 'var(--mono)', fontSize: 12, marginTop: 6 }}>{error}</div>
          <div style={{ fontSize: 12, color: 'var(--gray-500)', marginTop: 6 }}>
            The connected service may predate Issue #73 — this page needs a build that includes /admin/keys.
            Or your Bearer token might not have admin scope.
          </div>
        </div>
      )}

      {!error && !loading && keys.length === 0 && (
        <div className="card">
          <p>No keys minted yet.</p>
          <p style={{ color: 'var(--gray-500)', fontSize: 13 }}>
            Click "⚡ Generate new key" above to mint your first scoped key. Until then, only the
            bootstrap admin key from <code>node.json</code> works.
          </p>
        </div>
      )}

      {!error && keys.length > 0 && (
        <table className="table" style={{ background: 'white', border: '1px solid var(--gray-200)' }}>
          <thead>
            <tr style={{ background: 'var(--gray-100)' }}>
              <th style={thCell()}>ID</th>
              <th style={thCell()}>Scopes</th>
              <th style={thCell()}>Description</th>
              <th style={thCell()}>Created</th>
              <th style={thCell()}></th>
            </tr>
          </thead>
          <tbody>
            {keys.map(k => (
              <tr key={k.id} style={{ borderBottom: '1px solid var(--gray-100)', opacity: revoking === k.id ? 0.5 : 1 }}>
                <td style={tdCell()}>
                  <code style={{ fontFamily: 'var(--mono)', fontSize: 11 }} title={k.id}>
                    {k.id.slice(0, 12)}…
                  </code>
                </td>
                <td style={tdCell()}>
                  <div style={{ display: 'flex', gap: 4, flexWrap: 'wrap' }}>
                    {k.scopes.map(s => <ScopePill key={s} scope={s} />)}
                  </div>
                </td>
                <td style={tdCell()}>
                  {k.description || <span style={{ color: 'var(--gray-500)', fontStyle: 'italic' }}>—</span>}
                </td>
                <td style={tdCell()}>
                  <span style={{ fontFamily: 'var(--mono)', fontSize: 11, color: 'var(--gray-500)' }}>
                    {formatDate(k.createdAt)}
                  </span>
                </td>
                <td style={{ ...tdCell(), textAlign: 'right' }}>
                  <button
                    onClick={() => handleRevoke(k)}
                    disabled={revoking === k.id}
                    style={dangerBtn()}
                  >
                    {revoking === k.id ? 'Revoking…' : 'Revoke'}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <div style={{ marginTop: 24, fontSize: 12, color: 'var(--gray-500)', lineHeight: 1.65 }}>
        <strong>Bootstrap path:</strong> the legacy <code>node.json</code> <code>apiKey</code> still
        works as an admin key. Once you mint a scoped key here it persists in <code>_system.keys</code>
        and survives restarts. Revoking is immediate — no cache window. The plaintext is hashed
        (SHA-256) before storage; lost = revoke + regenerate.
      </div>
    </>
  );
}

// ---------- helpers ----------

function ScopeRadio(props: {
  mode: ScopeMode;
  current: ScopeMode;
  onChange: (m: ScopeMode) => void;
  label: string;
  detail: string;
}) {
  const selected = props.current === props.mode;
  return (
    <label
      style={{
        display: 'block',
        padding: '8px 12px',
        border: `1px solid ${selected ? 'var(--red)' : 'var(--gray-200)'}`,
        background: selected ? 'rgba(217,4,41,0.04)' : 'white',
        cursor: 'pointer',
      }}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
        <input
          type="radio"
          name="scopeMode"
          checked={selected}
          onChange={() => props.onChange(props.mode)}
        />
        <span style={{ fontWeight: 600 }}>{props.label}</span>
      </div>
      <div style={{ fontSize: 12, color: 'var(--gray-500)', marginLeft: 22, marginTop: 2 }}>
        {props.detail}
      </div>
    </label>
  );
}

function ScopePill({ scope }: { scope: string }) {
  // Color the pill by how powerful the scope is.
  let bg = 'var(--gray-100)', fg = 'var(--ink)';
  if (scope === '*') { bg = 'var(--red)'; fg = 'white'; }
  else if (scope === 'db:*') { bg = '#fef3c7'; fg = '#92400e'; }
  else if (scope.endsWith(':read')) { bg = '#dbeafe'; fg = '#1d4ed8'; }
  return (
    <span style={{
      display: 'inline-block',
      padding: '2px 8px',
      background: bg,
      color: fg,
      fontFamily: 'var(--mono)',
      fontSize: 10,
      fontWeight: 600,
      marginRight: 4,
      borderRadius: 2,
    }}>{scope}</span>
  );
}

function formatDate(iso: string): string {
  try {
    const d = new Date(iso);
    return d.toLocaleString();
  } catch { return iso; }
}

function primaryBtn(disabled: boolean = false): React.CSSProperties {
  return { background: disabled ? 'var(--gray-200)' : 'var(--red)', color: 'white', border: 'none', padding: '8px 18px', fontSize: 13, fontWeight: 700, cursor: disabled ? 'not-allowed' : 'pointer' };
}
function ghostBtn(): React.CSSProperties {
  return { background: 'transparent', color: 'var(--ink)', border: '1px solid var(--gray-200)', padding: '7px 14px', fontSize: 12, cursor: 'pointer' };
}
function dangerBtn(): React.CSSProperties {
  return { background: 'transparent', color: 'var(--red)', border: '1px solid var(--red)', padding: '5px 12px', fontSize: 11, fontWeight: 600, cursor: 'pointer' };
}
function thCell(): React.CSSProperties {
  return { padding: '10px 12px', textAlign: 'left', fontFamily: 'var(--mono)', fontSize: 10, letterSpacing: '0.08em', textTransform: 'uppercase', color: 'var(--gray-500)', fontWeight: 700, borderBottom: '1px solid var(--gray-200)' };
}
function tdCell(): React.CSSProperties {
  return { padding: '10px 12px', fontSize: 13, verticalAlign: 'middle' };
}
