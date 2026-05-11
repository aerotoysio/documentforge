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
  applyNodeChanges,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { useConnections } from '@/lib/connections-context';
import {
  listDatabases,
  createDatabase,
  deleteDatabase,
  setDefaultDatabase,
  getDbReplicationStatus,
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

function TopologyInner() {
  const { active } = useConnections();
  const [nodes, setNodes] = useNodesState<Node<DBNodeData>>([]);
  const [edges, setEdges] = useEdgesState<Edge>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [snapshot, setSnapshot] = useState<SnapshotEntry[]>([]);
  const [selectedDb, setSelectedDb] = useState<string | null>(null);
  const [showAddModal, setShowAddModal] = useState(false);
  const [actionBusy, setActionBusy] = useState(false);

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
      // Same-service replication uses loopback. Cross-service follow would
      // pass a different host; that UI lands when we add the "External
      // attach" branch in the Add Database modal.
      await startDbAsFollower(selectedDb, 'localhost', leaderPort, { connection: active ?? undefined });
      await refresh();
    } catch (e: any) {
      alert(`Could not follow: ${e.message || String(e)}`);
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

      {error && (
        <div className="card" style={{ borderLeft: '4px solid var(--red)', background: 'rgba(217,4,41,0.04)', margin: '0 0 16px' }}>
          <strong style={{ color: 'var(--red)' }}>Could not load topology.</strong>
          <div style={{ fontFamily: 'var(--mono)', fontSize: 12, marginTop: 6 }}>{error}</div>
          <div style={{ fontSize: 12, color: 'var(--gray-500)', marginTop: 6 }}>
            The connected service may predate Issue #66 — Topology needs a build that includes /databases and /db/{'{name}'}/replication routes.
          </div>
        </div>
      )}

      {/* Canvas + side panel layout */}
      <div className={`topology-stage ${selected ? '' : 'no-side'}`}>
        <div className="topology-canvas">
          <ReactFlow
            nodes={nodes}
            edges={edges}
            nodeTypes={nodeTypes}
            onNodesChange={onNodesChange}
            onNodeClick={onNodeClick}
            onPaneClick={() => setSelectedDb(null)}
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
            onMakeActive={handleMakeActive}
            onDrop={handleDrop}
          />
        )}
      </div>

      {showAddModal && (
        <AddDatabaseModal
          busy={actionBusy}
          onCancel={() => setShowAddModal(false)}
          onSubmit={handleAddDatabase}
        />
      )}
    </div>
  );
}

// ---------------------------------------------------------------- nodes/edges

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
  onMakeActive: () => void;
  onDrop: (deleteFiles: boolean) => void;
}

function SidePanel({ info, availableLeaders, busy, onClose, onStartLeader, onFollow, onMakeActive, onDrop }: SidePanelProps) {
  const [draftPort, setDraftPort] = useState(5500);
  const [draftLeader, setDraftLeader] = useState<string>('');
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
              <div>
                <div style={{ fontSize: 11, color: 'var(--gray-500)', marginBottom: 4 }}>Or follow an existing leader:</div>
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
