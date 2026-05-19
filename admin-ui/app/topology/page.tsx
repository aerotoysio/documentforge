'use client';

// Issue #66 Phase 2.6 — Studio Topology page.
//
// One react-flow canvas per active connection. Every attached database on
// that service shows up as a node; replication relationships render as
// directed edges. "+ Add database" creates a new local DB (POST /databases);
// "Set leader on port…" / "Follow this DB" wire up replication via the
// scoped Phase 2.5 endpoints. The whole topology is queryable, configurable,
// and re-arrangeable from this one page.
//
// React Flow gives us the canvas primitives; everything else (data flow,
// edge derivation, node positioning persistence) is hand-rolled to match
// the engine's actual data model — not a generic graph.

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  ReactFlow,
  Background,
  Controls,
  MiniMap,
  useNodesState,
  useEdgesState,
  ReactFlowProvider,
  type Node,
  type Edge,
  type NodeChange,
  type Connection as ReactFlowConnection,
  type NodeMouseHandler,
  applyNodeChanges,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { useConnections } from '@/lib/connections-context';
import type { Connection } from '@/lib/connections';
import {
  listDatabases,
  createDatabase,
  deleteDatabase,
  setDefaultDatabase,
  getDbReplicationStatus,
  listDbCollections,
  startDbAsLeader,
  startDbAsFollower,
  type DatabaseEntry,
  type PerDbReplicationStatus,
} from '@/lib/api';
import { DBNode, type DBNodeData } from './db-node';

const POSITIONS_KEY = 'dfdb_topology_positions';

interface SnapshotEntry {
  db: DatabaseEntry;
  status: PerDbReplicationStatus | null;
}

interface SelectedDbInfo {
  name: string;
  status: PerDbReplicationStatus | null;
  isDefault: boolean;
  filePath: string;
}

export default function TopologyPage() {
  return (
    <ReactFlowProvider>
      <TopologyInner />
    </ReactFlowProvider>
  );
}

type TopologyView = 'graph' | 'list' | 'across' | 'collections';

