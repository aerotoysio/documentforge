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
  type DatabaseEntry,
  type UnattachedFile,
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

  async function refresh() {
    setLoading(true);
    setError(null);
    try {
      const list = await listDatabases();
      setDatabases(list.databases);
      setDefaultName(list.default);
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

      {!error && databases.length > 0 && (
        <div style={{ display: 'grid', gap: 10 }}>
          {databases.map(db => {
            const isDefault = db.isDefault;
            const isDropping = dropping === db.name;
            const isSwitching = switching === db.name;
            const isPulsing = pulse === db.name;
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
                  </div>
                  <div style={{ fontFamily: 'var(--mono)', fontSize: 11, color: 'var(--gray-500)', marginTop: 4, wordBreak: 'break-all' }}>
                    {db.filePath}
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
