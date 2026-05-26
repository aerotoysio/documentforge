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
  getArchiveStatus,
  listArchiveSegments,
  enableArchiving,
  disableArchiving,
  flushArchive,
  previewPitr,
  executePitr,
  type ArchiveStatusResponse,
  type WalSegmentRow,
  type PitrPreviewResponse,
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

  // Issue #88 — archive status per DB + PITR wizard state.
  const [archiveStatus, setArchiveStatus] = useState<Record<string, ArchiveStatusResponse>>({});
  const [archiveBusy, setArchiveBusy] = useState<string | null>(null);

  // PITR wizard state. Opens when the user clicks "Restore to point-
  // in-time" on a backup row. Walks them through picking a target
  // time, previewing what would be replayed, and committing.
  const [pitrSrc, setPitrSrc] = useState<BackupRow | null>(null);
  const [pitrTargetTime, setPitrTargetTime] = useState('');
  const [pitrNewName, setPitrNewName] = useState('');
  const [pitrSegments, setPitrSegments] = useState<WalSegmentRow[]>([]);
  const [pitrPreview, setPitrPreview] = useState<PitrPreviewResponse | null>(null);
  const [pitrPreviewing, setPitrPreviewing] = useState(false);
  const [pitrRestoring, setPitrRestoring] = useState(false);

  async function refresh() {
    setLoading(true);
    setError(null);
    try {
      const [dbs, bks, cfg] = await Promise.all([
        listDatabases(),
        listAllBackups(),
        getBackupConfig(),
      ]);
      const userDbs = dbs.databases.filter(d => !d.name.startsWith('_'));
      setDatabases(userDbs);
      setBackups(bks.backups);
      setConfig(cfg);
      setDraftBackupDir(cfg.backupDirConfigured ?? '');
      setDraftRetention(cfg.retentionCount);
      // Issue #88 — fetch archive status for every user-tenant DB in
      // the background so the per-row badges + the PITR wizard can
      // tell which DBs have continuous archiving enabled. Per-DB,
      // independent so a slow one doesn't block the rest.
      userDbs.forEach(d => {
        getArchiveStatus(d.name)
          .then(s => setArchiveStatus(curr => ({ ...curr, [d.name]: s })))
          .catch(() => { /* leave the row badge-less on failure */ });
      });
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

  async function toggleArchive(db: DatabaseEntry) {
    const isOn = archiveStatus[db.name]?.enabled;
    setArchiveBusy(db.name);
    try {
      if (isOn) await disableArchiving(db.name);
      else await enableArchiving(db.name);
      await refresh();
      showBanner('ok', isOn
        ? `Continuous archiving disabled for ${db.name}.`
        : `Continuous archiving enabled for ${db.name}. Page writes are now shipped on each flush — PITR available.`);
    } catch (e: any) {
      showBanner('err', `Archive toggle failed: ${e.message || e}`);
    } finally {
      setArchiveBusy(null);
    }
  }

  async function flushNow() {
    try {
      await flushArchive();
      await refresh();
      showBanner('ok', 'Forced flush — pending pages shipped as a new segment for every enabled DB.');
    } catch (e: any) {
      showBanner('err', `Flush failed: ${e.message || e}`);
    }
  }

  async function openPitrWizard(bk: BackupRow) {
    setPitrSrc(bk);
    setPitrPreview(null);
    // Default the target to "now" — but as a LOCAL-time string so
    // the datetime-local input shows the operator's wall-clock time,
    // not UTC. Subtracting the timezone offset before .toISOString()
    // makes the string parse identically as local time when the
    // input later round-trips it back to a UTC ISO string for the API.
    const now = new Date();
    const localOffsetMs = now.getTimezoneOffset() * 60000;
    const localNowIso = new Date(now.getTime() - localOffsetMs).toISOString().slice(0, 19);
    setPitrTargetTime(localNowIso);
    const stamp = localNowIso.replace(/[-:]/g, '').replace('T', '_');
    setPitrNewName(`${bk.database}_pitr_${stamp.slice(0, 13)}`);
    // Fetch the segment list for the source DB so the timeline can
    // render. Best-effort — the wizard still works without it,
    // we just lose the visualisation.
    try {
      const segs = await listArchiveSegments(bk.database);
      setPitrSegments(segs.segments);
    } catch {
      setPitrSegments([]);
    }
  }

  async function previewPitrNow() {
    if (!pitrSrc || !pitrTargetTime) return;
    setPitrPreviewing(true);
    try {
      // Browser local datetime input — convert to ISO with Z so the
      // backend's UTC normalisation is unambiguous.
      const targetIso = new Date(pitrTargetTime).toISOString();
      const preview = await previewPitr(pitrSrc.id, targetIso);
      setPitrPreview(preview);
    } catch (e: any) {
      showBanner('err', `Preview failed: ${e.message || e}`);
    } finally {
      setPitrPreviewing(false);
    }
  }

  async function confirmPitr() {
    if (!pitrSrc || !pitrTargetTime || !pitrNewName.trim()) return;
    setPitrRestoring(true);
    try {
      const targetIso = new Date(pitrTargetTime).toISOString();
      const result = await executePitr(pitrSrc.id, targetIso, pitrNewName.trim());
      await refresh();
      setPitrSrc(null);
      showBanner('ok',
        `Restored ${pitrSrc.database} to ${pitrTargetTime} → ${result.database} ` +
        `(${result.segmentsApplied} segment${result.segmentsApplied === 1 ? '' : 's'} replayed). ` +
        `Visit Studio to query it.`);
    } catch (e: any) {
      showBanner('err', `PITR failed: ${e.message || e}`);
    } finally {
      setPitrRestoring(false);
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
        <button onClick={flushNow} style={ghostBtn()} title="Force every archive-enabled DB to flush dirty pages now (so the next PITR target can include the latest writes)">↻ Flush archive</button>
        <button onClick={() => setSettingsOpen(s => !s)} style={ghostBtn()}>⚙ Settings</button>
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
            const archive = archiveStatus[db.name];
            const archiveOn = archive?.enabled ?? false;
            const isArchiveBusy = archiveBusy === db.name;
            return (
              <div key={db.name} className="card">
                <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
                  <div style={{ flex: 1 }}>
                    <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                      <span style={{ fontWeight: 600, fontSize: 16 }}>{db.name}</span>
                      {/* Issue #88 — PITR badge. Green when continuous
                          archive is on, gray when off. Hover for status. */}
                      {archive && (
                        <span
                          title={archiveOn
                            ? `Continuous archive ON. ${archive.segmentsThisSession} segment${archive.segmentsThisSession === 1 ? '' : 's'} this session, last shipped ${archive.lastShippedAtUtc ? new Date(archive.lastShippedAtUtc).toLocaleTimeString() : 'never'}.`
                            : 'Continuous archive OFF — PITR not available, only snapshot-level restores.'}
                          style={{
                            background: archiveOn ? 'rgba(40,160,80,0.12)' : 'rgba(100,100,100,0.08)',
                            color: archiveOn ? 'rgb(20,120,60)' : 'var(--gray-500)',
                            fontSize: 10, fontWeight: 700, fontFamily: 'var(--mono)',
                            letterSpacing: '0.05em', textTransform: 'uppercase',
                            padding: '2px 8px',
                          }}
                        >
                          {archiveOn ? `● PITR ON · seq ${archive.nextSequence}` : '○ PITR off'}
                        </span>
                      )}
                    </div>
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
                    onClick={() => toggleArchive(db)}
                    disabled={isArchiveBusy}
                    style={ghostBtn()}
                    title={archiveOn
                      ? 'Stop shipping WAL segments on flush. Existing segments stay on disk.'
                      : 'Ship every page write to the backup directory on each flush. Required for point-in-time restore.'}
                  >
                    {isArchiveBusy ? '…' : archiveOn ? 'Disable PITR' : 'Enable PITR'}
                  </button>
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
                              {archiveOn && (
                                <button
                                  onClick={() => openPitrWizard(r)}
                                  style={ghostBtn(2)}
                                  title="Restore to any moment in time between this backup and the latest archived segment"
                                >⏱ Restore to point-in-time</button>
                              )}
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
        💡 Hot snapshots use the engine's <code style={{ fontFamily: 'var(--mono)' }}>Snapshot()</code> primitive (consistent on-disk checkpoint, no writes blocked beyond the fsync). When PITR is enabled, every page write is archived as a WAL segment on each flush (~5s cadence). Restore-to-point-in-time replays segments forward from a base backup to any moment up to the latest flush. Coming next: scheduled backups (the <code style={{ fontFamily: 'var(--mono)' }}>scheduleCron</code> config field is reserved), offsite shipping (S3 / Azure Blob), cluster-wide coordinated PITR across shards.
      </div>

      {/* Issue #88 — PITR wizard. Timeline visualisation + datetime
          picker + preview. Operator picks a target time between
          backup.createdAt and the latest segment's archivedAt; we
          show how many segments + bytes would replay before they
          commit. Restore always creates a NEW DB (never clobbers). */}
      {pitrSrc && (() => {
        const baseTime = new Date(pitrSrc.createdAtUtc).getTime();
        const latestSeg = pitrSegments.length > 0
          ? new Date(pitrSegments[pitrSegments.length - 1].archivedAtUtc).getTime()
          : baseTime;
        const totalSpan = Math.max(latestSeg - baseTime, 1);
        const targetMs = pitrTargetTime ? new Date(pitrTargetTime).getTime() : baseTime;
        const targetPct = Math.max(0, Math.min(100, ((targetMs - baseTime) / totalSpan) * 100));
        return (
          <div
            onClick={() => !pitrRestoring && setPitrSrc(null)}
            style={{
              position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.5)',
              display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 60,
            }}
          >
            <div onClick={e => e.stopPropagation()} style={{
              background: 'white', padding: 24, maxWidth: 820, width: '94vw',
              maxHeight: '90vh', overflow: 'auto',
              border: '1px solid var(--gray-200)',
            }}>
              <h2 style={{ margin: 0, fontSize: 20, marginBottom: 4 }}>⏱ Restore to point-in-time</h2>
              <div style={{ fontSize: 13, color: 'var(--gray-700)', marginBottom: 14, lineHeight: 1.5 }}>
                Roll forward from a base backup to <strong>any moment</strong> between when it was taken and the most recent archived flush. Restore goes to a <strong>new database name</strong> — the source is never touched.
              </div>

              {/* Timeline visualisation */}
              <div style={{ marginBottom: 18 }}>
                <div style={{ fontSize: 11, fontFamily: 'var(--mono)', color: 'var(--gray-500)', marginBottom: 6, display: 'flex', justifyContent: 'space-between' }}>
                  <span>● base backup · {new Date(pitrSrc.createdAtUtc).toLocaleString()}</span>
                  <span>{pitrSegments.length} segment{pitrSegments.length === 1 ? '' : 's'} on the timeline</span>
                  <span>latest segment · {latestSeg > baseTime ? new Date(latestSeg).toLocaleString() : '—'}</span>
                </div>
                <div style={{
                  position: 'relative', height: 40, background: 'var(--gray-100)',
                  border: '1px solid var(--gray-200)',
                }}>
                  {/* Segment markers */}
                  {pitrSegments.map(s => {
                    const t = new Date(s.archivedAtUtc).getTime();
                    const pct = Math.max(0, Math.min(100, ((t - baseTime) / totalSpan) * 100));
                    return (
                      <div
                        key={s.sequenceNumber}
                        title={`seq ${s.sequenceNumber} · ${new Date(s.archivedAtUtc).toLocaleString()} · ${(s.byteCount / 1024).toFixed(1)} KB · ${s.recordCount} records`}
                        style={{
                          position: 'absolute', left: `${pct}%`, top: 4, bottom: 4,
                          width: 2, background: 'var(--gray-500)',
                        }}
                      />
                    );
                  })}
                  {/* Target time indicator */}
                  <div style={{
                    position: 'absolute', left: `${targetPct}%`, top: -4, bottom: -4,
                    width: 3, background: 'var(--red)', transform: 'translateX(-1.5px)',
                  }} />
                  <div style={{
                    position: 'absolute', left: `${targetPct}%`, top: -22,
                    transform: 'translateX(-50%)', fontSize: 11, fontFamily: 'var(--mono)',
                    color: 'var(--red)', fontWeight: 600, whiteSpace: 'nowrap',
                  }}>
                    target ▼
                  </div>
                </div>
              </div>

              {/* Target picker + new name */}
              <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 14, marginBottom: 14 }}>
                <div>
                  <label style={labelStyle()}>Recover to (your local time)</label>
                  <input
                    type="datetime-local"
                    step="1"
                    value={pitrTargetTime}
                    onChange={e => { setPitrTargetTime(e.target.value); setPitrPreview(null); }}
                    style={inputStyle()}
                  />
                  <div style={{ fontSize: 11, color: 'var(--gray-500)', marginTop: 4 }}>
                    earliest: {new Date(pitrSrc.createdAtUtc).toLocaleString()}
                  </div>
                </div>
                <div>
                  <label style={labelStyle()}>Restore as new database name</label>
                  <input
                    type="text"
                    value={pitrNewName}
                    onChange={e => setPitrNewName(e.target.value)}
                    style={inputStyle()}
                  />
                </div>
              </div>

              <div style={{ display: 'flex', gap: 8, marginBottom: 14 }}>
                <button onClick={previewPitrNow} disabled={pitrPreviewing || !pitrTargetTime} style={ghostBtn()}>
                  {pitrPreviewing ? 'Previewing…' : '⌕ Preview what would replay'}
                </button>
              </div>

              {pitrPreview && (
                <div style={{
                  padding: 12, marginBottom: 14,
                  background: pitrPreview.feasibleToRestore ? 'rgba(40,160,80,0.08)' : 'rgba(217,4,41,0.08)',
                  color: pitrPreview.feasibleToRestore ? 'rgb(20,120,60)' : 'var(--red)',
                  fontFamily: 'var(--mono)', fontSize: 12,
                }}>
                  {pitrPreview.feasibleToRestore ? (
                    <>
                      ✓ Feasible · {pitrPreview.segmentCount} segment{pitrPreview.segmentCount === 1 ? '' : 's'} · {(pitrPreview.bytesToReplay / 1024).toFixed(1)} KB of page writes will replay
                      {pitrPreview.sequenceGaps.length > 0 && (
                        <div style={{ color: 'var(--red)', marginTop: 6 }}>
                          ⚠ {pitrPreview.sequenceGaps.length} sequence gap{pitrPreview.sequenceGaps.length === 1 ? '' : 's'} detected — some segments are missing. Restore will proceed but those page writes won't be applied. Acceptable when later writes overwrite the missing pages.
                        </div>
                      )}
                    </>
                  ) : (
                    <>✗ Cannot restore: {pitrPreview.refusalReason}</>
                  )}
                </div>
              )}

              <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end' }}>
                <button onClick={() => setPitrSrc(null)} disabled={pitrRestoring} style={ghostBtn()}>Cancel</button>
                <button
                  onClick={confirmPitr}
                  disabled={pitrRestoring || !pitrTargetTime || !pitrNewName.trim() || (pitrPreview ? !pitrPreview.feasibleToRestore : false)}
                  style={primaryBtn(pitrRestoring || !pitrTargetTime || !pitrNewName.trim() || (pitrPreview ? !pitrPreview.feasibleToRestore : false))}
                >
                  {pitrRestoring ? 'Restoring…' : '⏱ Commit point-in-time restore'}
                </button>
              </div>
            </div>
          </div>
        );
      })()}
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