function TopologyInner() {
  const { active, connections: connectionsForAcrossView } = useConnections();
  const [nodes, setNodes] = useNodesState<Node<DBNodeData>>([]);
  const [edges, setEdges] = useEdgesState<Edge>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [snapshot, setSnapshot] = useState<SnapshotEntry[]>([]);
  const [selectedDb, setSelectedDb] = useState<string | null>(null);
  const [showAddModal, setShowAddModal] = useState(false);
  const [actionBusy, setActionBusy] = useState(false);
  // Consolidation pass: Topology is now the unified DB hub. View toggle
  // determines which lens to show. Default to graph — the new headline view.
  const [view, setView] = useState<TopologyView>('graph');

  // Drag-to-wire state — opens a confirmation when the user drags a handle
  // from one DB to another. Source becomes the leader, target the follower;
  // the modal lets them tweak the port before committing.
  const [wireDraft, setWireDraft] = useState<{ sourceName: string; targetName: string; suggestedPort: number } | null>(null);

  // Keep a ref to the latest snapshot for handlers that capture stale state.
  const snapshotRef = useRef<SnapshotEntry[]>([]);
  useEffect(() => { snapshotRef.current = snapshot; }, [snapshot]);

  const nodeTypes = useMemo(() => ({ database: DBNode }), []);

  const refresh = useCallback(async () => {
    if (!active) return;
    setLoading(true);
    setError(null);
    try {
      const list = await listDatabases({ connection: active });
      // Per-DB replication status — fan out, tolerate per-DB failures.
      const statuses = await Promise.all(list.databases.map(async db => {
        try {
          return await getDbReplicationStatus(db.name, { connection: active });
        } catch { return null; }
      }));
      const snap: SnapshotEntry[] = list.databases.map((db, i) => ({
        db, status: statuses[i],
      }));
      setSnapshot(snap);
      setNodes(buildNodes(snap));
      setEdges(buildEdges(snap));
    } catch (e: any) {
      setError(e.message || String(e));
    } finally {
      setLoading(false);
    }
  }, [active, setNodes, setEdges]);

  useEffect(() => { refresh(); /* eslint-disable-next-line */ }, [active?.id]);

  // Periodically re-poll status so follower counts, applied-seq, and
  // newly-attached DBs land without the user hitting refresh. 5s feels
  // live without hammering the service.
  useEffect(() => {
    if (!active) return;
    const id = window.setInterval(() => {
      // Silent refresh — only updates state if anything changed
      // (buildNodes derives positions from current state when available).
      refreshSilent();
    }, 5000);
    return () => window.clearInterval(id);
    // eslint-disable-next-line
  }, [active?.id]);

  async function refreshSilent() {
    if (!active) return;
    try {
      const list = await listDatabases({ connection: active });
      const statuses = await Promise.all(list.databases.map(async db => {
        try { return await getDbReplicationStatus(db.name, { connection: active }); }
        catch { return null; }
      }));
      const snap: SnapshotEntry[] = list.databases.map((db, i) => ({ db, status: statuses[i] }));
      setSnapshot(snap);
      // Diff-update nodes so dragged positions persist between polls.
      setNodes(prev => mergeNodes(prev, buildNodes(snap)));
      setEdges(buildEdges(snap));
    } catch { /* swallow — banner will show on next manual refresh */ }
  }

  // Persist drag positions so a refresh doesn't rearrange the user's layout.
  const onNodesChange = useCallback((changes: NodeChange<Node<DBNodeData>>[]) => {
    setNodes((nds) => {
      const next = applyNodeChanges(changes, nds);
      // Save positions whenever a drag completes (not every intermediate tick).
      if (changes.some(c => c.type === 'position' && (c as any).dragging === false)) {
        savePositions(next);
      }
      return next;
    });
  }, [setNodes]);

  const onNodeClick = useCallback((_e: React.MouseEvent, node: Node) => {
    setSelectedDb(node.id);
  }, []);

  const selected: SelectedDbInfo | null = useMemo(() => {
    if (!selectedDb) return null;
    const entry = snapshot.find(s => s.db.name === selectedDb);
    if (!entry) return null;
    return {
      name: entry.db.name,
      filePath: entry.db.filePath,
      isDefault: entry.db.isDefault,
      status: entry.status,
    };
  }, [selectedDb, snapshot]);

  // ---- Action handlers ----

  async function handleAddDatabase(name: string, path: string) {
    setActionBusy(true);
    try {
      await createDatabase(name, { path: path || undefined, connection: active ?? undefined });
      setShowAddModal(false);
      await refresh();
    } catch (e: any) {
      alert(`Could not create: ${e.message || String(e)}`);
    } finally {
      setActionBusy(false);
    }
  }

  async function handleStartLeader(port: number) {
    if (!selectedDb) return;
    setActionBusy(true);
    try {
      await startDbAsLeader(selectedDb, port, { connection: active ?? undefined });
      await refresh();
    } catch (e: any) {
      alert(`Could not start leader: ${e.message || String(e)}`);
    } finally {
      setActionBusy(false);
    }
  }

  async function handleFollowLeader(leaderName: string) {
    if (!selectedDb) return;
    const leaderEntry = snapshot.find(s => s.db.name === leaderName);
    const leaderPort = leaderEntry?.status?.leader?.port;
    if (!leaderPort) {
      alert(`Database "${leaderName}" isn't currently a leader. Start it as leader first.`);
      return;
    }
    setActionBusy(true);
    try {
      // Intra-service follow uses loopback.
      await startDbAsFollower(selectedDb, 'localhost', leaderPort, { connection: active ?? undefined });
      await refresh();
    } catch (e: any) {
      alert(`Could not follow: ${e.message || String(e)}`);
    } finally {
      setActionBusy(false);
    }
  }

  // Follow a leader running on a different service. Useful when you want
  // a DB hosted here to be a hot-standby of a leader on a different host.
  async function handleFollowExternal(host: string, port: number) {
    if (!selectedDb) return;
    setActionBusy(true);
    try {
      await startDbAsFollower(selectedDb, host, port, { connection: active ?? undefined });
      await refresh();
    } catch (e: any) {
      alert(`Could not follow ${host}:${port}: ${e.message || String(e)}`);
    } finally {
      setActionBusy(false);
    }
  }

  async function handleMakeActive() {
    if (!selectedDb) return;
    setActionBusy(true);
    try {
      await setDefaultDatabase(selectedDb, { connection: active ?? undefined });
      await refresh();
    } catch (e: any) {
      alert(`Could not set active: ${e.message || String(e)}`);
    } finally {
      setActionBusy(false);
    }
  }

  // Drag-from-handle-to-handle on the Graph canvas. Source must end up
  // leader, target must end up follower. We don't commit anything from
  // this callback — a confirmation modal pops first so the user can
  // sanity-check the port + the direction before any HTTP calls go out.
  const handleGraphConnect = useCallback((params: ReactFlowConnection) => {
    if (!params.source || !params.target || params.source === params.target) return;
    const sourceEntry = snapshotRef.current.find(s => s.db.name === params.source);
    const targetEntry = snapshotRef.current.find(s => s.db.name === params.target);
    if (!sourceEntry || !targetEntry) return;
    // If the source isn't already leading, suggest a port. Otherwise
    // re-use the leader's current port so the modal reads as "follow
    // the existing leader on :X" rather than "set up a new leader."
    const suggestedPort = sourceEntry.status?.leader?.port
      ?? suggestPortForName(sourceEntry.db.name);
    setWireDraft({
      sourceName: params.source,
      targetName: params.target,
      suggestedPort,
    });
  }, []);

  async function handleWireConfirm(port: number) {
    if (!wireDraft) return;
    setActionBusy(true);
    try {
      const sourceEntry = snapshotRef.current.find(s => s.db.name === wireDraft.sourceName);
      // Promote source to leader if it isn't already.
      if (sourceEntry?.status?.role !== 'leader') {
        await startDbAsLeader(wireDraft.sourceName, port, { connection: active ?? undefined });
      }
      // Always (re)wire target as follower of source on the chosen port.
      await startDbAsFollower(wireDraft.targetName, 'localhost', port, { connection: active ?? undefined });
      setWireDraft(null);
      await refresh();
    } catch (e: any) {
      alert(`Could not wire replication: ${e.message || String(e)}`);
    } finally {
      setActionBusy(false);
    }
  }

  async function handleDrop(deleteFiles: boolean) {
    if (!selectedDb) return;
    const verb = deleteFiles ? 'DROP' : 'detach';
    const msg = deleteFiles
      ? `DROP "${selectedDb}" and delete files? This is irreversible.`
      : `Detach "${selectedDb}"? The .dfdb file stays on disk.`;
    if (!confirm(msg)) return;
    setActionBusy(true);
    try {
      await deleteDatabase(selectedDb, { deleteFiles, connection: active ?? undefined });
      setSelectedDb(null);
      await refresh();
    } catch (e: any) {
      alert(`${verb} failed: ${e.message || String(e)}`);
    } finally {
      setActionBusy(false);
    }
  }

  // ---- Render ----

  if (!active) {
    return (
      <div style={{ padding: 24 }}>
        <h1 className="page-title">Topology</h1>
        <div className="card">
          <p>No connection selected. Pick one in the sidebar.</p>
        </div>
      </div>
    );
  }

  return (
    <div className="topology-page">
      {/* Header */}
      <div className="topology-header">
        <div>
          <div className="eyebrow">{active.name}</div>
          <h1 className="page-title" style={{ margin: 0 }}>
            Topology
            <span style={{ color: 'var(--gray-500)', fontSize: 14, fontWeight: 500, marginLeft: 10 }}>
              {snapshot.length} database{snapshot.length === 1 ? '' : 's'}
              {snapshot.filter(s => s.status?.role === 'leader').length > 0 && (
                <span style={{ marginLeft: 8 }}>
                  · {snapshot.filter(s => s.status?.role === 'leader').length} leader
                  {snapshot.filter(s => s.status?.role === 'leader').length === 1 ? '' : 's'}
                </span>
              )}
              {snapshot.filter(s => s.status?.role === 'follower').length > 0 && (
                <span style={{ marginLeft: 8 }}>
                  · {snapshot.filter(s => s.status?.role === 'follower').length} follower
                  {snapshot.filter(s => s.status?.role === 'follower').length === 1 ? '' : 's'}
                </span>
              )}
            </span>
          </h1>
        </div>
        <div style={{ display: 'flex', gap: 8 }}>
          <button onClick={refresh} disabled={loading} style={ghostBtn()}>⟲ Refresh</button>
          <button onClick={() => relayout(snapshot, setNodes)} style={ghostBtn()} title="Re-run auto-layout (resets node positions)">⤢ Auto-layout</button>
          <button onClick={() => setShowAddModal(true)} style={primaryBtn()}>+ Add Database</button>
        </div>
      </div>

      {/* View tabs — Graph is the headline; List is the table view (lifted
          from the old /databases page); Across services hosts the future
          sharding / multi-connection topology (Phase 5). */}
      <div className="topology-tabs">
        <button
          onClick={() => setView('graph')}
          className={`topology-tab ${view === 'graph' ? 'active' : ''}`}
        >
          ⌬ Graph
        </button>
        <button
          onClick={() => setView('list')}
          className={`topology-tab ${view === 'list' ? 'active' : ''}`}
        >
          ☰ List
        </button>
        <button
          onClick={() => setView('across')}
          className={`topology-tab ${view === 'across' ? 'active' : ''}`}
        >
          🐝 Across services
        </button>
        <button
          onClick={() => setView('collections')}
          className={`topology-tab ${view === 'collections' ? 'active' : ''}`}
        >
          📚 Collections
        </button>
        <span className="topology-tab-spacer" />
      </div>

      {error && (
        <div className="card" style={{ borderLeft: '4px solid var(--red)', background: 'rgba(217,4,41,0.04)', margin: '0 0 16px' }}>
          <strong style={{ color: 'var(--red)' }}>Could not load topology.</strong>
          <div style={{ fontFamily: 'var(--mono)', fontSize: 12, marginTop: 6 }}>{error}</div>
          <div style={{ fontSize: 12, color: 'var(--gray-500)', marginTop: 6 }}>
            The connected service may predate Issue #66 — Topology needs a build that includes /databases and /db/{'{name}'}/replication routes.
          </div>
        </div>
      )}

      {view === 'list' && (
        <ListView
          snapshot={snapshot}
          busy={actionBusy}
          onSelect={(name) => { setView('graph'); setSelectedDb(name); }}
          onSetActive={async (name) => {
            setActionBusy(true);
            try { await setDefaultDatabase(name, { connection: active ?? undefined }); await refresh(); }
            catch (e: any) { alert(`Could not set active: ${e.message || String(e)}`); }
            finally { setActionBusy(false); }
          }}
          onDrop={async (name, deleteFiles) => {
            const verb = deleteFiles ? 'DROP' : 'detach';
            const msg = deleteFiles
              ? `DROP "${name}" and delete files? This is irreversible.`
              : `Detach "${name}"? The .dfdb file stays on disk.`;
            if (!confirm(msg)) return;
            setActionBusy(true);
            try { await deleteDatabase(name, { deleteFiles, connection: active ?? undefined }); await refresh(); }
            catch (e: any) { alert(`${verb} failed: ${e.message || String(e)}`); }
            finally { setActionBusy(false); }
          }}
        />
      )}

      {view === 'across' && <AcrossServicesView connections={[...connectionsForAcrossView]} />}

      {view === 'collections' && <CollectionsView connections={[...connectionsForAcrossView]} />}

      {view === 'graph' && (
      /* Canvas + side panel layout */
      <div className={`topology-stage ${selected ? '' : 'no-side'}`}>
        <div className="topology-canvas">
          <ReactFlow
            nodes={nodes}
            edges={edges}
            nodeTypes={nodeTypes}
            onNodesChange={onNodesChange}
            onNodeClick={onNodeClick}
            onPaneClick={() => setSelectedDb(null)}
            onConnect={handleGraphConnect}
            connectionLineStyle={{ stroke: 'var(--red)', strokeWidth: 2, strokeDasharray: '6 4' }}
            fitView
            proOptions={{ hideAttribution: true }}
            defaultEdgeOptions={{
              animated: true,
              style: { stroke: 'var(--red)', strokeWidth: 2 },
            }}
          >
            <Background gap={20} size={1} color="#e6e6e6" />
            <Controls showInteractive={false} />
            <MiniMap nodeColor={n => {
              const role = (n.data as DBNodeData)?.role;
              if (role === 'leader') return '#d90429';
              if (role === 'follower') return '#3b82f6';
              return '#a3a3a3';
            }} />
          </ReactFlow>

          <Legend />
        </div>

        {selected && (
          <SidePanel
            info={selected}
            availableLeaders={snapshot.filter(s =>
              s.db.name !== selected.name && s.status?.role === 'leader',
            )}
            busy={actionBusy}
            onClose={() => setSelectedDb(null)}
            onStartLeader={handleStartLeader}
            onFollow={handleFollowLeader}
            onFollowExternal={handleFollowExternal}
            onMakeActive={handleMakeActive}
            onDrop={handleDrop}
          />
        )}
      </div>
      )}

      {showAddModal && (
        <AddDatabaseModal
          busy={actionBusy}
          onCancel={() => setShowAddModal(false)}
          onSubmit={handleAddDatabase}
        />
      )}

      {wireDraft && (
        <WireReplicationModal
          busy={actionBusy}
          source={wireDraft.sourceName}
          target={wireDraft.targetName}
          suggestedPort={wireDraft.suggestedPort}
          onCancel={() => setWireDraft(null)}
          onConfirm={handleWireConfirm}
        />
      )}
    </div>
  );
}

