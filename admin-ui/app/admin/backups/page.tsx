'use client';

// Issue #87 — Backups page. The whole design goal is "non-SQL-dev
// manages this with confidence" so every action is a single button
// click, every destructive action confirms first, restore always
// creates a fresh DB (the source is never clobbered), and the page
// makes the "what state are my backups in?" question answerable
// without reading the API docs.

import { useEffect, useState } from 'react';
import { useConnections } from '@/lib/connections-context';
import {
  listDatabases,
  type DatabaseEntry,
  listAllBackups,
  backupDatabase,
  backupAllDatabases,
  deleteBackup,
  restoreBackup,
  getBackupConfig,
  saveBackupConfig,
  type BackupRow,
  type BackupConfigResponse,
} from '@/lib/api';

export default function BackupsPage() {
  const { active } = useConnections();
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [databases, setDatabases] = useState<DatabaseEntry[]>([]);
  const [backups, setBackups] = useState<BackupRow[]>([]);
  const [config, setConfig] = useState<BackupConfigResponse | null>(null);

  // Action state — keyed by id so multiple actions on different rows
  // animate independently.
  const [backingUp, setBackingUp] = useState<string | null>(null);
  const [backingUpAll, setBackingUpAll] = useState(false);
  const [banner, setBanner] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null);

  // Restore wizard.
  const [restoreSrc, setRestoreSrc] = useState<BackupRow | null>(null);
  const [restoreName, setRestoreName] = useState('');
  const [restoring, setRestoring] = useState(false);

  // Settings panel.
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [draftBackupDir, setDraftBackupDir] = useState('');
  const [draftRetention, setDraftRetention] = useState(10);
  const [savingSettings, setSavingSettings] = useState(false);

  async function refresh() {
    setLoading(true);
    setError(null);
    try {
      const [dbs, bks, cfg] = await Promise.all([
        listDatabases(),
        listAllBackups(),
        getBackupConfig(),
      ]);
      setDatabases(dbs.databases.filter(d => !d.name.startsWith('_')));
      setBackups(bks.backups);
      setConfig(cfg);
      setDraftBackupDir(cfg.backupDirConfigured ?? '');
      setDraftRetention(cfg.retentionCount);
    } catch (e: any) {
      setError(e.message || String(e));
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => { refresh(); /* eslint-disable-next-line */ }, [active?.id]);

  function showBanner(kind: 'ok' | 'err', text: string) {
    setBanner({ kind, text });
    setTimeout(() => setBanner(null), 5000);
  }

  async function onBackupOne(db: DatabaseEntry) {
    setBackingUp(db.name);
    try {
      await backupDatabase(db.name);
      await refresh();
      showBanner('ok', `Backed up ${db.name}.`);
    } catch (e: any) {
      showBanner('err', `Backup failed: ${e.message || e}`);
    } finally {
      setBackingUp(null);
    }
  }

  async function onBackupAll() {
    setBackingUpAll(true);
    try {
      const result = await backupAllDatabases();
      await refresh();
      const ok = result.count;
      const errs = result.errors.length;
      showBanner(errs > 0 ? 'err' : 'ok',
        `Backed up ${ok} database${ok === 1 ? '' : 's'}` +
        (errs > 0 ? ` · ${errs} failed: ${result.errors.join('; ')}` : '.'));
    } catch (e: any) {
      showBanner('err', `Backup-all failed: ${e.message || e}`);
    } finally {
      setBackingUpAll(false);
    }
  }

  async function onDelete(bk: BackupRow) {
    if (!confirm(`Delete backup of "${bk.database}" from ${new Date(bk.createdAtUtc).toLocaleString()}?\n\nThe .dfdb file at ${bk.path} will be removed from disk and the audit row deleted. This cannot be undone.`)) return;
    try {
      await deleteBackup(bk.id);
      await refresh();
      showBanner('ok', `Deleted backup ${bk.id.slice(0, 8)}…`);
    } catch (e: any) {
      showBanner('err', `Delete failed: ${e.message || e}`);
    }
  }

  function openRestore(bk: BackupRow) {
    setRestoreSrc(bk);
    // Suggest a name that won't collide with the source. The user can
    // edit before submitting.
    const stamp = new Date(bk.createdAtUtc).toISOString().slice(0, 10).replace(/-/g, '');
    setRestoreName(`${bk.database}_restored_${stamp}`);
  }

  async function confirmRestore() {
    if (!restoreSrc) return;
    const name = restoreName.trim();
    if (!name) { showBanner('err', 'Pick a name for the restored database.'); return; }
    setRestoring(true);
    try {
      const result = await restoreBackup(restoreSrc.id, name);
      await refresh();
      setRestoreSrc(null);
      showBanner('ok', `Restored ${restoreSrc.database} → ${result.database}. Visit Studio to query it.`);
    } catch (e: any) {
      showBanner('err', `Restore failed: ${e.message || e}`);
    } finally {
      setRestoring(false);
    }
  }

  async function saveSettings() {
    setSavingSettings(true);
    try {
      await saveBackupConfig({
        backupDir: draftBackupDir.trim() || null,
        retentionCount: draftRetention,
      });
      await refresh();
      showBanner('ok', 'Settings saved.');
      setSettingsOpen(false);
    } catch (e: any) {
      showBanner('err', `Save failed: ${e.message || e}`);
    } finally {
      setSavingSettings(false);
    }
  }

  if (!active) {
    return (
      <>
        <h1 className="page-title">Backups</h1>
        <div className="card">
          <p>No connection selected. Pick one from the sidebar first.</p>
        </div>
      </>
    );
  }

  // Group backups by DB for the per-DB sub-lists below the main row.
  const backupsByDb = backups.reduce<Record<string, BackupRow[]>>((m, b) => {
    (m[b.database] ??= []).push(b);
    return m;
  }, {});

  return (
    <>
      <div className="eyebrow">{active.name}</div>
      <h1 className="page-title">
        Backups
        <span style={{ color: 'var(--gray-500)', fontSize: 18, fontWeight: 500, marginLeft: 12 }}>
          {backups.length} stored
        </span>
      </h1>

      {banner && (
        <div style={{
          padding: 10,
          marginBottom: 14,
          background: banner.kind === 'ok' ? 'rgba(40,160,80,0.1)' : 'rgba(217,4,41,0.08)',
          color: banner.kind === 'ok' ? 'rgb(20,120,60)' : 'var(--red)',
          fontFamily: 'var(--mono)', fontSize: 12,
        }}>
          {banner.kind === 'ok' ? '✓ ' : '✗ '}{banner.text}
        </div>
      )}

      {/* Action bar */}
      <div className="card" style={{ marginBottom: 16, display: 'flex', alignItems: 'center', gap: 12 }}>
        <div style={{ flex: 1 }}>
          <div style={{ fontWeight: 600, marginBottom: 2 }}>Backup all tenant databases</div>
          <div style={{ fontSize: 12, color: 'var(--gray-500)' }}>
            Snapshots every attached DB (excluding <code>_system</code>) under your backup directory. Safe to run at any time — uses a consistent on-disk checkpoint, no writes are blocked beyond the snapshot duration.
          </div>
        </div>
        <button
          onClick={() => setSettingsOpen(s => !s)}
          style={ghostBtn()}
        >⚙ Settings</button>
        <button
          onClick={onBackupAll}
          disabled={backingUpAll}
          style={primaryBtn(backingUpAll)}
        >
          {backingUpAll ? 'Backing up…' : '↻ Backup all now'}
        </button>
      </div>

      {/* Settings */}
      {settingsOpen && (
        <div className="card" style={{ marginBottom: 16, borderLeft: '4px solid var(--gray-500)' }}>
          <div style={{ fontFamily: 'var(--mono)', fontSize: 11, letterSpacing: '0.08em', textTransform: 'uppercase', color: 'var(--gray-500)', fontWeight: 700, marginBottom: 10 }}>
            ⚙ Backup settings
          </div>
          <div style={{ display: 'grid', gridTemplateColumns: '2fr 1fr auto', gap: 10, alignItems: 'end' }}>
            <div>
              <label style={labelStyle()}>Backup directory <span style={{ color: 'var(--gray-500)', fontWeight: 400 }}>(blank = default <code>{`{dataDir}/backups`}</code>)</span></label>
              <input
                type="text"
                value={draftBackupDir}
                onChange={e => setDraftBackupDir(e.target.value)}
                placeholder={config?.backupDir || '/path/to/backups'}
                style={inputStyle()}
              />
            </div>
            <div>
              <label style={labelStyle()}>Keep N most recent per DB</label>
              <input
                type="number"
                min={1}
                max={1000}
                value={draftRetention}
                onChange={e => setDraftRetention(parseInt(e.target.value, 10) || 1)}
                style={inputStyle()}
              />
            </div>
            <button
              onClick={saveSettings}
              disabled={savingSettings}
              style={primaryBtn(savingSettings)}
            >
              {savingSettings ? 'Saving…' : 'Save'}
            </button>
          </div>
          <div style={{ marginTop: 8, fontSize: 12, color: 'var(--gray-500)' }}>
            Effective backup dir: <code style={{ fontFamily: 'var(--mono)' }}>{config?.backupDir}</code>
            {config?.backupDirConfigured && config.backupDirConfigured !== config.backupDir && (
              <> · <span style={{ color: 'var(--red)' }}>warning — configured value <code>{config.backupDirConfigured}</code> differs from effective</span></>
            )}
          </div>
        </div>
      )}

      {/* Loading / error */}
      {error && (
        <div className="card" style={{ borderLeft: '4px solid var(--red)' }}>
          <strong style={{ color: 'var(--red)' }}>Could not load.</strong>
          <div style={{ fontFamily: 'var(--mono)', fontSize: 12 }}>{error}</div>
        </div>
      )}
      {loading && <div className="card">Loading…</div>}

      {/* Per-DB row */}
      {!loading && !error && (
        <div style={{ display: 'grid', gap: 12 }}>
          {databases.length === 0 && (
            <div className="card">
              <p>No tenant databases attached. Visit <a href="/databases">Databases</a> to attach one.</p>
            </div>
          )}
          {databases.map(db => {
            const rows = backupsByDb[db.name] ?? [];
            const isBackingUp = backingUp === db.name;
            const last = rows[0];
            return (
              <div key={db.name} className="card">
                <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
                  <div style={{ flex: 1 }}>
                    <div style={{ fontWeight: 600, fontSize: 16 }}>{db.name}</div>
                    <div style={{ fontFamily: 'var(--mono)', fontSize: 11, color: 'var(--gray-500)', marginTop: 2 }}>
                      {db.filePath}
                    </div>
                    <div style={{ fontSize: 12, color: 'var(--gray-500)', marginTop: 6 }}>
                      {rows.length === 0 ? (
                        <span style={{ color: 'var(--red)' }}>⚠ No backups yet — take your first one!</span>
                      ) : (
                        <>
                          {rows.length} backup{rows.length === 1 ? '' : 's'} · last on {new Date(last.createdAtUtc).toLocaleString()}
                          {' · '}{(last.sizeBytes / 1024).toFixed(0)} KB
                        </>
                      )}
                    </div>
                  </div>
                  <button
                    onClick={() => onBackupOne(db)}
                    disabled={isBackingUp || backingUpAll}
                    style={primaryBtn(isBackingUp || backingUpAll)}
                  >
                    {isBackingUp ? 'Backing up…' : '↻ Backup now'}
                  </button>
                </div>

                {rows.length > 0 && (
                  <details style={{ marginTop: 12 }}>
                    <summary style={{ cursor: 'pointer', fontSize: 12, color: 'var(--gray-500)', fontFamily: 'var(--mono)' }}>
                      Show {rows.length} backup{rows.length === 1 ? '' : 's'} for {db.name}
                    </summary>
                    <table style={{ width: '100%', marginTop: 8, fontSize: 13, borderCollapse: 'collapse' }}>
                      <thead>
                        <tr style={{ borderBottom: '2px solid var(--gray-200)' }}>
                          <th style={thStyle()}>Created</th>
                          <th style={thStyle()}>Size</th>
                          <th style={thStyle()}>Kind</th>
                          <th style={{ ...thStyle(), textAlign: 'right' }}>Actions</th>
                        </tr>
                      </thead>
                      <tbody>
                        {rows.map(r => (
                          <tr key={r.id} style={{ borderBottom: '1px solid var(--gray-100)' }}>
                            <td style={tdStyle()}>{new Date(r.createdAtUtc).toLocaleString()}</td>
                            <td style={tdStyle()}>{(r.sizeBytes / 1024).toFixed(0)} KB</td>
                            <td style={tdStyle()}><code style={{ fontFamily: 'var(--mono)', fontSize: 11 }}>{r.kind}</code></td>
                            <td style={{ ...tdStyle(), textAlign: 'right' }}>
                              <button onClick={() => openRestore(r)} style={ghostBtn(2)}>Restore</button>
                              <button onClick={() => onDelete(r)} style={dangerBtn(2)}>Delete</button>
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </details>
                )}
              </div>
            );
          })}
        </div>
      )}

      {/* Restore wizard */}
      {restoreSrc && (
        <div
          onClick={() => !restoring && setRestoreSrc(null)}
          style={{
            position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.5)',
            display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 50,
          }}
        >
          <div onClick={e => e.stopPropagation()} style={{
            background: 'white', padding: 24, maxWidth: 640, width: '92vw',
            border: '1px solid var(--gray-200)',
          }}>
            <h2 style={{ margin: 0, marginBottom: 8, fontSize: 20 }}>Restore backup</h2>
            <div style={{ fontSize: 13, color: 'var(--gray-700)', marginBottom: 14, lineHeight: 1.5 }}>
              The backup is copied to a <strong>new database</strong>. The source database is never touched — even if you fat-finger the name. Use Studio to compare the restored data, then drop the old one if you want to keep just the restore.
            </div>
            <table style={{ width: '100%', fontSize: 13, marginBottom: 14, borderCollapse: 'collapse' }}>
              <tbody>
                <tr style={{ borderBottom: '1px solid var(--gray-100)' }}>
                  <td style={{ padding: '6px 0', color: 'var(--gray-500)', fontSize: 11, fontFamily: 'var(--mono)', letterSpacing: '0.05em', textTransform: 'uppercase' }}>Source</td>
                  <td style={{ padding: '6px 0' }}>{restoreSrc.database}</td>
                </tr>
                <tr style={{ borderBottom: '1px solid var(--gray-100)' }}>
                  <td style={{ padding: '6px 0', color: 'var(--gray-500)', fontSize: 11, fontFamily: 'var(--mono)', letterSpacing: '0.05em', textTransform: 'uppercase' }}>Backup taken</td>
                  <td style={{ padding: '6px 0' }}>{new Date(restoreSrc.createdAtUtc).toLocaleString()}</td>
                </tr>
                <tr>
                  <td style={{ padding: '6px 0', color: 'var(--gray-500)', fontSize: 11, fontFamily: 'var(--mono)', letterSpacing: '0.05em', textTransform: 'uppercase' }}>Size</td>
                  <td style={{ padding: '6px 0' }}>{(restoreSrc.sizeBytes / 1024).toFixed(0)} KB</td>
                </tr>
              </tbody>
            </table>
            <label style={labelStyle()}>Restore as new database name</label>
            <input
              type="text"
              value={restoreName}
              onChange={e => setRestoreName(e.target.value)}
              style={{ ...inputStyle(), marginBottom: 14 }}
              autoFocus
            />
            <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end' }}>
              <button onClick={() => setRestoreSrc(null)} disabled={restoring} style={ghostBtn()}>Cancel</button>
              <button onClick={confirmRestore} disabled={restoring || !restoreName.trim()} style={primaryBtn(restoring || !restoreName.trim())}>
                {restoring ? 'Restoring…' : '↻ Restore'}
              </button>
            </div>
          </div>
        </div>
      )}

      <div style={{ color: 'var(--gray-500)', fontSize: 12, marginTop: 32, lineHeight: 1.6 }}>
        💡 Today: hot snapshots via the engine's <code style={{ fontFamily: 'var(--mono)' }}>Snapshot()</code> primitive (writes briefly blocked during the fsync + copy; safe to run on a live system). Coming next: WAL archiving + true point-in-time recovery (restore to a specific timestamp), scheduled backups, offsite shipping (S3 / Azure Blob), per-shard coordination for cluster-wide consistent snapshots.
      </div>
    </>
  );
}

function labelStyle(): React.CSSProperties {
  return { display: 'block', fontFamily: 'var(--mono)', fontSize: 10, letterSpacing: '0.08em', textTransform: 'uppercase', color: 'var(--gray-500)', marginBottom: 4, fontWeight: 700 };
}
function inputStyle(): React.CSSProperties {
  return { width: '100%', padding: '8px 10px', fontSize: 14, border: '1px solid var(--gray-200)', fontFamily: 'var(--sans)', background: 'white' };
}
function primaryBtn(disabled: boolean): React.CSSProperties {
  return { background: disabled ? 'var(--gray-200)' : 'var(--red)', color: 'white', border: 'none', padding: '10px 18px', fontSize: 13, fontWeight: 600, cursor: disabled ? 'not-allowed' : 'pointer', minHeight: 38 };
}
function ghostBtn(margin = 0): React.CSSProperties {
  return { background: 'transparent', color: 'var(--ink)', border: '1px solid var(--gray-200)', padding: '6px 12px', fontSize: 12, cursor: 'pointer', marginLeft: margin === 0 ? 0 : margin };
}
function dangerBtn(margin = 0): React.CSSProperties {
  return { background: 'transparent', color: 'var(--red)', border: '1px solid var(--red)', padding: '6px 12px', fontSize: 12, fontWeight: 600, cursor: 'pointer', marginLeft: margin };
}
function thStyle(): React.CSSProperties {
  return { padding: '8px 0', textAlign: 'left', fontFamily: 'var(--mono)', fontSize: 11, letterSpacing: '0.05em', textTransform: 'uppercase', color: 'var(--gray-500)' };
}
function tdStyle(): React.CSSProperties {
  return { padding: '8px 0' };
}
