'use client';

import { useEffect, useState } from 'react';
import Link from 'next/link';
import { useConnections } from '@/lib/connections-context';
import {
  listDatabases,
  createDatabase,
  deleteDatabase,
  setDefaultDatabase,
  listUnattachedDatabases,
  discoverDatabases,
  getDatabaseHealth,
  previewRebuildCatalog,
  executeRebuildCatalog,
  type DatabaseEntry,
  type UnattachedFile,
  type DatabaseHealth,
  type RebuildPlan,
  type RebuildResult,
} from '@/lib/api';

// Issue #66 Phase 2 — "create a swarm on one box" lives here. Drop a name
// in the input, hit Enter, and a fresh .dfdb file lands on the service.
// The Active toggle is the per-service "I'm working on DB X" switch; until
// auth scopes ship (Phase 4) flat /collections/* calls resolve through it.

export default function DatabasesPage() {
  const { active } = useConnections();
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [databases, setDatabases] = useState<DatabaseEntry[]>([]);
  const [defaultName, setDefaultName] = useState<string | null>(null);
  const [draftName, setDraftName] = useState('');
  const [draftPath, setDraftPath] = useState('');
  const [creating, setCreating] = useState(false);
  const [dropping, setDropping] = useState<string | null>(null);
  const [switching, setSwitching] = useState<string | null>(null);
  const [createError, setCreateError] = useState<string | null>(null);
  const [pulse, setPulse] = useState<string | null>(null);

  // Issue #83 — "Browse & attach" panel state. Surfaces .dfdb files on
  // disk that aren't currently attached, so the operator doesn't have to
  // type absolute paths or wait for the next service restart.
  const [unattached, setUnattached] = useState<UnattachedFile[]>([]);
  const [unattachedDataDir, setUnattachedDataDir] = useState<string | null>(null);
  const [scanRecursive, setScanRecursive] = useState(false);
  const [scanning, setScanning] = useState(false);
  const [scanError, setScanError] = useState<string | null>(null);
  const [attaching, setAttaching] = useState<string | null>(null);
  const [renameDrafts, setRenameDrafts] = useState<Record<string, string>>({});

  // Issue #84 — runtime rescan + auto-attach (one-click bulk).
  const [discovering, setDiscovering] = useState(false);
  const [discoverBanner, setDiscoverBanner] = useState<string | null>(null);

  // Issue #85 — per-DB health map. Loaded in the background after the
  // main list comes back so the page paints fast and badges fill in.
  const [healthByName, setHealthByName] = useState<Record<string, DatabaseHealth>>({});
  const [healthInspect, setHealthInspect] = useState<DatabaseHealth | null>(null);

  // Issue #86 — rebuild-catalog wizard state. Operator picks a file
  // (typically from the health modal's "Rebuild catalog" CTA), we
  // detach it if attached, fetch the plan, show the chain preview,
  // and on confirm POST the execute.
  const [rebuildPath, setRebuildPath] = useState<string | null>(null);
  const [rebuildPlan, setRebuildPlan] = useState<RebuildPlan | null>(null);
  const [rebuildResult, setRebuildResult] = useState<RebuildResult | null>(null);
  const [rebuildError, setRebuildError] = useState<string | null>(null);
  const [rebuildBusy, setRebuildBusy] = useState(false);

  async function refresh() {
    setLoading(true);
    setError(null);
    try {
      const list = await listDatabases();
      setDatabases(list.databases);
      setDefaultName(list.default);
      // Issue #85 — kick off background health fetches per DB. Each is
      // independent so a slow / failing one doesn't block the others.
      // No await on the loop — we paint immediately and badges fill in.
      list.databases.forEach(d => {
        getDatabaseHealth(d.name)
          .then(h => setHealthByName(curr => ({ ...curr, [d.name]: h })))
          .catch(() => { /* leave the row badge-less on health-fetch failure */ });
      });
    } catch (e: any) {
      setError(e.message || String(e));
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => { refresh(); /* eslint-disable-next-line */ }, [active?.id]);

  async function scanUnattached(recursive: boolean) {
    setScanning(true);
    setScanError(null);
    setScanRecursive(recursive);
    try {
      const resp = await listUnattachedDatabases({ recursive });
      setUnattached(resp.files);
      setUnattachedDataDir(resp.dataDir);
      // Seed rename inputs to the suggested name so the operator
      // can attach without typing in the no-conflict case.
      const drafts: Record<string, string> = {};
      for (const f of resp.files) drafts[f.path] = f.suggestedName;
      setRenameDrafts(drafts);
    } catch (e: any) {
      setScanError(e.message || String(e));
    } finally {
      setScanning(false);
    }
  }

  async function discoverAndAttachAll(recursive: boolean) {
    setDiscovering(true);
    setDiscoverBanner(null);
    setScanError(null);
    try {
      const resp = await discoverDatabases({ recursive });
      // Refresh both the attached list AND the unattached panel so the
      // user sees the new state immediately. If there's any unattached
      // list visible, re-run that scan too to keep it consistent.
      await refresh();
      if (unattachedDataDir !== null) await scanUnattached(scanRecursive);
      const errPart = resp.errors.length > 0 ? ` · ${resp.errors.length} error${resp.errors.length === 1 ? '' : 's'}` : '';
      const tombPart = resp.skippedTombstoned > 0
        ? ` · ${resp.skippedTombstoned} tombstoned skipped (use Scan + Attach to override)`
        : '';
      setDiscoverBanner(
        resp.discovered > 0
          ? `✓ Attached ${resp.discovered} database${resp.discovered === 1 ? '' : 's'}${tombPart}${errPart}`
          : `No new databases found${tombPart}${errPart}`,
      );
      setTimeout(() => setDiscoverBanner(null), 5000);
    } catch (e: any) {
      setScanError(`Discover failed: ${e.message || String(e)}`);
    } finally {
      setDiscovering(false);
    }
  }

  async function openRebuildWizard(path: string, attachedAs: string | null) {
    setRebuildBusy(true);
    setRebuildError(null);
    setRebuildPlan(null);
    setRebuildResult(null);
    setRebuildPath(path);
    try {
      // If the DB is currently attached, the rebuilder will refuse —
      // detach first so the operator doesn't bounce off a 409.
      if (attachedAs) {
        await deleteDatabase(attachedAs);  // detach (not drop)
      }
      const plan = await previewRebuildCatalog(path);
      setRebuildPlan(plan);
      await refresh();
    } catch (e: any) {
      setRebuildError(e.message || String(e));
    } finally {
      setRebuildBusy(false);
    }
  }

  async function confirmRebuild() {
    if (!rebuildPath) return;
    setRebuildBusy(true);
    setRebuildError(null);
    try {
      const r = await executeRebuildCatalog(rebuildPath);
      setRebuildResult(r);
      // Health badges should refresh once the operator re-attaches —
      // they can do that with the existing Browse & Attach panel.
      await refresh();
    } catch (e: any) {
      setRebuildError(e.message || String(e));
    } finally {
      setRebuildBusy(false);
    }
  }

  function closeRebuildWizard() {
    setRebuildPath(null);
    setRebuildPlan(null);
    setRebuildResult(null);
    setRebuildError(null);
  }

  async function attachUnattached(file: UnattachedFile) {
    const name = (renameDrafts[file.path] || file.suggestedName).trim();
    if (!name) {
      setScanError('Pick a name first.');
      return;
    }
    setAttaching(file.path);
    setScanError(null);
    try {
      await createDatabase(name, { path: file.path, createIfMissing: false });
      // Pulse the new row in the attached list, drop it from the scan results.
      setPulse(name);
      setTimeout(() => setPulse(p => (p === name ? null : p)), 900);
      setUnattached(curr => curr.filter(f => f.path !== file.path));
      await refresh();
    } catch (e: any) {
      setScanError(`Attach failed: ${e.message || String(e)}`);
    } finally {
      setAttaching(null);
    }
  }

  async function onCreate(e?: React.FormEvent) {
    e?.preventDefault();
    const name = draftName.trim();
    if (!name) return;
    setCreateError(null);
    setCreating(true);
    try {
      await createDatabase(name, { path: draftPath.trim() || undefined });
      setDraftName('');
      setDraftPath('');
      // Pulse the new row so the user sees what just landed.
      setPulse(name);
      setTimeout(() => setPulse(p => (p === name ? null : p)), 900);
      await refresh();
    } catch (e: any) {
      setCreateError(e.message || String(e));
    } finally {
      setCreating(false);
    }
  }

  async function onDrop(db: DatabaseEntry, deleteFiles: boolean) {
    const verb = deleteFiles ? 'DROP' : 'detach';
    const msg = deleteFiles
      ? `DROP database "${db.name}" and DELETE every on-disk file?\n\nFile: ${db.filePath}\n\nThis is irreversible.`
      : `Detach database "${db.name}"?\n\nThe .dfdb file stays on disk — you can re-attach later.`;
    if (!confirm(msg)) return;
    setDropping(db.name);
    try {
      await deleteDatabase(db.name, { deleteFiles });
      await refresh();
    } catch (e: any) {
      alert(`${verb} failed: ${e.message || String(e)}`);
    } finally {
      setDropping(null);
    }
  }

  async function onActivate(db: DatabaseEntry) {
    if (db.isDefault) return;
    setSwitching(db.name);
    try {
      await setDefaultDatabase(db.name);
      await refresh();
    } catch (e: any) {
      alert(`Could not set "${db.name}" active: ${e.message || String(e)}`);
    } finally {
      setSwitching(null);
    }
  }

  if (!active) {
    return (
      <>
        <h1 className="page-title">Databases</h1>
        <div className="card">
          <p>No connection selected.</p>
          <p>Pick one from the sidebar or <Link href="/connections">add a connection</Link> first.</p>
        </div>
      </>
    );
  }

  return (
    <>
      <div className="eyebrow">{active.name}</div>
      <h1 className="page-title">
        Databases
        <span style={{ color: 'var(--gray-500)', fontSize: 18, fontWeight: 500, marginLeft: 12 }}>
          {databases.length} attached
        </span>
      </h1>

      {/* Create card */}
      <div className="card" style={{ marginBottom: 24 }}>
        <div style={{ fontFamily: 'var(--mono)', fontSize: 11, letterSpacing: '0.08em', textTransform: 'uppercase', color: 'var(--red)', fontWeight: 700, marginBottom: 10 }}>
          + Add database
        </div>
        <form onSubmit={onCreate} style={{ display: 'grid', gridTemplateColumns: '1fr 2fr auto', gap: 10, alignItems: 'end' }}>
          <div>
            <label style={labelStyle()}>Name</label>
            <input
              type="text"
              value={draftName}
              onChange={e => setDraftName(e.target.value)}
              placeholder="acme"
              style={inputStyle()}
              autoFocus
            />
          </div>
          <div>
            <label style={labelStyle()}>File path <span style={{ color: 'var(--gray-500)', fontWeight: 400 }}>(optional · defaults to data dir)</span></label>
            <input
              type="text"
              value={draftPath}
              onChange={e => setDraftPath(e.target.value)}
              placeholder="leave blank → {dataDir}/acme.dfdb"
              style={inputStyle()}
            />
          </div>
          <button
            type="submit"
            disabled={creating || !draftName.trim()}
            style={primaryBtn(creating || !draftName.trim())}
          >
            {creating ? 'Creating…' : 'Create'}
          </button>
        </form>
        <div style={{ marginTop: 8, fontSize: 12, color: 'var(--gray-500)' }}>
          Creates the file if it doesn't exist, attaches if it does. Names: letters/digits/underscore/dash, must start with a letter, max 64 chars.
        </div>
        {createError && (
          <div style={{ marginTop: 12, padding: 8, background: 'rgba(217,4,41,0.06)', color: 'var(--red)', fontFamily: 'var(--mono)', fontSize: 12 }}>
            ✗ {createError}
          </div>
        )}
      </div>

      {/* Issue #83 — Browse & attach panel. Sits between the Add form
          (operator-driven) and the attached-DBs list (state). Scans the
          service's data-dir for *.dfdb files not currently attached and
          lets the operator one-click attach them — useful for:
          * 1.0.x → 1.1.x upgrades where attach state was lost
          * dropping a backup .dfdb into a mounted volume
          * recovering after an accidental Detach */}
      <div className="card" style={{ marginBottom: 24 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
          <div style={{ flex: 1 }}>
            <div style={{ fontFamily: 'var(--mono)', fontSize: 11, letterSpacing: '0.08em', textTransform: 'uppercase', color: 'var(--red)', fontWeight: 700, marginBottom: 4 }}>
              ⌕ Browse & attach
            </div>
            <div style={{ fontSize: 12, color: 'var(--gray-500)' }}>
              Scan the service's data dir for <code style={{ fontFamily: 'var(--mono)' }}>.dfdb</code> files that aren't attached yet. Pick a name + click Attach — no path typing needed.
            </div>
          </div>
          <label style={{ display: 'flex', alignItems: 'center', gap: 6, fontSize: 12, color: 'var(--gray-500)', cursor: 'pointer' }} title="Search subfolders too (useful for backup folders, container volumes with nested layouts).">
            <input
              type="checkbox"
              checked={scanRecursive}
              onChange={e => setScanRecursive(e.target.checked)}
              disabled={scanning}
            />
            Recurse subfolders
          </label>
          <button
            onClick={() => discoverAndAttachAll(scanRecursive)}
            disabled={discovering || scanning}
            style={primaryBtn(discovering || scanning)}
            title="Scan the data dir AND auto-attach every .dfdb file found. Honours Detach tombstones — those need manual Attach via Scan."
          >
            {discovering ? 'Discovering…' : '↻ Scan & attach all'}
          </button>
          <button
            onClick={() => scanUnattached(scanRecursive)}
            disabled={scanning || discovering}
            style={ghostBtn()}
            title="Just list unattached files — useful when you want to rename before attaching, or skip particular files."
          >
            {scanning ? 'Scanning…' : '⌕ Scan (preview)'}
          </button>
        </div>

        {discoverBanner && (
          <div style={{ marginTop: 10, padding: 8, background: discoverBanner.startsWith('✓') ? 'rgba(40,160,80,0.08)' : 'rgba(0,0,0,0.04)', color: 'var(--ink)', fontFamily: 'var(--mono)', fontSize: 12 }}>
            {discoverBanner}
          </div>
        )}

        {unattachedDataDir && (
          <div style={{ marginTop: 10, fontFamily: 'var(--mono)', fontSize: 11, color: 'var(--gray-500)' }}>
            data dir: {unattachedDataDir}{scanRecursive ? ' (recursive)' : ''}
          </div>
        )}

        {scanError && (
          <div style={{ marginTop: 12, padding: 8, background: 'rgba(217,4,41,0.06)', color: 'var(--red)', fontFamily: 'var(--mono)', fontSize: 12 }}>
            ✗ {scanError}
          </div>
        )}

        {unattachedDataDir !== null && !scanning && unattached.length === 0 && !scanError && (
          <div style={{ marginTop: 12, fontSize: 13, color: 'var(--gray-500)' }}>
            No unattached <code style={{ fontFamily: 'var(--mono)' }}>.dfdb</code> files found{scanRecursive ? ' (recursive scan)' : ''}. Everything on disk is already attached.
          </div>
        )}

        {unattached.length > 0 && (
          <div style={{ marginTop: 14, display: 'grid', gap: 8 }}>
            {unattached.map(f => {
              const isAttaching = attaching === f.path;
              const draft = renameDrafts[f.path] ?? f.suggestedName;
              return (
                <div
                  key={f.path}
                  style={{
                    display: 'grid',
                    gridTemplateColumns: '1fr 200px auto',
                    gap: 10,
                    alignItems: 'center',
                    padding: 10,
                    border: '1px solid var(--gray-200)',
                    background: f.nameConflict ? 'rgba(217,4,41,0.03)' : 'white',
                    opacity: isAttaching ? 0.5 : 1,
                  }}
                >
                  <div>
                    <div style={{ fontFamily: 'var(--mono)', fontSize: 12, wordBreak: 'break-all' }}>
                      {f.path}
                    </div>
                    <div style={{ fontSize: 11, color: 'var(--gray-500)', marginTop: 2 }}>
                      {(f.sizeBytes / 1024).toFixed(1)} KB · modified {new Date(f.modifiedUtc).toLocaleString()}
                      {f.nameConflict && (
                        <span style={{ color: 'var(--red)', marginLeft: 8 }}>
                          ⚠ name <code style={{ fontFamily: 'var(--mono)' }}>{f.suggestedName}</code> already attached — pick another
                        </span>
                      )}
                    </div>
                  </div>
                  <input
                    type="text"
                    value={draft}
                    onChange={e => setRenameDrafts(d => ({ ...d, [f.path]: e.target.value }))}
                    placeholder="name"
                    style={inputStyle()}
                  />
                  <button
                    onClick={() => attachUnattached(f)}
                    disabled={isAttaching || !draft.trim()}
                    style={primaryBtn(isAttaching || !draft.trim())}
                  >
                    {isAttaching ? 'Attaching…' : '+ Attach'}
                  </button>
                </div>
              );
            })}
          </div>
        )}
      </div>

      {/* Toolbar */}
      <div className="toolbar">
        <button onClick={refresh} disabled={loading} style={ghostBtn()}>⟲ Refresh</button>
        <span className="spacer" />
        <span style={{ color: 'var(--gray-500)', fontSize: 12 }}>
          Default → flat <code style={{ fontFamily: 'var(--mono)' }}>/collections</code> routes target the active database
        </span>
      </div>

      {/* List */}
      {error && (
        <div className="card" style={{ borderLeft: '4px solid var(--red)', background: 'rgba(217,4,41,0.04)' }}>
          <strong style={{ color: 'var(--red)' }}>Could not load databases.</strong>
          <div style={{ fontFamily: 'var(--mono)', fontSize: 12, marginTop: 6 }}>{error}</div>
          <div style={{ fontSize: 12, color: 'var(--gray-500)', marginTop: 6 }}>
            Make sure the service at <code>{active.baseUrl}</code> is running a build that includes Issue #66.
          </div>
        </div>
      )}

      {!error && !loading && databases.length === 0 && (
        <div className="card">
          <p>No databases attached yet.</p>
          <p style={{ color: 'var(--gray-500)', fontSize: 13 }}>
            Add one above. The service hosts as many as you like — one process, N attached .dfdb files.
          </p>
        </div>
      )}

      {/* Issue #85 — top-level attention banner. If any DB needs operator
          action (rebuild-catalog, recovery-pending, engine-degraded),
          surface it here so the operator doesn't have to scan every row. */}
      {(() => {
        const attn = databases
          .map(d => healthByName[d.name])
          .filter((h): h is DatabaseHealth => h !== undefined && h.recommendation !== 'healthy');
        if (attn.length === 0) return null;
        return (
          <div className="card" style={{
            marginBottom: 16,
            borderLeft: '4px solid var(--red)',
            background: 'rgba(217,4,41,0.04)',
          }}>
            <div style={{ fontFamily: 'var(--mono)', fontSize: 11, letterSpacing: '0.08em', textTransform: 'uppercase', color: 'var(--red)', fontWeight: 700, marginBottom: 6 }}>
              ⚠ {attn.length} database{attn.length === 1 ? '' : 's'} need{attn.length === 1 ? 's' : ''} attention
            </div>
            <div style={{ display: 'grid', gap: 6 }}>
              {attn.map(h => (
                <div key={h.database} style={{ fontSize: 13 }}>
                  <strong>{h.database}</strong>
                  <span style={{ marginLeft: 6, fontFamily: 'var(--mono)', fontSize: 11, color: 'var(--red)' }}>
                    [{h.recommendation}]
                  </span>
                  <button
                    onClick={() => setHealthInspect(h)}
                    style={{ ...ghostBtn(), marginLeft: 8, fontSize: 11, padding: '2px 8px' }}
                  >
                    Details
                  </button>
                  {h.recommendationDetail && (
                    <div style={{ fontSize: 12, color: 'var(--gray-500)', marginTop: 2 }}>
                      {h.recommendationDetail}
                    </div>
                  )}
                </div>
              ))}
            </div>
          </div>
        );
      })()}

      {!error && databases.length > 0 && (
        <div style={{ display: 'grid', gap: 10 }}>
          {databases.map(db => {
            const isDefault = db.isDefault;
            const isDropping = dropping === db.name;
            const isSwitching = switching === db.name;
            const isPulsing = pulse === db.name;
            const health = healthByName[db.name];
            return (
              <div
                key={db.name}
                className="card"
                style={{
                  borderLeft: `4px solid ${isDefault ? 'var(--red)' : 'var(--gray-200)'}`,
                  display: 'grid',
                  gridTemplateColumns: '1fr auto',
                  alignItems: 'center',
                  gap: 16,
                  background: isPulsing ? 'rgba(217,4,41,0.04)' : 'white',
                  transition: 'background 600ms ease-out',
                  opacity: isDropping ? 0.5 : 1,
                }}
              >
                <div>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                    <span style={{ fontSize: 16, fontWeight: 600 }}>{db.name}</span>
                    {isDefault && <span className="pill red" style={{ fontSize: 10 }}>ACTIVE</span>}
                    {/* Issue #85 — health badge. Click for full diagnostic. */}
                    {health && (
                      <button
                        onClick={() => setHealthInspect(health)}
                        title={
                          health.recommendation === 'healthy'
                            ? `${health.collections.count} collection${health.collections.count === 1 ? '' : 's'} · ${health.collections.totalDocuments.toLocaleString()} docs · ${(health.files.dataSizeBytes / 1024).toFixed(0)} KB`
                            : health.recommendationDetail || health.recommendation
                        }
                        style={{
                          background: health.recommendation === 'healthy' ? 'rgba(40,160,80,0.1)' : 'rgba(217,4,41,0.1)',
                          color: health.recommendation === 'healthy' ? 'rgb(20,120,60)' : 'var(--red)',
                          border: 'none',
                          fontSize: 10,
                          fontWeight: 700,
                          fontFamily: 'var(--mono)',
                          letterSpacing: '0.05em',
                          textTransform: 'uppercase',
                          padding: '2px 8px',
                          cursor: 'pointer',
                        }}
                      >
                        {health.recommendation === 'healthy' ? '● healthy' : `⚠ ${health.recommendation}`}
                      </button>
                    )}
                  </div>
                  <div style={{ fontFamily: 'var(--mono)', fontSize: 11, color: 'var(--gray-500)', marginTop: 4, wordBreak: 'break-all' }}>
                    {db.filePath}
                    {health && (
                      <span style={{ marginLeft: 12 }}>
                        {health.collections.count} collection{health.collections.count === 1 ? '' : 's'} · {health.collections.totalDocuments.toLocaleString()} docs · {(health.files.dataSizeBytes / 1024).toFixed(0)} KB
                      </span>
                    )}
                  </div>
                </div>
                <div style={{ display: 'flex', gap: 6 }}>
                  {!isDefault && (
                    <button
                      onClick={() => onActivate(db)}
                      disabled={isSwitching || isDropping}
                      style={ghostBtn()}
                      title="Make this database the target of flat /collections routes"
                    >
                      {isSwitching ? 'Switching…' : 'Set active'}
                    </button>
                  )}
                  <button
                    onClick={() => onDrop(db, false)}
                    disabled={isDropping}
                    style={ghostBtn()}
                    title="Unregister but keep the .dfdb file on disk"
                  >
                    Detach
                  </button>
                  <button
                    onClick={() => onDrop(db, true)}
                    disabled={isDropping}
                    style={dangerBtn()}
                    title="Drop and delete every on-disk file (irreversible)"
                  >
                    {isDropping ? 'Dropping…' : 'Drop'}
                  </button>
                </div>
              </div>
            );
          })}
        </div>
      )}

      <div style={{ color: 'var(--gray-500)', fontSize: 12, marginTop: 32, lineHeight: 1.6 }}>
        💡 Tip — each attached database is a fully independent engine (own WAL, recovery log, lock file, page cache, replication followers). The registry is just a name → instance dictionary; it adds zero hot-path cost.
      </div>

      {/* Issue #85 — health inspect modal. Plain content reveal — no
          backdrop dimming because we want the operator to still see the
          row they clicked. Click outside or hit the X to close. */}
      {healthInspect && (
        <div
          onClick={() => setHealthInspect(null)}
          style={{
            position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.4)',
            display: 'flex', alignItems: 'center', justifyContent: 'center',
            zIndex: 50,
          }}
        >
          <div
            onClick={e => e.stopPropagation()}
            style={{
              background: 'white', padding: 24, maxWidth: 720, width: '92vw',
              maxHeight: '80vh', overflow: 'auto',
              border: '1px solid var(--gray-200)',
            }}
          >
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 12 }}>
              <h2 style={{ margin: 0, fontSize: 20 }}>
                Health · <code style={{ fontFamily: 'var(--mono)' }}>{healthInspect.database}</code>
              </h2>
              <button onClick={() => setHealthInspect(null)} style={ghostBtn()}>✕ Close</button>
            </div>
            <div style={{
              padding: 10,
              marginBottom: 14,
              background: healthInspect.recommendation === 'healthy' ? 'rgba(40,160,80,0.08)' : 'rgba(217,4,41,0.06)',
              color: healthInspect.recommendation === 'healthy' ? 'rgb(20,120,60)' : 'var(--red)',
              fontFamily: 'var(--mono)', fontSize: 12,
            }}>
              {healthInspect.recommendation === 'healthy' ? '● ' : '⚠ '}
              {healthInspect.recommendation}
              {healthInspect.recommendationDetail && (
                <div style={{ marginTop: 6, color: 'var(--ink)', fontFamily: 'var(--sans)', fontSize: 13, lineHeight: 1.5 }}>
                  {healthInspect.recommendationDetail}
                </div>
              )}
            </div>
            <table style={{ width: '100%', fontSize: 13, borderCollapse: 'collapse' }}>
              <tbody>
                {[
                  ['File path', <code key="fp" style={{ fontFamily: 'var(--mono)', fontSize: 11 }}>{healthInspect.filePath}</code>],
                  ['Engine health', healthInspect.healthStatus + (healthInspect.readOnly ? ' (read-only)' : '')],
                  ['Collections', `${healthInspect.collections.count} · ${healthInspect.collections.totalDocuments.toLocaleString()} docs total`],
                  ...(healthInspect.collections.names.length > 0
                    ? [['Collection names', healthInspect.collections.names.join(', ')] as [string, any]]
                    : []),
                  ['Data file', `${healthInspect.files.dataSizeBytes.toLocaleString()} bytes (${(healthInspect.files.dataSizeBytes / 1024).toFixed(1)} KB)`],
                  ['Recovery log', healthInspect.files.recoveryLogBytes === 0 ? 'empty' : `${healthInspect.files.recoveryLogBytes.toLocaleString()} bytes pending replay`],
                  ['WAL', healthInspect.files.walBytes === 0 ? 'empty' : `${healthInspect.files.walBytes.toLocaleString()} bytes`],
                  ['Snapshot incoming', healthInspect.files.snapshotMarkerPresent ? 'YES — partial snapshot present' : 'no'],
                  ...(healthInspect.lockHolder
                    ? [['Lock holder', `pid ${healthInspect.lockHolder.pid} on ${healthInspect.lockHolder.host} (opened ${healthInspect.lockHolder.openedAtUtc})`] as [string, any]]
                    : [['Lock holder', 'this service'] as [string, any]]),
                ].map(([label, val], i) => (
                  <tr key={i} style={{ borderBottom: '1px solid var(--gray-100)' }}>
                    <td style={{ padding: '8px 0', color: 'var(--gray-500)', width: 160, verticalAlign: 'top', fontSize: 11, fontFamily: 'var(--mono)', letterSpacing: '0.05em', textTransform: 'uppercase' }}>{label}</td>
                    <td style={{ padding: '8px 0', wordBreak: 'break-all' }}>{val as React.ReactNode}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            {healthInspect.recommendation === 'rebuild-catalog' && (
              <div style={{ marginTop: 16, padding: 12, background: 'rgba(217,4,41,0.04)', border: '1px solid rgba(217,4,41,0.15)' }}>
                <div style={{ fontWeight: 600, marginBottom: 6 }}>Recover this database</div>
                <div style={{ fontSize: 12, lineHeight: 1.5, color: 'var(--gray-700)', marginBottom: 10 }}>
                  Your data pages are likely intact but the catalog page (page 1 of the .dfdb file) that names collections is empty. The rebuilder scans the data pages, reconstructs the catalog, and writes a <code style={{ fontFamily: 'var(--mono)' }}>.page1.backup</code> sidecar first so the operation is reversible. The database will be detached briefly; you'll re-attach it once rebuild completes.
                </div>
                <button
                  onClick={() => {
                    const path = healthInspect.filePath;
                    const name = healthInspect.database;
                    setHealthInspect(null);
                    openRebuildWizard(path, name);
                  }}
                  style={primaryBtn(false)}
                >
                  ⚙ Rebuild catalog
                </button>
              </div>
            )}
          </div>
        </div>
      )}

      {/* Issue #86 — rebuild-catalog wizard. Multi-stage modal:
            Loading → Plan preview → (optionally) Result
          Backdrop click dismisses unless we're mid-execute. */}
      {rebuildPath && (
        <div
          onClick={() => { if (!rebuildBusy) closeRebuildWizard(); }}
          style={{
            position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.5)',
            display: 'flex', alignItems: 'center', justifyContent: 'center',
            zIndex: 60,
          }}
        >
          <div
            onClick={e => e.stopPropagation()}
            style={{
              background: 'white', padding: 24, maxWidth: 720, width: '92vw',
              maxHeight: '85vh', overflow: 'auto',
              border: '1px solid var(--gray-200)',
            }}
          >
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 12 }}>
              <h2 style={{ margin: 0, fontSize: 20 }}>⚙ Rebuild catalog</h2>
              <button
                onClick={closeRebuildWizard}
                disabled={rebuildBusy}
                style={ghostBtn()}
              >✕ Close</button>
            </div>
            <div style={{ fontFamily: 'var(--mono)', fontSize: 11, color: 'var(--gray-500)', marginBottom: 14, wordBreak: 'break-all' }}>
              {rebuildPath}
            </div>

            {rebuildError && (
              <div style={{ padding: 10, marginBottom: 14, background: 'rgba(217,4,41,0.08)', color: 'var(--red)', fontFamily: 'var(--mono)', fontSize: 12 }}>
                ✗ {rebuildError}
              </div>
            )}

            {!rebuildPlan && !rebuildResult && rebuildBusy && (
              <div style={{ padding: 24, textAlign: 'center', color: 'var(--gray-500)' }}>
                Detaching and scanning…
              </div>
            )}

            {rebuildPlan && !rebuildResult && (
              <>
                <div style={{ marginBottom: 14 }}>
                  {rebuildPlan.safeToRebuild ? (
                    <>
                      <div style={{ padding: 10, background: 'rgba(40,160,80,0.08)', color: 'rgb(20,120,60)', fontFamily: 'var(--mono)', fontSize: 12, marginBottom: 10 }}>
                        ✓ Safe to rebuild · {rebuildPlan.chains.length} data chain{rebuildPlan.chains.length === 1 ? '' : 's'} discovered
                      </div>
                      <div style={{ fontSize: 13, color: 'var(--gray-700)', lineHeight: 1.5 }}>
                        The current page-1 contents will be backed up to
                        <code style={{ fontFamily: 'var(--mono)', fontSize: 12, margin: '0 4px' }}>.page1.backup</code>
                        next to the data file. Recovered collections get auto-generated names — rename them with
                        <code style={{ fontFamily: 'var(--mono)', fontSize: 12, margin: '0 4px' }}>RENAME COLLECTION recovered_pN TO real_name</code>
                        once you identify each one.
                      </div>
                    </>
                  ) : (
                    <div style={{ padding: 10, background: 'rgba(217,4,41,0.08)', color: 'var(--red)', fontFamily: 'var(--mono)', fontSize: 12 }}>
                      ⚠ Rebuilder refused: {rebuildPlan.refusalReason}
                    </div>
                  )}
                </div>

                {rebuildPlan.chains.length > 0 && (
                  <table style={{ width: '100%', fontSize: 13, borderCollapse: 'collapse', marginBottom: 14 }}>
                    <thead>
                      <tr style={{ borderBottom: '2px solid var(--gray-200)' }}>
                        <th style={{ padding: '8px 0', textAlign: 'left', fontFamily: 'var(--mono)', fontSize: 11, letterSpacing: '0.05em', textTransform: 'uppercase', color: 'var(--gray-500)' }}>Suggested name</th>
                        <th style={{ padding: '8px 0', textAlign: 'right', fontFamily: 'var(--mono)', fontSize: 11, letterSpacing: '0.05em', textTransform: 'uppercase', color: 'var(--gray-500)' }}>Pages</th>
                        <th style={{ padding: '8px 0', textAlign: 'right', fontFamily: 'var(--mono)', fontSize: 11, letterSpacing: '0.05em', textTransform: 'uppercase', color: 'var(--gray-500)' }}>Docs</th>
                        <th style={{ padding: '8px 0', textAlign: 'right', fontFamily: 'var(--mono)', fontSize: 11, letterSpacing: '0.05em', textTransform: 'uppercase', color: 'var(--gray-500)' }}>First page</th>
                      </tr>
                    </thead>
                    <tbody>
                      {rebuildPlan.chains.map(c => (
                        <tr key={c.firstPageId} style={{ borderBottom: '1px solid var(--gray-100)' }}>
                          <td style={{ padding: '8px 0', fontFamily: 'var(--mono)', fontSize: 12 }}>{c.suggestedName}</td>
                          <td style={{ padding: '8px 0', textAlign: 'right' }}>{c.pageCount}</td>
                          <td style={{ padding: '8px 0', textAlign: 'right' }}>{c.documentCount.toLocaleString()}</td>
                          <td style={{ padding: '8px 0', textAlign: 'right', fontFamily: 'var(--mono)', fontSize: 11, color: 'var(--gray-500)' }}>#{c.firstPageId}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                )}

                {rebuildPlan.safeToRebuild && (
                  <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end' }}>
                    <button onClick={closeRebuildWizard} disabled={rebuildBusy} style={ghostBtn()}>Cancel</button>
                    <button onClick={confirmRebuild} disabled={rebuildBusy} style={primaryBtn(rebuildBusy)}>
                      {rebuildBusy ? 'Rebuilding…' : '⚙ Commit rebuild'}
                    </button>
                  </div>
                )}
              </>
            )}

            {rebuildResult && (
              <>
                <div style={{ padding: 10, background: 'rgba(40,160,80,0.1)', color: 'rgb(20,120,60)', fontFamily: 'var(--mono)', fontSize: 12, marginBottom: 14 }}>
                  ✓ Rebuilt · {rebuildResult.recovered.length} collection{rebuildResult.recovered.length === 1 ? '' : 's'} recovered
                </div>
                <div style={{ fontSize: 13, lineHeight: 1.6, color: 'var(--gray-700)' }}>
                  <strong>Backup written:</strong>{' '}
                  <code style={{ fontFamily: 'var(--mono)', fontSize: 12 }}>{rebuildResult.backupPath}</code>{' '}
                  ({rebuildResult.backedUpPage1Bytes.toLocaleString()} bytes). To revert: overwrite page 1 of the .dfdb with this file's contents.
                </div>
                <table style={{ width: '100%', fontSize: 13, borderCollapse: 'collapse', margin: '14px 0' }}>
                  <thead>
                    <tr style={{ borderBottom: '2px solid var(--gray-200)' }}>
                      <th style={{ padding: '8px 0', textAlign: 'left', fontFamily: 'var(--mono)', fontSize: 11, color: 'var(--gray-500)' }}>Recovered as</th>
                      <th style={{ padding: '8px 0', textAlign: 'right', fontFamily: 'var(--mono)', fontSize: 11, color: 'var(--gray-500)' }}>Docs</th>
                    </tr>
                  </thead>
                  <tbody>
                    {rebuildResult.recovered.map(r => (
                      <tr key={r.firstPageId} style={{ borderBottom: '1px solid var(--gray-100)' }}>
                        <td style={{ padding: '8px 0', fontFamily: 'var(--mono)', fontSize: 12 }}>{r.name}</td>
                        <td style={{ padding: '8px 0', textAlign: 'right' }}>{r.documentCount.toLocaleString()}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
                <div style={{ fontSize: 12, color: 'var(--gray-500)', lineHeight: 1.5, marginBottom: 14 }}>
                  <strong>Next step:</strong> Re-attach the database via the "+ Add Database" form above (it's currently detached), then inspect each recovered collection to identify it. Rename with{' '}
                  <code style={{ fontFamily: 'var(--mono)', fontSize: 12 }}>RENAME COLLECTION recovered_pN TO real_name</code>.
                </div>
                <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
                  <button onClick={closeRebuildWizard} style={primaryBtn(false)}>Done</button>
                </div>
              </>
            )}
          </div>
        </div>
      )}
    </>
  );
}

// ---------- styling helpers ----------
function labelStyle(): React.CSSProperties {
  return { display: 'block', fontFamily: 'var(--mono)', fontSize: 10, letterSpacing: '0.08em', textTransform: 'uppercase', color: 'var(--gray-500)', marginBottom: 4, fontWeight: 700 };
}
function inputStyle(): React.CSSProperties {
  return { width: '100%', padding: '8px 10px', fontSize: 14, border: '1px solid var(--gray-200)', fontFamily: 'var(--sans)', background: 'white' };
}
function primaryBtn(disabled: boolean): React.CSSProperties {
  return { background: disabled ? 'var(--gray-200)' : 'var(--red)', color: 'white', border: 'none', padding: '10px 18px', fontSize: 13, fontWeight: 600, cursor: disabled ? 'not-allowed' : 'pointer', minHeight: 38 };
}
function ghostBtn(): React.CSSProperties {
  return { background: 'transparent', color: 'var(--ink)', border: '1px solid var(--gray-200)', padding: '6px 12px', fontSize: 12, cursor: 'pointer' };
}
function dangerBtn(): React.CSSProperties {
  return { background: 'transparent', color: 'var(--red)', border: '1px solid var(--red)', padding: '6px 12px', fontSize: 12, fontWeight: 600, cursor: 'pointer' };
}