// Same hash → stable suggested port that doesn't jitter on rerenders.
function suggestPortForName(name: string): number {
  let h = 0;
  for (let i = 0; i < name.length; i++) h = (h * 31 + name.charCodeAt(i)) | 0;
  return 5500 + (Math.abs(h) % 100);
}

// Strip protocol + port from a baseUrl to get the host the follower
// should dial. Localhost stays localhost; a public service hosted at
// https://node-a.example.com:5000 turns into "node-a.example.com".
function hostnameFromBaseUrl(baseUrl: string): string {
  try { return new URL(baseUrl).hostname; }
  catch { return baseUrl.replace(/^https?:\/\//, '').split(':')[0].split('/')[0]; }
}

interface WireModalProps {
  busy: boolean;
  source: string;
  target: string;
  suggestedPort: number;
  onCancel: () => void;
  onConfirm: (port: number) => void;
}

// Confirmation that pops after a handle-to-handle drag. Shows the
// "source becomes leader" arrow, the chosen port, and a single Confirm
// button. Click anywhere outside cancels (no accidental commits).
function WireReplicationModal({ busy, source, target, suggestedPort, onCancel, onConfirm }: WireModalProps) {
  const [port, setPort] = useState(suggestedPort);
  return (
    <div className="topology-modal-backdrop" onClick={onCancel}>
      <div className="topology-modal" onClick={e => e.stopPropagation()}>
        <div style={{ fontFamily: 'var(--mono)', fontSize: 11, letterSpacing: '0.08em', textTransform: 'uppercase', color: 'var(--red)', fontWeight: 700, marginBottom: 12 }}>
          Wire replication
        </div>
        <div style={{ marginBottom: 16, display: 'flex', alignItems: 'center', gap: 12, fontSize: 14 }}>
          <span style={{ padding: '6px 12px', background: 'rgba(217,4,41,0.08)', border: '1px solid var(--red)', fontWeight: 600 }}>{source}</span>
          <span style={{ color: 'var(--gray-500)' }}>(leader)</span>
          <span style={{ color: 'var(--red)', fontSize: 18 }}>→</span>
          <span style={{ padding: '6px 12px', background: 'rgba(59,130,246,0.08)', border: '1px solid #3b82f6', fontWeight: 600 }}>{target}</span>
          <span style={{ color: 'var(--gray-500)' }}>(follower)</span>
        </div>
        <div style={{ marginBottom: 16 }}>
          <label style={{ display: 'block', fontFamily: 'var(--mono)', fontSize: 10, letterSpacing: '0.08em', textTransform: 'uppercase', color: 'var(--gray-500)', marginBottom: 4, fontWeight: 700 }}>
            Replication TCP port
          </label>
          <input
            type="number"
            value={port}
            onChange={e => setPort(parseInt(e.target.value) || 0)}
            style={{ width: 140, padding: '8px 10px', fontSize: 14, border: '1px solid var(--gray-200)', fontFamily: 'var(--mono)' }}
          />
          <div style={{ marginTop: 6, fontSize: 11, color: 'var(--gray-500)' }}>
            Source listens on this port for the follower's connection. Must be free + ≥ 1024.
          </div>
        </div>
        <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end' }}>
          <button type="button" onClick={onCancel} style={{ background: 'transparent', color: 'var(--ink)', border: '1px solid var(--gray-200)', padding: '6px 12px', fontSize: 12, cursor: 'pointer' }}>
            Cancel
          </button>
          <button
            type="button"
            onClick={() => onConfirm(port)}
            disabled={busy || port < 1024}
            style={{ background: 'var(--red)', color: 'white', border: 'none', padding: '8px 18px', fontSize: 12, fontWeight: 700, cursor: 'pointer' }}
          >
            {busy ? 'Wiring…' : 'Wire replication'}
          </button>
        </div>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------- list view

interface ListViewProps {
  snapshot: SnapshotEntry[];
  busy: boolean;
  onSelect: (name: string) => void;
  onSetActive: (name: string) => void;
  onDrop: (name: string, deleteFiles: boolean) => void;
}

// Table-style view of attached databases — lifted from the now-deprecated
// /databases page so the consolidation lands all DB-management work in one
// place. Each row links back to the graph view's side panel for replication
// configuration; inline actions cover the fast common cases (set active,
// detach, drop) without context-switching.
function ListView({ snapshot, busy, onSelect, onSetActive, onDrop }: ListViewProps) {
  if (snapshot.length === 0) {
    return (
      <div className="card">
        <p>No databases attached yet.</p>
        <p style={{ color: 'var(--gray-500)', fontSize: 13 }}>
          Add one via "+ Add Database" above. The service hosts as many as you like — one process, N attached .dfdb files.
        </p>
      </div>
    );
  }
  return (
    <div style={{ display: 'grid', gap: 10 }}>
      {snapshot.map(({ db, status }) => {
        const role = status?.role ?? 'unknown';
        const accent = role === 'leader' ? 'var(--red)'
          : role === 'follower' ? '#3b82f6'
          : 'var(--gray-200)';
        return (
          <div
            key={db.name}
            className="card"
            style={{
              borderLeft: `4px solid ${db.isDefault ? 'var(--red)' : accent}`,
              display: 'grid',
              gridTemplateColumns: '1fr auto',
              alignItems: 'center',
              gap: 16,
            }}
          >
            <div onClick={() => onSelect(db.name)} style={{ cursor: 'pointer' }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
                <span style={{ fontSize: 16, fontWeight: 600 }}>{db.name}</span>
                {db.isDefault && <span className="pill red" style={{ fontSize: 10 }}>ACTIVE</span>}
                {role === 'leader' && (
                  <>
                    <span className="pill red" style={{ fontSize: 10 }}>LEADER</span>
                    {status?.leader.port != null && (
                      <span style={{ fontFamily: 'var(--mono)', fontSize: 11, color: 'var(--gray-500)' }}>
                        :{status.leader.port} · {status.leader.followerCount} follower{status.leader.followerCount === 1 ? '' : 's'}
                      </span>
                    )}
                  </>
                )}
                {role === 'follower' && (
                  <>
                    <span className="pill" style={{ background: '#dbeafe', color: '#1d4ed8', fontSize: 10 }}>FOLLOWER</span>
                    {status?.follower.leader?.endpoint && (
                      <span style={{ fontFamily: 'var(--mono)', fontSize: 11, color: 'var(--gray-500)' }}>
                        ← {status.follower.leader.endpoint}
                      </span>
                    )}
                  </>
                )}
                {role === 'none' && <span className="pill gray" style={{ fontSize: 10 }}>STANDALONE</span>}
              </div>
              <div style={{ fontFamily: 'var(--mono)', fontSize: 11, color: 'var(--gray-500)', marginTop: 4, wordBreak: 'break-all' }}>
                {db.filePath}
              </div>
            </div>
            <div style={{ display: 'flex', gap: 6 }}>
              {!db.isDefault && (
                <button onClick={() => onSetActive(db.name)} disabled={busy} style={ghostBtn()} title="Make this the default DB for flat routes">
                  Set active
                </button>
              )}
              <button onClick={() => onSelect(db.name)} disabled={busy} style={ghostBtn()} title="Open in graph view to configure replication">
                Configure
              </button>
              <button onClick={() => onDrop(db.name, false)} disabled={busy} style={ghostBtn()}>
                Detach
              </button>
              <button onClick={() => onDrop(db.name, true)} disabled={busy} style={dangerBtn()}>
                Drop
              </button>
            </div>
          </div>
        );
      })}
    </div>
  );
}

// ---------------------------------------------------------------- across-services view

interface AcrossSnapshot {
  conn: Connection;
  databases: DatabaseEntry[];
  statuses: (PerDbReplicationStatus | null)[];
  error: string | null;
}

// Phase 5 — every registered Connection contributes its attached DBs to
// one shared canvas. Cross-service replication edges connect a follower
// DB on one service to its leader DB on another (matched by TCP port).
// Each service gets its own column on the canvas with a labelled group
// box so the visual story stays clear at 5+ services.
function AcrossServicesView({ connections }: { connections: Connection[] }) {
  const { update: updateConnection } = useConnections();
  const [snapshots, setSnapshots] = useState<AcrossSnapshot[]>([]);
  const [loading, setLoading] = useState(true);
  const [nodes, setNodes] = useNodesState<Node>([]);
  const [edges, setEdges] = useEdgesState<Edge>([]);

  // Drag-to-wire (cross-service edition) — same modal as the Graph tab
  // but the source/target may live on different services, so the wire
  // calls fan out across two connections.
  const [wireDraft, setWireDraft] = useState<{
    sourceConn: Connection; sourceName: string;
    targetConn: Connection; targetName: string;
    suggestedPort: number;
  } | null>(null);
  const [wiring, setWiring] = useState(false);

  const nodeTypes = useMemo(() => ({ database: DBNode }), []);

  const refresh = useCallback(async () => {
    if (connections.length === 0) { setSnapshots([]); setLoading(false); return; }
    setLoading(true);
    const results = await Promise.all(connections.map(async (conn): Promise<AcrossSnapshot> => {
      try {
        const list = await listDatabases({ connection: conn });
        const statuses = await Promise.all(list.databases.map(db =>
          getDbReplicationStatus(db.name, { connection: conn }).catch(() => null),
        ));
        return { conn, databases: list.databases, statuses, error: null };
      } catch (e: any) {
        return { conn, databases: [], statuses: [], error: e.message || String(e) };
      }
    }));
    setSnapshots(results);
    setLoading(false);
  }, [connections]);

  useEffect(() => { refresh(); /* eslint-disable-next-line */ }, [connections.length, connections.map(c => c.id + c.baseUrl).join('|')]);

  // Auto-refresh every 5s so spawned services light up + replication
  // edges appear without manual refresh.
  useEffect(() => {
    const id = window.setInterval(refresh, 5000);
    return () => window.clearInterval(id);
  }, [refresh]);

  // Rebuild nodes + edges whenever snapshots change.
  useEffect(() => {
    if (snapshots.length === 0) { setNodes([]); setEdges([]); return; }
    const built = buildAcrossNodesAndEdges(snapshots);
    setNodes(built.nodes);
    setEdges(built.edges);
  }, [snapshots, setNodes, setEdges]);

  // Drag-from-handle on a DB node to another DB node, possibly on a
  // different service. Node IDs in this view are `db:<connId>/<dbName>`
  // so we parse both ends, look up the matching Connection objects,
  // and open the confirm dialog with a sensible suggested port.
  const handleAcrossConnect = useCallback((params: ReactFlowConnection) => {
    if (!params.source || !params.target || params.source === params.target) return;
    if (!params.source.startsWith('db:') || !params.target.startsWith('db:')) return;
    const [sourceConnId, sourceName] = params.source.slice('db:'.length).split('/');
    const [targetConnId, targetName] = params.target.slice('db:'.length).split('/');
    const sourceConn = connections.find(c => c.id === sourceConnId);
    const targetConn = connections.find(c => c.id === targetConnId);
    if (!sourceConn || !targetConn) return;
    const sourceSnap = snapshots.find(s => s.conn.id === sourceConnId);
    const sourceStatusIdx = sourceSnap?.databases.findIndex(d => d.name === sourceName) ?? -1;
    const sourceStatus = sourceStatusIdx >= 0 ? sourceSnap?.statuses[sourceStatusIdx] : null;
    const suggestedPort = sourceStatus?.leader?.port ?? suggestPortForName(sourceName);
    setWireDraft({ sourceConn, sourceName, targetConn, targetName, suggestedPort });
  }, [connections, snapshots]);

  // Drag-to-reshard: when a service column is dropped inside a different
  // shard frame, update the connection's shard tag. Studio-side only —
  // the backend doesn't track shards. The next refresh redraws the
  // column inside the new frame.
  const handleNodeDragStop = useCallback<NodeMouseHandler>((_event, node) => {
    if (!node.id.startsWith('conn:')) return;
    const connectionId = (node.data as any).connectionId as string | undefined;
    const previousShard = (node.data as any).currentShard as string | undefined;
    if (!connectionId) return;

    // Where did the column end up? Use the absolute centre to be tolerant
    // of large columns straddling frame borders.
    const colW = (node.width ?? (node.style as any)?.width ?? 280) as number;
    const colH = (node.height ?? (node.style as any)?.height ?? 100) as number;
    const abs = (node as any).positionAbsolute ?? node.position;
    const cx = abs.x + colW / 2;
    const cy = abs.y + colH / 2;

    // Find a shard frame whose bounds contain the centre.
    let targetShard: string | null = null;
    for (const candidate of nodes) {
      if (!candidate.id.startsWith('shard:')) continue;
      if (candidate.id === `shard:${previousShard}`) continue;
      const w = (candidate.style as any)?.width ?? 0;
      const h = (candidate.style as any)?.height ?? 0;
      const x = candidate.position.x;
      const y = candidate.position.y;
      if (cx >= x && cx <= x + w && cy >= y && cy <= y + h) {
        targetShard = candidate.id.slice('shard:'.length);
        break;
      }
    }
    if (!targetShard || targetShard === previousShard) return;

    // Commit the change. The next snapshot refresh will rebuild nodes
    // with the column inside the new frame; until then we leave the
    // user's drop position alone so the move "feels stable."
    const newShardValue = targetShard === '__unsharded__' ? undefined : targetShard;
    updateConnection(connectionId, { shard: newShardValue });
    refresh();
  }, [nodes, refresh, updateConnection]);

  async function handleAcrossWireConfirm(port: number) {
    if (!wireDraft) return;
    setWiring(true);
    try {
      const sourceSnap = snapshots.find(s => s.conn.id === wireDraft.sourceConn.id);
      const sourceIdx = sourceSnap?.databases.findIndex(d => d.name === wireDraft.sourceName) ?? -1;
      const sourceStatus = sourceIdx >= 0 ? sourceSnap?.statuses[sourceIdx] : null;
      // Promote source to leader on its own service if it isn't already.
      if (sourceStatus?.role !== 'leader') {
        await startDbAsLeader(wireDraft.sourceName, port, { connection: wireDraft.sourceConn });
      }
      // Target follows source's host (parsed from the source connection's
      // baseUrl). Cross-service replication just works because the
      // engines speak the same wire protocol over TCP.
      const sourceHost = hostnameFromBaseUrl(wireDraft.sourceConn.baseUrl);
      await startDbAsFollower(wireDraft.targetName, sourceHost, port, { connection: wireDraft.targetConn });
      setWireDraft(null);
      await refresh();
    } catch (e: any) {
      alert(`Could not wire ${wireDraft.sourceName} → ${wireDraft.targetName}: ${e.message || String(e)}`);
    } finally {
      setWiring(false);
    }
  }

  if (loading && snapshots.length === 0) {
    return <div className="card"><p>Probing every registered connection…</p></div>;
  }
  if (connections.length === 0) {
    return (
      <div className="card">
        <p>No connections registered yet.</p>
        <p style={{ color: 'var(--gray-500)', fontSize: 13 }}>
          Head to <a href="/connections" style={{ color: 'var(--red)' }}>Connections</a> and
          either add an existing endpoint or use "+ Spawn local service" to start a fresh one.
        </p>
      </div>
    );
  }

  // Quick stats roll-up for the header.
  const totalDbs = snapshots.reduce((s, x) => s + x.databases.length, 0);
  const leaderCount = snapshots.reduce((s, x) => s + x.statuses.filter(st => st?.role === 'leader').length, 0);
  const followerCount = snapshots.reduce((s, x) => s + x.statuses.filter(st => st?.role === 'follower').length, 0);
  const errored = snapshots.filter(x => x.error);

  return (
    <>
      <div className="card" style={{ marginBottom: 12, display: 'flex', alignItems: 'center', gap: 16 }}>
        <div style={{ fontSize: 14 }}>
          <strong>{snapshots.length}</strong> service{snapshots.length === 1 ? '' : 's'}
          · <strong>{totalDbs}</strong> database{totalDbs === 1 ? '' : 's'} total
          {leaderCount > 0 && <> · <strong>{leaderCount}</strong> leader{leaderCount === 1 ? '' : 's'}</>}
          {followerCount > 0 && <> · <strong>{followerCount}</strong> follower{followerCount === 1 ? '' : 's'}</>}
        </div>
        {errored.length > 0 && (
          <div style={{ fontSize: 12, color: 'var(--red)' }}>
            {errored.length} unreachable
          </div>
        )}
      </div>

      <div className="topology-stage no-side">
        <div className="topology-canvas">
          <ReactFlow
            nodes={nodes}
            edges={edges}
            nodeTypes={nodeTypes}
            onConnect={handleAcrossConnect}
            onNodeDragStop={handleNodeDragStop}
            connectionLineStyle={{ stroke: 'var(--red)', strokeWidth: 2, strokeDasharray: '6 4' }}
            fitView
            proOptions={{ hideAttribution: true }}
            defaultEdgeOptions={{
              animated: true,
              style: { stroke: 'var(--red)', strokeWidth: 2 },
            }}
          >
            <Background gap={20} size={1} color="#e6e6e6" />
            <Controls showInteractive={false} />
            <MiniMap />
          </ReactFlow>
        </div>
      </div>

      {wireDraft && (
        <WireReplicationModal
          busy={wiring}
          source={`${wireDraft.sourceConn.name} / ${wireDraft.sourceName}`}
          target={`${wireDraft.targetConn.name} / ${wireDraft.targetName}`}
          suggestedPort={wireDraft.suggestedPort}
          onCancel={() => setWireDraft(null)}
          onConfirm={handleAcrossWireConfirm}
        />
      )}

      {errored.length > 0 && (
        <div className="card" style={{ marginTop: 12, background: 'rgba(217,4,41,0.04)', borderLeft: '4px solid var(--red)' }}>
          <strong style={{ color: 'var(--red)' }}>Unreachable services</strong>
          <div style={{ fontSize: 12, marginTop: 6 }}>
            {errored.map(e => (
              <div key={e.conn.id} style={{ fontFamily: 'var(--mono)', marginBottom: 2 }}>
                {e.conn.name} ({e.conn.baseUrl}) — {e.error}
              </div>
            ))}
          </div>
        </div>
      )}
    </>
  );
}

interface BuiltCanvas { nodes: Node[]; edges: Edge[]; }

// Render: connections are grouped by their `shard` tag (Phase 5c). Each
// shard becomes a labelled meta-frame on the canvas containing its
// service columns; columns inside a shard cluster horizontally with a
// header row at the top showing "Shard: X". Connections without a shard
// tag go in an "Unsharded" frame on the right.
function buildAcrossNodesAndEdges(snapshots: AcrossSnapshot[]): BuiltCanvas {
  const COL_WIDTH = 280;
  const COL_GAP = 40;
  const ROW_HEIGHT = 110;
  const HEADER_HEIGHT = 60;
  const SHARD_GAP = 60;
  const SHARD_PADDING_X = 20;
  const SHARD_LABEL_HEIGHT = 36;

  // Group snapshots by shard tag. Stable ordering: shards alphabetically,
  // "(unsharded)" last so the eye reads grouped clusters first.
  const grouped = new Map<string, AcrossSnapshot[]>();
  for (const snap of snapshots) {
    const key = (snap.conn.shard?.trim() || '__unsharded__');
    if (!grouped.has(key)) grouped.set(key, []);
    grouped.get(key)!.push(snap);
  }
  const shardKeys = Array.from(grouped.keys()).sort((a, b) => {
    if (a === '__unsharded__') return 1;
    if (b === '__unsharded__') return -1;
    return a.localeCompare(b);
  });

  const nodes: Node[] = [];
  let cursorX = 0;

  for (const shardKey of shardKeys) {
    const members = grouped.get(shardKey)!;
    const shardWidth = SHARD_PADDING_X * 2 + members.length * COL_WIDTH + Math.max(0, members.length - 1) * COL_GAP;
    // Shard frame height = label + tallest column inside it.
    const maxRows = Math.max(1, ...members.map(m => m.databases.length));
    const colHeight = HEADER_HEIGHT + maxRows * ROW_HEIGHT + 12;
    const shardHeight = SHARD_LABEL_HEIGHT + colHeight + 16;

    const isUnsharded = shardKey === '__unsharded__';
    const shardId = `shard:${shardKey}`;
    const shardLabel = isUnsharded ? 'Unsharded' : `Shard · ${shardKey}`;
    const shardAccent = isUnsharded ? 'var(--gray-200)' : 'var(--red)';
    const shardBackground = isUnsharded ? 'rgba(245,245,245,0.4)' : 'rgba(217,4,41,0.03)';

    // Shard meta-frame (parent of service columns).
    nodes.push({
      id: shardId,
      type: 'group',
      position: { x: cursorX, y: 0 },
      data: { label: shardLabel },
      style: {
        width: shardWidth,
        height: shardHeight,
        background: shardBackground,
        border: `1.5px ${isUnsharded ? 'dashed' : 'solid'} ${shardAccent}`,
        borderRadius: 2,
      },
    });

    // Shard label header — non-interactive, sits at the top of the frame.
    nodes.push({
      id: `${shardId}/label`,
      type: 'default',
      parentId: shardId,
      extent: 'parent',
      draggable: false,
      selectable: false,
      position: { x: 0, y: 0 },
      data: {
        label: (
          <div style={{ padding: '6px 12px', textAlign: 'left' }}>
            <div style={{ fontFamily: 'var(--mono)', fontSize: 10, letterSpacing: '0.08em', textTransform: 'uppercase', fontWeight: 700, color: isUnsharded ? 'var(--gray-500)' : 'var(--red)' }}>
              {shardLabel}
            </div>
            <div style={{ fontSize: 11, color: 'var(--gray-500)', marginTop: 2 }}>
              {members.length} service{members.length === 1 ? '' : 's'} · {members.reduce((s, m) => s + m.databases.length, 0)} database{members.reduce((s, m) => s + m.databases.length, 0) === 1 ? '' : 's'}
            </div>
          </div>
        ),
      },
      style: {
        width: shardWidth - 2,
        height: SHARD_LABEL_HEIGHT - 4,
        background: 'transparent',
        border: 'none',
      },
    });

    // Service columns inside this shard frame.
    for (let i = 0; i < members.length; i++) {
      const snap = members[i];
      const rows = Math.max(1, snap.databases.length);
      const innerColHeight = HEADER_HEIGHT + rows * ROW_HEIGHT + 12;
      const colXInShard = SHARD_PADDING_X + i * (COL_WIDTH + COL_GAP);

      // Service column group (child of the shard frame). Intentionally
      // NOT pinned via `extent: 'parent'` so the user can drag the
      // column into another shard frame to reassign its shard tag —
      // Phase 5d drag-to-reshard. The drop handler below detects
      // which frame the column ended up in and updates the connection.
      nodes.push({
        id: `conn:${snap.conn.id}`,
        type: 'group',
        parentId: shardId,
        position: { x: colXInShard, y: SHARD_LABEL_HEIGHT + 4 },
        data: { label: snap.conn.name, connectionId: snap.conn.id, currentShard: shardKey },
        style: {
          width: COL_WIDTH,
          height: innerColHeight,
          background: snap.error ? 'rgba(217,4,41,0.04)' : 'white',
          border: `1px solid ${snap.error ? 'var(--red)' : 'var(--gray-200)'}`,
          borderRadius: 2,
        },
      });

      // Service header.
      nodes.push({
        id: `header:${snap.conn.id}`,
        type: 'default',
        parentId: `conn:${snap.conn.id}`,
        extent: 'parent',
        draggable: false,
        selectable: false,
        position: { x: 0, y: 0 },
        data: {
          label: (
            <div style={{ textAlign: 'left', padding: '6px 10px' }}>
              <div style={{ fontWeight: 700, fontSize: 13, color: snap.error ? 'var(--red)' : 'var(--ink)' }}>
                {snap.conn.name}
              </div>
              <div style={{ fontFamily: 'var(--mono)', fontSize: 10, color: 'var(--gray-500)' }}>
                {snap.conn.baseUrl.replace(/^https?:\/\//, '')}
                {snap.error ? ' · unreachable' : ''}
              </div>
            </div>
          ),
        },
        style: {
          width: COL_WIDTH - 2,
          height: HEADER_HEIGHT - 8,
          background: 'transparent',
          border: 'none',
        },
      });

      // DB nodes inside the service column.
      for (let j = 0; j < snap.databases.length; j++) {
        const db = snap.databases[j];
        const status = snap.statuses[j];
        nodes.push({
          id: `db:${snap.conn.id}/${db.name}`,
          type: 'database',
          parentId: `conn:${snap.conn.id}`,
          extent: 'parent',
          position: { x: 16, y: HEADER_HEIGHT + j * ROW_HEIGHT },
          data: {
            name: db.name,
            filePath: db.filePath,
            isDefault: db.isDefault,
            role: status?.role ?? 'unknown',
            leaderPort: status?.leader?.port ?? null,
            followerCount: status?.leader?.followerCount ?? 0,
            followerLeaderEndpoint: status?.follower?.leader?.endpoint ?? null,
            readOnly: status?.readOnly ?? false,
          } as DBNodeData,
        });
      }
    }

    cursorX += shardWidth + SHARD_GAP;
  }

  // Edges: for each follower, find a matching leader anywhere by port.
  // Build a (port → leaderNodeId) map first.
  const leaderByPort = new Map<number, string>();
  for (const snap of snapshots) {
    for (let j = 0; j < snap.databases.length; j++) {
      const status = snap.statuses[j];
      if (status?.role === 'leader' && status.leader.port != null) {
        leaderByPort.set(status.leader.port, `db:${snap.conn.id}/${snap.databases[j].name}`);
      }
    }
  }

  const edges: Edge[] = [];
  for (const snap of snapshots) {
    for (let j = 0; j < snap.databases.length; j++) {
      const status = snap.statuses[j];
      if (status?.role !== 'follower') continue;
      const endpoint = status.follower.leader?.endpoint;
      if (!endpoint) continue;
      const m = endpoint.match(/:(\d+)$/);
      if (!m) continue;
      const port = parseInt(m[1], 10);
      const leaderId = leaderByPort.get(port);
      if (!leaderId) continue;
      const followerId = `db:${snap.conn.id}/${snap.databases[j].name}`;
      edges.push({
        id: `xrep:${leaderId}->${followerId}`,
        source: leaderId,
        target: followerId,
        label: `rep :${port}`,
        labelStyle: { fontFamily: 'var(--mono)', fontSize: 10, fill: '#6c757d' },
        labelBgStyle: { fill: 'white' },
        labelBgPadding: [4, 2],
        type: 'smoothstep',
      });
    }
  }
  return { nodes, edges };
}

// ---------------------------------------------------------------- collections view

interface CollectionLocation {
  conn: Connection;
  database: string;
}
interface CollectionRow {
  name: string;
  locations: CollectionLocation[];
  inferredStrategy: 'hash' | 'replicated' | 'single' | 'unknown';
}

/**
 * Phase 6b — Collections tab. Walks every registered Connection, then
 * every attached DB on those connections, calling /db/{name}/collections
 * to build a cross-reference of where each collection lives. The
 * inference rule for strategy is intentionally crude:
 *
 *  - Present on EVERY (connection, database) probed → likely `replicated`.
 *  - Present on >1 location → likely `hash` (multiple shards hold slices).
 *  - Present on exactly 1 location → `single` (or unsharded standalone).
 *
 * Real strategy comes from a cluster.json the operator hands the router;
 * once the cluster config builder lands we'll prefer that over inference.
 */
function CollectionsView({ connections }: { connections: Connection[] }) {
  const [rows, setRows] = useState<CollectionRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [totalLocations, setTotalLocations] = useState(0);

  useEffect(() => {
    if (connections.length === 0) { setRows([]); setLoading(false); return; }
    let cancelled = false;
    setLoading(true); setError(null);
    (async () => {
      try {
        // Fan out to every connection: list its DBs, then for each DB
        // list its collections. Per-connection failures don't sink the
        // whole probe — they show up as gaps in the cross-reference.
        type Probe = { conn: Connection; database: string; collections: string[] };
        const probes: Probe[] = [];
        await Promise.all(connections.map(async (conn) => {
          try {
            const dbs = await listDatabases({ connection: conn });
            await Promise.all(dbs.databases.map(async (db) => {
              try {
                const r = await listDbCollections(db.name, { connection: conn });
                probes.push({ conn, database: db.name, collections: r.collections });
              } catch { /* ignore per-db failure */ }
            }));
          } catch { /* ignore per-conn failure */ }
        }));

        if (cancelled) return;

        // Build the cross-reference. Map { collectionName -> [...locations] }.
        const byCollection = new Map<string, CollectionLocation[]>();
        for (const p of probes) {
          for (const c of p.collections) {
            if (!byCollection.has(c)) byCollection.set(c, []);
            byCollection.get(c)!.push({ conn: p.conn, database: p.database });
          }
        }

        const totalProbes = probes.length;
        const result: CollectionRow[] = Array.from(byCollection.entries())
          .map(([name, locations]) => ({
            name,
            locations,
            inferredStrategy: inferStrategy(locations.length, totalProbes),
          }))
          .sort((a, b) => a.name.localeCompare(b.name));

        setRows(result);
        setTotalLocations(totalProbes);
      } catch (e: any) {
        if (!cancelled) setError(e.message || String(e));
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => { cancelled = true; };
    // eslint-disable-next-line
  }, [connections.length, connections.map(c => c.id).join('|')]);

  if (loading) return <div className="card"><p>Probing every connection's collections…</p></div>;
  if (error) return <div className="card" style={{ borderLeft: '4px solid var(--red)' }}><strong>Error:</strong> {error}</div>;
  if (connections.length === 0) {
    return (
      <div className="card">
        <p>No connections registered.</p>
        <p style={{ color: 'var(--gray-500)', fontSize: 13 }}>
          Add one via the <a href="/connections" style={{ color: 'var(--red)' }}>Connections</a> page to discover collections across your cluster.
        </p>
      </div>
    );
  }
  if (rows.length === 0) {
    return (
      <div className="card">
        <p>No collections found across {totalLocations} database{totalLocations === 1 ? '' : 's'}.</p>
        <p style={{ color: 'var(--gray-500)', fontSize: 13 }}>
          Insert a doc anywhere — the collection lands and shows up here.
        </p>
      </div>
    );
  }

  return (
    <>
      <div className="card" style={{ marginBottom: 12, display: 'flex', alignItems: 'center', gap: 16 }}>
        <div style={{ fontSize: 14 }}>
          <strong>{rows.length}</strong> distinct collection{rows.length === 1 ? '' : 's'}
          {' '}across <strong>{totalLocations}</strong> database{totalLocations === 1 ? '' : 's'}
          {' '}on <strong>{connections.length}</strong> connection{connections.length === 1 ? '' : 's'}
        </div>
        <span style={{ flex: 1 }} />
        <div style={{ fontSize: 11, color: 'var(--gray-500)' }}>
          Strategy is inferred from where each collection appears. Load a cluster.json for exact policies.
        </div>
      </div>

      <table className="table" style={{ background: 'white', border: '1px solid var(--gray-200)' }}>
        <thead>
          <tr style={{ background: 'var(--gray-100)' }}>
            <th style={thCell()}>Collection</th>
            <th style={thCell()}>Inferred strategy</th>
            <th style={thCell()}>Locations</th>
            <th style={thCell()}>Lives at</th>
          </tr>
        </thead>
        <tbody>
          {rows.map(row => (
            <tr key={row.name} style={{ borderBottom: '1px solid var(--gray-100)' }}>
              <td style={tdCell()}>
                <span style={{ fontWeight: 600, fontFamily: 'var(--mono)' }}>{row.name}</span>
              </td>
              <td style={tdCell()}>
                <StrategyPill strategy={row.inferredStrategy} />
              </td>
              <td style={tdCell()}>
                <span style={{ fontFamily: 'var(--mono)', fontSize: 12 }}>
                  {row.locations.length} / {totalLocations}
                </span>
              </td>
              <td style={tdCell()}>
                <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4 }}>
                  {row.locations.map((loc, i) => (
                    <span
                      key={`${loc.conn.id}/${loc.database}/${i}`}
                      style={{ fontFamily: 'var(--mono)', fontSize: 10, padding: '2px 6px', background: 'var(--gray-100)', border: '1px solid var(--gray-200)' }}
                      title={loc.conn.baseUrl}
                    >
                      {loc.conn.name}<span style={{ color: 'var(--gray-500)' }}>/{loc.database}</span>
                    </span>
                  ))}
                </div>
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      <div style={{ marginTop: 16, fontSize: 12, color: 'var(--gray-500)' }}>
        💡 The router uses these collection names + an explicit cluster.json
        (with strategies like <code style={{ fontFamily: 'var(--mono)' }}>hash</code> and{' '}
        <code style={{ fontFamily: 'var(--mono)' }}>replicated</code>) to route
        requests. The inferred-strategy column is a heuristic; once we build the
        cluster config editor it'll show authoritative values.
      </div>
    </>
  );
}

function inferStrategy(locationCount: number, totalProbes: number): CollectionRow['inferredStrategy'] {
  if (totalProbes === 0) return 'unknown';
  if (locationCount === totalProbes && totalProbes > 1) return 'replicated';
  if (locationCount > 1) return 'hash';
  return 'single';
}

function StrategyPill({ strategy }: { strategy: CollectionRow['inferredStrategy'] }) {
  switch (strategy) {
    case 'hash':
      return <span className="pill" style={{ background: '#dbeafe', color: '#1d4ed8' }}>HASH ?</span>;
    case 'replicated':
      return <span className="pill" style={{ background: '#dcfce7', color: '#15803d' }}>REPLICATED ?</span>;
    case 'single':
      return <span className="pill gray">SINGLE</span>;
    default:
      return <span className="pill gray">—</span>;
  }
}

function thCell(): React.CSSProperties {
  return {
    padding: '10px 12px',
    textAlign: 'left',
    fontFamily: 'var(--mono)',
    fontSize: 11,
    letterSpacing: '0.06em',
    textTransform: 'uppercase',
    color: 'var(--gray-500)',
    fontWeight: 700,
    borderBottom: '1px solid var(--gray-200)',
  };
}
function tdCell(): React.CSSProperties {
  return { padding: '10px 12px', fontSize: 13, verticalAlign: 'middle' };
}

// ---------------------------------------------------------------- nodes/edges

// On auto-poll, preserve any positions the user has already dragged to.
// New DBs use the auto-position; removed DBs vanish; existing DBs keep
// their canvas seat.
function mergeNodes(
  previous: Node<DBNodeData>[],
  next: Node<DBNodeData>[],
): Node<DBNodeData>[] {
  const prevById = new Map(previous.map(n => [n.id, n]));
  return next.map(n => {
    const prev = prevById.get(n.id);
    if (!prev) return n;
    return { ...n, position: prev.position };
  });
}

function buildNodes(snapshot: SnapshotEntry[]): Node<DBNodeData>[] {
  const positions = loadPositions();
  return snapshot.map((entry, i) => {
    const id = entry.db.name;
    const saved = positions[id];
    const auto = autoPosition(entry, i, snapshot);
    return {
      id,
      type: 'database',
      position: saved ?? auto,
      data: {
        name: entry.db.name,
        filePath: entry.db.filePath,
        isDefault: entry.db.isDefault,
        role: entry.status?.role ?? 'unknown',
        leaderPort: entry.status?.leader?.port ?? null,
        followerCount: entry.status?.leader?.followerCount ?? 0,
        followerLeaderEndpoint: entry.status?.follower?.leader?.endpoint ?? null,
        readOnly: entry.status?.readOnly ?? false,
      },
    };
  });
}

function buildEdges(snapshot: SnapshotEntry[]): Edge[] {
  // Index leaders by their listening port so followers can resolve which
  // attached DB is feeding them.
  const leaderByPort = new Map<number, string>();
  for (const e of snapshot) {
    if (e.status?.role === 'leader' && e.status.leader.port != null) {
      leaderByPort.set(e.status.leader.port, e.db.name);
    }
  }

  const edges: Edge[] = [];
  for (const e of snapshot) {
    if (e.status?.role !== 'follower') continue;
    const leaderEndpoint = e.status.follower.leader?.endpoint;
    if (!leaderEndpoint) continue;
    const port = parsePort(leaderEndpoint);
    if (port == null) continue;
    const leaderName = leaderByPort.get(port);
    if (!leaderName) continue; // leader lives outside this service
    edges.push({
      id: `rep:${leaderName}->${e.db.name}`,
      source: leaderName,
      target: e.db.name,
      label: `rep :${port}`,
      labelStyle: { fontFamily: 'var(--mono)', fontSize: 10, fill: '#6c757d' },
      labelBgStyle: { fill: 'white' },
      labelBgPadding: [4, 2],
      type: 'smoothstep',
    });
  }
  return edges;
}

function parsePort(endpoint: string): number | null {
  const m = endpoint.match(/:(\d+)$/);
  if (!m) return null;
  const n = parseInt(m[1], 10);
  return isFinite(n) ? n : null;
}

// Layered auto-layout: standalones top row, leaders middle row, followers
// below their leader. Quick and deterministic — dagre would be overkill
// for the ~50 DB ceiling we're targeting.
function autoPosition(
  entry: SnapshotEntry,
  i: number,
  all: SnapshotEntry[],
): { x: number; y: number } {
  const role = entry.status?.role ?? 'unknown';
  const colWidth = 240;
  const rowHeight = 160;
  // Row strategy:
  //  - role=leader → middle row (y=200)
  //  - role=follower → below its leader (y=400) — column-aligned with leader if possible
  //  - role=none/standalone → top row (y=40)
  if (role === 'leader') {
    const leaderIdx = all.filter(e => e.status?.role === 'leader').indexOf(entry);
    return { x: 60 + leaderIdx * colWidth, y: 200 };
  }
  if (role === 'follower') {
    // Column-align with leader if we can identify it.
    const port = parsePort(entry.status?.follower.leader?.endpoint ?? '');
    if (port != null) {
      const leaderEntry = all.find(e => e.status?.role === 'leader' && e.status.leader.port === port);
      if (leaderEntry) {
        const leaderIdx = all.filter(e => e.status?.role === 'leader').indexOf(leaderEntry);
        const sameLeaderFollowers = all.filter(e =>
          e.status?.role === 'follower' &&
          parsePort(e.status.follower.leader?.endpoint ?? '') === port,
        );
        const followerIdx = sameLeaderFollowers.indexOf(entry);
        return {
          x: 60 + leaderIdx * colWidth + (followerIdx - (sameLeaderFollowers.length - 1) / 2) * 100,
          y: 200 + rowHeight,
        };
      }
    }
    // Orphan follower: bottom row, indexed by overall position.
    return { x: 60 + i * colWidth, y: 200 + rowHeight };
  }
  // Standalone / unknown — top row.
  const standaloneIdx = all.filter(e => e.status?.role !== 'leader' && e.status?.role !== 'follower').indexOf(entry);
  return { x: 60 + standaloneIdx * colWidth, y: 40 };
}

function loadPositions(): Record<string, { x: number; y: number }> {
  if (typeof window === 'undefined') return {};
  try { return JSON.parse(window.localStorage.getItem(POSITIONS_KEY) ?? '{}'); }
  catch { return {}; }
}
function savePositions(nodes: Node<DBNodeData>[]) {
  if (typeof window === 'undefined') return;
  const out: Record<string, { x: number; y: number }> = {};
  for (const n of nodes) out[n.id] = n.position;
  window.localStorage.setItem(POSITIONS_KEY, JSON.stringify(out));
}

function relayout(snapshot: SnapshotEntry[], setNodes: (n: Node<DBNodeData>[]) => void) {
  if (typeof window !== 'undefined') window.localStorage.removeItem(POSITIONS_KEY);
  setNodes(buildNodes(snapshot));
}

// ---------------------------------------------------------------- side panel

interface SidePanelProps {
  info: SelectedDbInfo;
  availableLeaders: SnapshotEntry[];
  busy: boolean;
  onClose: () => void;
  onStartLeader: (port: number) => void;
  onFollow: (leaderName: string) => void;
  onFollowExternal: (host: string, port: number) => void;
  onMakeActive: () => void;
  onDrop: (deleteFiles: boolean) => void;
}

function SidePanel({ info, availableLeaders, busy, onClose, onStartLeader, onFollow, onFollowExternal, onMakeActive, onDrop }: SidePanelProps) {
  const [draftPort, setDraftPort] = useState(5500);
  const [draftLeader, setDraftLeader] = useState<string>('');
  const [showExternal, setShowExternal] = useState(false);
  const [extHost, setExtHost] = useState('');
  const [extPort, setExtPort] = useState(5500);
  const role = info.status?.role ?? 'unknown';

  // Suggest a port that isn't already taken by another leader on this service.
  useEffect(() => {
    setDraftPort(suggestPort(info.name));
  }, [info.name]);

  return (
    <aside className="topology-side">
      <div className="topology-side-header">
        <div style={{ fontSize: 16, fontWeight: 600 }}>{info.name}</div>
        <button onClick={onClose} style={iconBtn()} title="Close panel">✕</button>
      </div>

      <div className="setting-row"><span className="key">Role</span>
        <span className="val">
          {role === 'leader' && <span className="pill red">LEADER</span>}
          {role === 'follower' && <span className="pill" style={{ background: '#dbeafe', color: '#1d4ed8' }}>FOLLOWER</span>}
          {role === 'none' && <span className="pill gray">STANDALONE</span>}
        </span>
      </div>
      <div className="setting-row"><span className="key">Active default</span>
        <span className="val">{info.isDefault ? '✓ yes' : '—'}</span>
      </div>
      <div className="setting-row"><span className="key">File</span>
        <span className="val" style={{ fontFamily: 'var(--mono)', fontSize: 10 }}>{info.filePath}</span>
      </div>
      {role === 'leader' && (
        <>
          <div className="setting-row"><span className="key">Listening port</span>
            <span className="val" style={{ fontFamily: 'var(--mono)' }}>:{info.status!.leader.port ?? '—'}</span>
          </div>
          <div className="setting-row"><span className="key">Followers</span>
            <span className="val">{info.status!.leader.followerCount}</span>
          </div>
          <div className="setting-row"><span className="key">Current seq</span>
            <span className="val" style={{ fontFamily: 'var(--mono)' }}>{info.status!.leader.currentSeq}</span>
          </div>
        </>
      )}
      {role === 'follower' && (
        <>
          <div className="setting-row"><span className="key">Following</span>
            <span className="val" style={{ fontFamily: 'var(--mono)', fontSize: 11 }}>{info.status!.follower.leader?.endpoint ?? '—'}</span>
          </div>
          <div className="setting-row"><span className="key">Applied seq</span>
            <span className="val" style={{ fontFamily: 'var(--mono)' }}>{info.status!.follower.lastAppliedSeq}</span>
          </div>
          <div className="setting-row"><span className="key">Ops applied</span>
            <span className="val">{info.status!.follower.opsApplied}</span>
          </div>
        </>
      )}

      <div className="topology-side-section">
        <div className="topology-side-section-label">REPLICATION</div>
        {role === 'none' && (
          <>
            <div style={{ marginBottom: 8 }}>
              <div style={{ fontSize: 11, color: 'var(--gray-500)', marginBottom: 4 }}>Make this DB a leader on TCP port:</div>
              <div style={{ display: 'flex', gap: 6 }}>
                <input
                  type="number"
                  value={draftPort}
                  onChange={e => setDraftPort(parseInt(e.target.value) || 0)}
                  style={{ width: 80, padding: '6px 8px', fontSize: 12, border: '1px solid var(--gray-200)', fontFamily: 'var(--mono)' }}
                />
                <button onClick={() => onStartLeader(draftPort)} disabled={busy || draftPort < 1024} style={primaryBtn()}>
                  Start leader
                </button>
              </div>
            </div>
            {availableLeaders.length > 0 && (
              <div style={{ marginBottom: 8 }}>
                <div style={{ fontSize: 11, color: 'var(--gray-500)', marginBottom: 4 }}>Or follow an existing leader on this service:</div>
                <div style={{ display: 'flex', gap: 6 }}>
                  <select value={draftLeader} onChange={e => setDraftLeader(e.target.value)} style={{ flex: 1, padding: '6px 8px', fontSize: 12, border: '1px solid var(--gray-200)' }}>
                    <option value="">(pick a leader)</option>
                    {availableLeaders.map(l => (
                      <option key={l.db.name} value={l.db.name}>{l.db.name} :{l.status!.leader.port}</option>
                    ))}
                  </select>
                  <button onClick={() => onFollow(draftLeader)} disabled={busy || !draftLeader} style={primaryBtn()}>
                    Follow
                  </button>
                </div>
              </div>
            )}
            {/* External leader — for cross-service replication. Useful when
                this service hosts a hot-standby of a leader running elsewhere. */}
            <div>
              <button
                onClick={() => setShowExternal(s => !s)}
                style={{ background: 'none', border: 'none', color: 'var(--gray-500)', fontSize: 11, cursor: 'pointer', padding: 0, textDecoration: 'underline' }}
              >
                {showExternal ? '▾ Hide' : '▸ Follow a leader on a different service'}
              </button>
              {showExternal && (
                <div style={{ display: 'flex', gap: 6, marginTop: 4 }}>
                  <input
                    type="text"
                    value={extHost}
                    onChange={e => setExtHost(e.target.value)}
                    placeholder="host (e.g. 10.0.1.4)"
                    style={{ flex: 2, padding: '6px 8px', fontSize: 12, border: '1px solid var(--gray-200)', fontFamily: 'var(--mono)' }}
                  />
                  <input
                    type="number"
                    value={extPort}
                    onChange={e => setExtPort(parseInt(e.target.value) || 0)}
                    placeholder="port"
                    style={{ width: 72, padding: '6px 8px', fontSize: 12, border: '1px solid var(--gray-200)', fontFamily: 'var(--mono)' }}
                  />
                  <button onClick={() => onFollowExternal(extHost.trim(), extPort)} disabled={busy || !extHost.trim() || extPort < 1024} style={primaryBtn()}>
                    Follow
                  </button>
                </div>
              )}
            </div>
          </>
        )}
        {role === 'leader' && (
          <div style={{ fontSize: 11, color: 'var(--gray-500)' }}>
            Already a leader on port :{info.status!.leader.port}. Add followers by selecting another DB and choosing "Follow this DB."
          </div>
        )}
        {role === 'follower' && (
          <div style={{ fontSize: 11, color: 'var(--gray-500)' }}>
            Following {info.status!.follower.leader?.endpoint}. Read-only — writes happen at the leader.
          </div>
        )}
      </div>

      <div className="topology-side-section">
        <div className="topology-side-section-label">DATABASE</div>
        {!info.isDefault && (
          <button onClick={onMakeActive} disabled={busy} style={{ ...ghostBtn(), width: '100%', marginBottom: 6 }}>
            Set as active default
          </button>
        )}
        <button onClick={() => onDrop(false)} disabled={busy} style={{ ...ghostBtn(), width: '100%', marginBottom: 6 }}>
          Detach (keep file)
        </button>
        <button onClick={() => onDrop(true)} disabled={busy} style={{ ...dangerBtn(), width: '100%' }}>
          Drop and delete files
        </button>
      </div>
    </aside>
  );
}

function suggestPort(seedName: string): number {
  // Deterministic-ish: 5500 + hash(name) mod 100. Avoids collision in practice
  // and gives a stable suggestion per DB so the form doesn't jitter.
  let h = 0;
  for (let i = 0; i < seedName.length; i++) h = (h * 31 + seedName.charCodeAt(i)) | 0;
  return 5500 + (Math.abs(h) % 100);
}

// ---------------------------------------------------------------- add modal

interface AddModalProps {
  busy: boolean;
  onCancel: () => void;
  onSubmit: (name: string, path: string) => void;
}

function AddDatabaseModal({ busy, onCancel, onSubmit }: AddModalProps) {
  const [name, setName] = useState('');
  const [path, setPath] = useState('');
  return (
    <div className="topology-modal-backdrop" onClick={onCancel}>
      <div className="topology-modal" onClick={e => e.stopPropagation()}>
        <div style={{ fontFamily: 'var(--mono)', fontSize: 11, letterSpacing: '0.08em', textTransform: 'uppercase', color: 'var(--red)', fontWeight: 700, marginBottom: 12 }}>
          + Add database
        </div>
        <form onSubmit={e => { e.preventDefault(); if (name.trim()) onSubmit(name.trim(), path.trim()); }}>
          <div style={{ marginBottom: 12 }}>
            <label style={labelStyle()}>Name</label>
            <input
              type="text"
              value={name}
              onChange={e => setName(e.target.value)}
              placeholder="acme"
              autoFocus
              style={modalInputStyle()}
            />
          </div>
          <div style={{ marginBottom: 16 }}>
            <label style={labelStyle()}>File path <span style={{ color: 'var(--gray-500)', fontWeight: 400 }}>(optional)</span></label>
            <input
              type="text"
              value={path}
              onChange={e => setPath(e.target.value)}
              placeholder="leave blank → {dataDir}/{name}.dfdb"
              style={modalInputStyle()}
            />
            <div style={{ marginTop: 6, fontSize: 11, color: 'var(--gray-500)' }}>
              The service creates the file if it doesn't exist, attaches if it does.
            </div>
          </div>
          <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end' }}>
            <button type="button" onClick={onCancel} style={ghostBtn()}>Cancel</button>
            <button type="submit" disabled={busy || !name.trim()} style={primaryBtn()}>
              {busy ? 'Creating…' : 'Create'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------- legend

function Legend() {
  return (
    <div className="topology-legend">
      <div className="topology-legend-row"><span className="pill red" style={{ fontSize: 9 }}>LEADER</span></div>
      <div className="topology-legend-row"><span className="pill" style={{ background: '#dbeafe', color: '#1d4ed8', fontSize: 9 }}>FOLLOWER</span></div>
      <div className="topology-legend-row"><span className="pill gray" style={{ fontSize: 9 }}>STANDALONE</span></div>
      <div className="topology-legend-row" style={{ marginTop: 6 }}>
        <svg width="40" height="10"><line x1="0" y1="5" x2="40" y2="5" stroke="var(--red)" strokeWidth="2" strokeDasharray="4 2" /></svg>
        <span style={{ fontSize: 10, color: 'var(--gray-500)', marginLeft: 4 }}>replication</span>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------- styles

function primaryBtn(): React.CSSProperties {
  return { background: 'var(--red)', color: 'white', border: 'none', padding: '6px 14px', fontSize: 12, fontWeight: 600, cursor: 'pointer' };
}
function ghostBtn(): React.CSSProperties {
  return { background: 'transparent', color: 'var(--ink)', border: '1px solid var(--gray-200)', padding: '6px 12px', fontSize: 12, cursor: 'pointer' };
}
function dangerBtn(): React.CSSProperties {
  return { background: 'transparent', color: 'var(--red)', border: '1px solid var(--red)', padding: '6px 12px', fontSize: 12, fontWeight: 600, cursor: 'pointer' };
}
function iconBtn(): React.CSSProperties {
  return { background: 'transparent', border: 'none', color: 'var(--gray-500)', fontSize: 18, cursor: 'pointer', padding: 0, width: 24, height: 24 };
}
function labelStyle(): React.CSSProperties {
  return { display: 'block', fontFamily: 'var(--mono)', fontSize: 10, letterSpacing: '0.08em', textTransform: 'uppercase', color: 'var(--gray-500)', marginBottom: 4, fontWeight: 700 };
}
function modalInputStyle(): React.CSSProperties {
  return { width: '100%', padding: '8px 10px', fontSize: 14, border: '1px solid var(--gray-200)', fontFamily: 'var(--sans)', background: 'white' };
}
