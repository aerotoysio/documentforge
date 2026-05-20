'use client';

// Issue #66 Phase 6c — unified topology / cluster canvas.
//
// ONE react-flow canvas that shows the LIVE topology (every registered
// connection as a service frame, its attached databases as nodes inside,
// replication relationships as edges) AND lets you compose a router cluster
// on top of it. There are no editing "modes": the meaning of a drag is
// inferred from the node types it connects —
//
//   cluster → database   = claim that DB as a shard (design intent)
//   database → database  = wire replication (leader → follower, live action)
//
// A palette lets you drag a Cluster, a new Service, or a new Database onto
// the canvas. Layer toggles hide/show the replication and cluster overlays so
// the picture stays legible. The cluster design (name, port, claimed shards,
// shard-name overrides, collections) persists to localStorage; node positions
// persist separately (kept out of React state so they never feed back into the
// node-build effect — that loop strands nodes at visibility:hidden).

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  ReactFlow,
  Background,
  Controls,
  MiniMap,
  ReactFlowProvider,
  useReactFlow,
  useNodesState,
  useEdgesState,
  applyNodeChanges,
  type Node,
  type Edge,
  type NodeChange,
  type Connection as RFConnection,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { useConnections } from '@/lib/connections-context';
import type { Connection } from '@/lib/connections';
import {
  listDatabases,
  getDbReplicationStatus,
  spawnRouter,
  spawnService,
  createDatabase,
  startDbAsLeader,
  startDbAsFollower,
  type DatabaseEntry,
  type PerDbReplicationStatus,
} from '@/lib/api';
import { ClusterNode, ServiceFrameNode, BuilderDBNode, type ClusterNodeData, type ServiceFrameData, type BuilderDBNodeData } from './cluster-nodes';

const DESIGN_KEY = 'dfdb_cluster_design';
const POSITIONS_KEY = 'dfdb_cluster_positions';
const SIZES_KEY = 'dfdb_cluster_sizes';

type Strategy = 'hash' | 'replicated' | 'single';
interface CollEntry { name: string; strategy: Strategy; shardKeyPath?: string }

interface ClusterDesign {
  name: string;
  port: string;                       // '' = host picks
  claimed: string[];                  // claimed DB node ids: `db:${connId}/${dbName}`
  shardNames: Record<string, string>; // db node id -> shard-name override
  collections: CollEntry[];
}

interface Snapshot {
  conn: Connection;
  databases: DatabaseEntry[];
  statuses: (PerDbReplicationStatus | null)[];
  error: string | null;
}

const DEFAULT_DESIGN: ClusterDesign = {
  name: 'studio-cluster',
  port: '5500',
  claimed: [],
  shardNames: {},
  collections: [
    { name: 'orders', strategy: 'hash', shardKeyPath: 'pnr' },
    { name: 'lookup', strategy: 'replicated' },
  ],
};

// layout constants
const FRAME_W = 220;
const HEADER_H = 44;
const DB_H = 64;
const FRAME_PAD_BOTTOM = 14;
const FRAME_GAP = 36;
const FRAMES_Y = 190;

// ---------------------------------------------------------------- helpers

const dbNodeId = (connId: string, dbName: string) => `db:${connId}/${dbName}`;
function parseDbNodeId(id: string): { connId: string; dbName: string } {
  const rest = id.slice('db:'.length);
  const i = rest.indexOf('/');
  return { connId: rest.slice(0, i), dbName: rest.slice(i + 1) };
}

function slug(s: string): string {
  return s.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || 'ring';
}
function parsePort(s: string): number | null {
  const n = parseInt(s, 10);
  return Number.isFinite(n) && n > 0 ? n : null;
}
function hostnameFromBaseUrl(baseUrl: string): string {
  try { return new URL(baseUrl).hostname; }
  catch { return baseUrl.replace(/^https?:\/\//, '').split(':')[0].split('/')[0]; }
}
function portOf(baseUrl: string): string {
  try { const u = new URL(baseUrl); return u.port || (u.protocol === 'https:' ? '443' : '5000'); }
  catch { const m = baseUrl.match(/:(\d+)/); return m ? m[1] : '5000'; }
}
// `dfdb serve` command that brings this service back up — handy for debugging.
function serveCommand(conn: Connection): string {
  let cmd = `dfdb serve --port ${portOf(conn.baseUrl)} --data-dir ./data/${slug(conn.name)}`;
  if (conn.apiKey) cmd += ` --api-key ${conn.apiKey}`;
  return cmd;
}
// Full, ordered script that recreates the whole topology: every service, its
// extra databases, replication wiring, and the router. Best-effort — data dirs
// are placeholders and REST steps are emitted as comments to review.
function buildSetupScript(snapshots: Snapshot[]): string {
  const L: string[] = [];
  L.push('# Reproduce this DocumentForge topology.');
  L.push('# `dfdb` is the unified CLI (on Windows: .\\dfdb.exe). Data dirs are placeholders.');
  L.push('', '# 1) Start each service');
  for (const s of snapshots) L.push(`${serveCommand(s.conn)}    # ${s.conn.name}`);

  const attach: string[] = [];
  for (const s of snapshots) for (const db of s.databases) if (!db.isDefault)
    attach.push(`#   curl -X POST ${s.conn.baseUrl}/databases -H "Content-Type: application/json" -d '{"name":"${db.name}"}'`);
  if (attach.length) { L.push('', '# 2) Attach extra databases (multi-DB hosts)'); L.push(...attach); }

  const leaderByPort = new Map<number, { conn: Connection; db: string }>();
  for (const s of snapshots) s.databases.forEach((db, j) => {
    const st = s.statuses[j];
    if (st?.role === 'leader' && st.leader.port != null) leaderByPort.set(st.leader.port, { conn: s.conn, db: db.name });
  });
  const rep: string[] = [];
  const startedLeaders = new Set<string>();
  for (const s of snapshots) s.databases.forEach((db, j) => {
    const st = s.statuses[j];
    if (st?.role !== 'follower') return;
    const m = st.follower.leader?.endpoint?.match(/:(\d+)$/);
    if (!m) return;
    const port = parseInt(m[1], 10);
    const leader = leaderByPort.get(port);
    if (!leader) return;
    const lk = `${leader.conn.id}/${leader.db}:${port}`;
    if (!startedLeaders.has(lk)) {
      rep.push(`#   curl -X POST ${leader.conn.baseUrl}/db/${leader.db}/replication/start-leader -H "Content-Type: application/json" -d '{"port":${port}}'`);
      startedLeaders.add(lk);
    }
    rep.push(`#   curl -X POST ${s.conn.baseUrl}/db/${db.name}/replication/start-follower -H "Content-Type: application/json" -d '{"host":"${hostnameFromBaseUrl(leader.conn.baseUrl)}","port":${port}}'`);
  });
  if (rep.length) { L.push('', '# 3) Wire replication (leader → follower)'); L.push(...rep); }

  L.push('', '# 4) Front the cluster with the router (uses the cluster.json from the panel above)');
  L.push('dfdb router --config cluster.json');
  return L.join('\n');
}
function suggestPort(name: string): number {
  let h = 0;
  for (let i = 0; i < name.length; i++) h = (h * 31 + name.charCodeAt(i)) | 0;
  return 5500 + (Math.abs(h) % 100);
}

function loadDesign(): ClusterDesign {
  if (typeof window === 'undefined') return DEFAULT_DESIGN;
  try { const raw = window.localStorage.getItem(DESIGN_KEY); if (raw) return { ...DEFAULT_DESIGN, ...JSON.parse(raw) }; }
  catch { /* default */ }
  return DEFAULT_DESIGN;
}
function saveDesign(d: ClusterDesign) {
  if (typeof window !== 'undefined') window.localStorage.setItem(DESIGN_KEY, JSON.stringify(d));
}
function loadPositions(): Record<string, { x: number; y: number }> {
  if (typeof window === 'undefined') return {};
  try { return JSON.parse(window.localStorage.getItem(POSITIONS_KEY) ?? '{}'); }
  catch { return {}; }
}
function savePositionsFromNodes(nodes: Node[]) {
  if (typeof window === 'undefined') return;
  const out = loadPositions();
  for (const n of nodes) out[n.id] = n.position;
  window.localStorage.setItem(POSITIONS_KEY, JSON.stringify(out));
}
function savePositionFor(id: string, pos: { x: number; y: number }) {
  if (typeof window === 'undefined') return;
  const out = loadPositions();
  out[id] = pos;
  window.localStorage.setItem(POSITIONS_KEY, JSON.stringify(out));
}
function loadSizes(): Record<string, { width: number; height: number }> {
  if (typeof window === 'undefined') return {};
  try { return JSON.parse(window.localStorage.getItem(SIZES_KEY) ?? '{}'); }
  catch { return {}; }
}
function saveSizesFromNodes(nodes: Node[]) {
  if (typeof window === 'undefined') return;
  const out = loadSizes();
  for (const n of nodes) {
    if (!n.id.startsWith('svc:')) continue;
    const w = n.width ?? n.measured?.width;
    const h = n.height ?? n.measured?.height;
    if (w && h) out[n.id] = { width: w, height: h };
  }
  window.localStorage.setItem(SIZES_KEY, JSON.stringify(out));
}

interface ClaimInfo { id: string; conn: Connection; dbName: string; isDefault: boolean }

function defaultShardName(conn: Connection, dbName: string, isDefault: boolean): string {
  return isDefault ? slug(conn.name) : `${slug(conn.name)}-${slug(dbName)}`;
}
function shardNameFor(design: ClusterDesign, c: ClaimInfo): string {
  return (design.shardNames[c.id] ?? '').trim() || defaultShardName(c.conn, c.dbName, c.isDefault);
}

function buildConfig(design: ClusterDesign, claimed: ClaimInfo[]) {
  const shards = claimed.map((c) => {
    const name = shardNameFor(design, c);
    let leader: unknown;
    if (c.isDefault && !c.conn.apiKey) {
      leader = c.conn.baseUrl;
    } else {
      const obj: Record<string, string> = { baseUrl: c.conn.baseUrl };
      if (!c.isDefault) obj.database = c.dbName;
      if (c.conn.apiKey) obj.apiKey = c.conn.apiKey;
      leader = obj;
    }
    return { name, leader };
  });
  const collections = design.collections
    .filter((c) => c.name.trim())
    .map((c) => c.strategy === 'hash'
      ? { name: c.name.trim(), strategy: 'hash', shardKeyPath: (c.shardKeyPath ?? '').trim() }
      : { name: c.name.trim(), strategy: c.strategy });
  const router: Record<string, unknown> = { name: design.name.trim() || 'studio-cluster' };
  const port = parsePort(design.port);
  if (port != null) router.port = port;
  return { router, shards, collections };
}

function validate(design: ClusterDesign, claimed: ClaimInfo[]): string[] {
  const errs: string[] = [];
  if (claimed.length === 0) errs.push('Claim at least one database — drag the cluster node onto a DB.');
  const names = claimed.map((c) => shardNameFor(design, c));
  const dupes = [...new Set(names.filter((n, i) => names.indexOf(n) !== i))];
  if (dupes.length) errs.push(`Duplicate shard name(s): ${dupes.join(', ')}. Rename one in its panel.`);
  for (const c of design.collections) {
    if (!c.name.trim()) continue;
    if (c.strategy === 'hash' && !(c.shardKeyPath ?? '').trim()) {
      errs.push(`Collection “${c.name.trim()}” is hash-sharded but has no shard key path.`);
    }
  }
  return errs;
}

// ---------------------------------------------------------------- component

export function ClusterBuilderView({ connections }: { connections: Connection[] }) {
  // Own provider so screenToFlowPosition + the store are isolated from the
  // page-level provider used by the Graph / Across tabs.
  return (
    <ReactFlowProvider>
      <UnifiedCanvas connections={connections} />
    </ReactFlowProvider>
  );
}

function UnifiedCanvas({ connections }: { connections: Connection[] }) {
  const { active, add } = useConnections();
  const { screenToFlowPosition, fitView } = useReactFlow();

  const [design, setDesign] = useState<ClusterDesign>(() => loadDesign());
  const [snapshots, setSnapshots] = useState<Snapshot[]>([]);
  const [loading, setLoading] = useState(true);
  const [showCluster, setShowCluster] = useState(true);
  const [showReplication, setShowReplication] = useState(true);
  const [layoutRev, setLayoutRev] = useState(0);
  const [selectedDbId, setSelectedDbId] = useState<string | null>(null);
  const [selectedSvcId, setSelectedSvcId] = useState<string | null>(null);
  const [scriptCopied, setScriptCopied] = useState(false);
  const [copied, setCopied] = useState(false);
  const [spawning, setSpawning] = useState(false);
  const [banner, setBanner] = useState<{ kind: 'ok' | 'err'; text: string } | null>(null);
  const [wireDraft, setWireDraft] = useState<{
    sourceConn: Connection; sourceName: string;
    targetConn: Connection; targetName: string; suggestedPort: number;
  } | null>(null);
  const [wiring, setWiring] = useState(false);

  const [nodes, setNodes] = useNodesState<Node>([]);
  const [edges, setEdges] = useEdgesState<Edge>([]);
  const nodeTypes = useMemo(() => ({ cluster: ClusterNode, service: ServiceFrameNode, database: BuilderDBNode }), []);

  const snapshotsRef = useRef<Snapshot[]>([]);
  useEffect(() => { snapshotsRef.current = snapshots; }, [snapshots]);
  const nodesRef = useRef<Node[]>([]);
  useEffect(() => { nodesRef.current = nodes; }, [nodes]);

  useEffect(() => { saveDesign(design); }, [design]);

  // ---- live snapshot fetch ----
  const refresh = useCallback(async () => {
    if (connections.length === 0) { setSnapshots([]); setLoading(false); return; }
    setLoading(true);
    const results = await Promise.all(connections.map(async (conn): Promise<Snapshot> => {
      try {
        const list = await listDatabases({ connection: conn });
        const statuses = await Promise.all(list.databases.map((db) =>
          getDbReplicationStatus(db.name, { connection: conn }).catch(() => null)));
        return { conn, databases: list.databases, statuses, error: null };
      } catch (e: unknown) {
        return { conn, databases: [], statuses: [], error: e instanceof Error ? e.message : String(e) };
      }
    }));
    setSnapshots(results);
    setLoading(false);
  }, [connections]);

  useEffect(() => { refresh(); /* eslint-disable-next-line react-hooks/exhaustive-deps */ },
    [connections.length, connections.map((c) => c.id + c.baseUrl).join('|')]);
  useEffect(() => { const id = window.setInterval(refresh, 5000); return () => window.clearInterval(id); }, [refresh]);

  // ---- derive claimed-shard info from snapshots ----
  const claimed: ClaimInfo[] = useMemo(() => {
    const byId = new Map<string, ClaimInfo>();
    for (const s of snapshots) {
      for (const db of s.databases) {
        const id = dbNodeId(s.conn.id, db.name);
        if (design.claimed.includes(id)) byId.set(id, { id, conn: s.conn, dbName: db.name, isDefault: db.isDefault });
      }
    }
    // Preserve claim order.
    return design.claimed.map((id) => byId.get(id)).filter((c): c is ClaimInfo => !!c);
  }, [snapshots, design.claimed]);

  // ---- build nodes + edges ----
  useEffect(() => {
    const positions = loadPositions();
    const sizes = loadSizes();
    const built: Node[] = [];

    if (showCluster) {
      built.push({
        id: 'cluster',
        type: 'cluster',
        position: positions['cluster'] ?? { x: 320, y: 8 },
        data: { name: design.name, port: parsePort(design.port), shardCount: claimed.length } as ClusterNodeData,
      });
    }

    let cursorX = 20;
    for (const snap of snapshots) {
      const rows = Math.max(1, snap.databases.length);
      const frameId = `svc:${snap.conn.id}`;
      const frameH = HEADER_H + rows * DB_H + FRAME_PAD_BOTTOM;
      const savedSize = sizes[frameId];
      built.push({
        id: frameId,
        type: 'service',
        position: positions[frameId] ?? { x: cursorX, y: FRAMES_Y },
        width: savedSize?.width ?? FRAME_W,
        height: savedSize?.height ?? frameH,
        dragHandle: '.svc-drag-handle',
        data: { connName: snap.conn.name, baseUrl: snap.conn.baseUrl, error: snap.error } as ServiceFrameData,
      });
      snap.databases.forEach((db, j) => {
        const id = dbNodeId(snap.conn.id, db.name);
        const st = snap.statuses[j];
        built.push({
          id,
          type: 'database',
          parentId: frameId,
          extent: 'parent',
          position: { x: 12, y: HEADER_H + j * DB_H },
          data: {
            dbName: db.name,
            role: st?.role ?? 'unknown',
            isDefault: db.isDefault,
            claimed: design.claimed.includes(id),
            leaderPort: st?.leader?.port ?? null,
          } as BuilderDBNodeData,
        });
      });
      cursorX += (savedSize?.width ?? FRAME_W) + FRAME_GAP;
    }
    setNodes(built);

    const built_edges: Edge[] = [];
    if (showReplication) {
      const leaderByPort = new Map<number, string>();
      for (const s of snapshots) {
        s.databases.forEach((db, j) => {
          const st = s.statuses[j];
          if (st?.role === 'leader' && st.leader.port != null) leaderByPort.set(st.leader.port, dbNodeId(s.conn.id, db.name));
        });
      }
      for (const s of snapshots) {
        s.databases.forEach((db, j) => {
          const st = s.statuses[j];
          if (st?.role !== 'follower') return;
          const ep = st.follower.leader?.endpoint;
          const m = ep?.match(/:(\d+)$/);
          if (!m) return;
          const leaderId = leaderByPort.get(parseInt(m[1], 10));
          if (!leaderId) return;
          built_edges.push({
            id: `rep:${leaderId}->${dbNodeId(s.conn.id, db.name)}`,
            source: leaderId,
            target: dbNodeId(s.conn.id, db.name),
            type: 'smoothstep',
            animated: true,
            style: { stroke: '#3b82f6', strokeWidth: 2 },
            label: `rep :${m[1]}`,
            labelStyle: { fontFamily: 'var(--mono)', fontSize: 9, fill: '#3b82f6' },
            labelBgStyle: { fill: 'white' },
          });
        });
      }
    }
    if (showCluster) {
      for (const c of claimed) {
        built_edges.push({
          id: `claim:${c.id}`,
          source: 'cluster',
          target: c.id,
          type: 'smoothstep',
          animated: true,
          style: { stroke: 'var(--red)', strokeWidth: 2, strokeDasharray: '6 4' },
        });
      }
    }
    setEdges(built_edges);
  }, [snapshots, design.claimed, design.name, design.port, design.shardNames, showCluster, showReplication, claimed, layoutRev, setNodes, setEdges]);

  const onNodesChange = useCallback((changes: NodeChange<Node>[]) => {
    setNodes((nds) => {
      const next = applyNodeChanges(changes, nds);
      if (changes.some((c) => c.type === 'position' && (c as { dragging?: boolean }).dragging === false)) {
        savePositionsFromNodes(next);
      }
      if (changes.some((c) => c.type === 'dimensions' && (c as { id?: string }).id?.startsWith('svc:'))) {
        saveSizesFromNodes(next);
      }
      return next;
    });
  }, [setNodes]);

  // ---- gesture-disambiguated wiring ----
  const onConnect = useCallback((params: RFConnection) => {
    const { source, target } = params;
    if (!source || !target || source === target) return;
    if (source === 'cluster' && target.startsWith('db:')) {
      setDesign((d) => (d.claimed.includes(target) ? d : { ...d, claimed: [...d.claimed, target] }));
      return;
    }
    if (source === 'cluster' && target.startsWith('svc:')) {
      // Dropping the cluster on a whole service claims its default DB (or its
      // first DB) — shorthand for the common one-DB-per-service case.
      const connId = target.slice('svc:'.length);
      const snap = snapshotsRef.current.find((x) => x.conn.id === connId);
      const def = snap?.databases.find((d) => d.isDefault) ?? snap?.databases[0];
      if (def) {
        const id = dbNodeId(connId, def.name);
        setDesign((d) => (d.claimed.includes(id) ? d : { ...d, claimed: [...d.claimed, id] }));
      }
      return;
    }
    if (source.startsWith('db:') && target.startsWith('db:')) {
      const s = parseDbNodeId(source);
      const t = parseDbNodeId(target);
      const sConn = connections.find((c) => c.id === s.connId);
      const tConn = connections.find((c) => c.id === t.connId);
      if (!sConn || !tConn) return;
      const sSnap = snapshotsRef.current.find((x) => x.conn.id === s.connId);
      const idx = sSnap?.databases.findIndex((d) => d.name === s.dbName) ?? -1;
      const sStatus = idx >= 0 ? sSnap?.statuses[idx] : null;
      setWireDraft({
        sourceConn: sConn, sourceName: s.dbName,
        targetConn: tConn, targetName: t.dbName,
        suggestedPort: sStatus?.leader?.port ?? suggestPort(s.dbName),
      });
    }
  }, [connections]);

  const onEdgeClick = useCallback((_e: React.MouseEvent, edge: Edge) => {
    if (!edge.id.startsWith('claim:')) return;
    const id = edge.id.slice('claim:'.length);
    setDesign((d) => ({ ...d, claimed: d.claimed.filter((x) => x !== id) }));
  }, []);

  const onNodeClick = useCallback((_e: React.MouseEvent, node: Node) => {
    setSelectedDbId(node.id.startsWith('db:') ? node.id : null);
    setSelectedSvcId(node.id.startsWith('svc:') ? node.id : null);
  }, []);

  function resetLayout() {
    if (typeof window !== 'undefined') {
      window.localStorage.removeItem(POSITIONS_KEY);
      window.localStorage.removeItem(SIZES_KEY);
    }
    setSelectedDbId(null);
    setSelectedSvcId(null);
    setLayoutRev((r) => r + 1);
    window.setTimeout(() => fitView({ duration: 300 }), 80);
  }

  async function confirmWire(port: number) {
    if (!wireDraft) return;
    setWiring(true);
    try {
      const sSnap = snapshotsRef.current.find((x) => x.conn.id === wireDraft.sourceConn.id);
      const idx = sSnap?.databases.findIndex((d) => d.name === wireDraft.sourceName) ?? -1;
      const sStatus = idx >= 0 ? sSnap?.statuses[idx] : null;
      if (sStatus?.role !== 'leader') {
        await startDbAsLeader(wireDraft.sourceName, port, { connection: wireDraft.sourceConn });
      }
      const host = hostnameFromBaseUrl(wireDraft.sourceConn.baseUrl);
      await startDbAsFollower(wireDraft.targetName, host, port, { connection: wireDraft.targetConn });
      setWireDraft(null);
      await refresh();
    } catch (e: unknown) {
      setBanner({ kind: 'err', text: `Wire failed: ${e instanceof Error ? e.message : String(e)}` });
    } finally {
      setWiring(false);
    }
  }

  // ---- palette drop ----
  const onDragOver = useCallback((e: React.DragEvent) => { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; }, []);

  function frameConnIdAt(pos: { x: number; y: number }): string | null {
    for (const n of nodesRef.current) {
      if (!n.id.startsWith('svc:')) continue;
      const w = (n.width ?? n.measured?.width ?? FRAME_W);
      const h = (n.height ?? n.measured?.height ?? 200);
      if (pos.x >= n.position.x && pos.x <= n.position.x + w && pos.y >= n.position.y && pos.y <= n.position.y + h) {
        return n.id.slice('svc:'.length);
      }
    }
    return null;
  }

  const onDrop = useCallback((e: React.DragEvent) => {
    e.preventDefault();
    const type = e.dataTransfer.getData('application/df-node');
    if (!type) return;
    const pos = screenToFlowPosition({ x: e.clientX, y: e.clientY });

    if (type === 'cluster') {
      savePositionFor('cluster', pos);
      setShowCluster(true);
      setLayoutRev((r) => r + 1);
      return;
    }
    if (type === 'service') {
      if (!active) { setBanner({ kind: 'err', text: 'Pick an active connection first — its host runs the spawned service.' }); return; }
      const name = window.prompt('Name for the new service (blank = auto):', '');
      if (name === null) return;
      setBanner(null);
      (async () => {
        try {
          const res = await spawnService({ connection: active, nodeName: name.trim() || undefined });
          add({ name: res.nodeName || `spawned-:${res.port}`, baseUrl: res.baseUrl, tags: ['spawned', `parent:${active.baseUrl}`] });
          setBanner({ kind: 'ok', text: `Spawned ${res.baseUrl} — added as a connection.` });
          await refresh();
        } catch (err: unknown) {
          setBanner({ kind: 'err', text: `Spawn service failed: ${err instanceof Error ? err.message : String(err)}` });
        }
      })();
      return;
    }
    if (type === 'database') {
      const connId = frameConnIdAt(pos);
      const conn = connId ? connections.find((c) => c.id === connId) : null;
      if (!conn) { setBanner({ kind: 'err', text: 'Drop the database onto a service frame to create it there.' }); return; }
      const name = window.prompt(`New database name on “${conn.name}”:`, '');
      if (name === null || !name.trim()) return;
      setBanner(null);
      (async () => {
        try {
          await createDatabase(name.trim(), { connection: conn });
          setBanner({ kind: 'ok', text: `Created “${name.trim()}” on ${conn.name}.` });
          await refresh();
        } catch (err: unknown) {
          setBanner({ kind: 'err', text: `Create database failed: ${err instanceof Error ? err.message : String(err)}` });
        }
      })();
    }
  }, [screenToFlowPosition, active, connections, add, refresh]);

  // ---- collections + config ----
  function updateColl(i: number, patch: Partial<CollEntry>) {
    setDesign((d) => { const cs = [...d.collections]; cs[i] = { ...cs[i], ...patch }; return { ...d, collections: cs }; });
  }
  function addColl() { setDesign((d) => ({ ...d, collections: [...d.collections, { name: '', strategy: 'hash', shardKeyPath: '' }] })); }
  function removeColl(i: number) { setDesign((d) => ({ ...d, collections: d.collections.filter((_, j) => j !== i) })); }

  const config = useMemo(() => buildConfig(design, claimed), [design, claimed]);
  const json = useMemo(() => JSON.stringify(config, null, 2), [config]);
  const errors = useMemo(() => validate(design, claimed), [design, claimed]);
  const valid = errors.length === 0;

  const selected = selectedDbId ? claimedOrDbInfo(selectedDbId, snapshots) : null;
  const selectedSvcConn = selectedSvcId ? (connections.find((c) => c.id === selectedSvcId.slice('svc:'.length)) ?? null) : null;
  const setupScript = useMemo(() => buildSetupScript(snapshots), [snapshots]);
  function downloadScript() {
    const blob = new Blob([setupScript], { type: 'text/plain' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url; a.download = 'reproduce-cluster.ps1'; a.click();
    URL.revokeObjectURL(url);
  }

  function download() {
    const blob = new Blob([json], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url; a.download = 'cluster.json'; a.click();
    URL.revokeObjectURL(url);
  }
  async function copy() {
    try { await navigator.clipboard.writeText(json); setCopied(true); window.setTimeout(() => setCopied(false), 1500); }
    catch { /* selectable <pre> fallback */ }
  }
  async function handleSpawn() {
    setBanner(null);
    if (!active) { setBanner({ kind: 'err', text: 'Pick an active connection — its host runs the router.' }); return; }
    if (!valid) { setBanner({ kind: 'err', text: 'Resolve the validation issues first.' }); return; }
    setSpawning(true);
    try {
      const res = await spawnRouter(json, { connection: active, name: design.name.trim() || undefined, port: parsePort(design.port) ?? undefined });
      add({ name: res.name ?? `router-:${res.port}`, baseUrl: res.baseUrl, color: '#d90429', tags: ['router', `parent:${active.baseUrl}`] });
      setBanner({ kind: 'ok', text: `Router live at ${res.baseUrl} — added as a connection.` });
    } catch (e: unknown) {
      setBanner({ kind: 'err', text: e instanceof Error ? e.message : String(e) });
    } finally {
      setSpawning(false);
    }
  }

  // ---- render ----
  if (connections.length === 0) {
    return (
      <div className="card">
        <p>No connections registered yet.</p>
        <p style={{ color: 'var(--gray-500)', fontSize: 13 }}>
          Add one on the <a href="/connections" style={{ color: 'var(--red)' }}>Connections</a> page,
          or drag a <strong>Service</strong> from the palette once you have an active connection.
        </p>
      </div>
    );
  }

  const totalDbs = snapshots.reduce((s, x) => s + x.databases.length, 0);

  return (
    <>
      {/* Palette + layers toolbar */}
      <div className="card" style={{ marginBottom: 12, display: 'flex', alignItems: 'center', gap: 14, flexWrap: 'wrap' }}>
        <span style={{ fontFamily: 'var(--mono)', fontSize: 10, letterSpacing: '0.08em', textTransform: 'uppercase', color: 'var(--gray-500)', fontWeight: 700 }}>Drag onto canvas</span>
        <PaletteChip type="cluster" label="⛁ Cluster" />
        <PaletteChip type="service" label="🖥 Service" />
        <PaletteChip type="database" label="🗄 Database" />
        <span style={{ width: 1, height: 22, background: 'var(--gray-200)' }} />
        <label style={toggleLabel()}><input type="checkbox" checked={showCluster} onChange={(e) => setShowCluster(e.target.checked)} /> Cluster wiring</label>
        <label style={toggleLabel()}><input type="checkbox" checked={showReplication} onChange={(e) => setShowReplication(e.target.checked)} /> Replication</label>
        <span style={{ flex: 1 }} />
        <span style={{ fontSize: 12, color: 'var(--gray-500)' }}>
          {snapshots.length} service{snapshots.length === 1 ? '' : 's'} · {totalDbs} DB · {claimed.length} claimed
        </span>
        <button onClick={resetLayout} style={ghostBtn()}>⤢ Reset layout</button>
        <button onClick={refresh} disabled={loading} style={ghostBtn()}>{loading ? 'Scanning…' : '⟲ Rescan'}</button>
      </div>

      {banner && (
        <div className="card" style={{ marginBottom: 12, borderLeft: `4px solid ${banner.kind === 'ok' ? 'var(--green)' : 'var(--red)'}`, background: banner.kind === 'ok' ? 'rgba(16,185,129,0.06)' : 'rgba(217,4,41,0.05)', color: banner.kind === 'ok' ? 'var(--green)' : 'var(--red)', fontSize: 13 }}>
          {banner.kind === 'ok' ? '✓ ' : '✗ '}{banner.text}
        </div>
      )}

      <div className="topology-stage">
        <div className="topology-canvas" onDrop={onDrop} onDragOver={onDragOver}>
          <ReactFlow
            nodes={nodes}
            edges={edges}
            nodeTypes={nodeTypes}
            onNodesChange={onNodesChange}
            onConnect={onConnect}
            onEdgeClick={onEdgeClick}
            onNodeClick={onNodeClick}
            onPaneClick={() => { setSelectedDbId(null); setSelectedSvcId(null); }}
            connectionLineStyle={{ stroke: 'var(--red)', strokeWidth: 2, strokeDasharray: '6 4' }}
            fitView
            proOptions={{ hideAttribution: true }}
          >
            <Background gap={20} size={1} color="#e6e6e6" />
            <Controls showInteractive={false} />
            <MiniMap nodeColor={(n) => (n.id === 'cluster' ? '#d90429' : ((n.data as BuilderDBNodeData)?.claimed ? '#d90429' : '#a3a3a3'))} />
          </ReactFlow>
        </div>

        <aside className="topology-side">
          <div className="topology-side-header"><div style={{ fontSize: 16, fontWeight: 600 }}>Cluster</div></div>
          <div style={{ marginBottom: 12 }}>
            <label style={labelStyle()}>Router name</label>
            <input type="text" value={design.name} onChange={(e) => setDesign((d) => ({ ...d, name: e.target.value }))} placeholder="studio-cluster" style={inputStyle()} />
          </div>
          <div>
            <label style={labelStyle()}>Router port <span style={{ textTransform: 'none', fontWeight: 400 }}>(blank = host picks)</span></label>
            <input type="text" value={design.port} onChange={(e) => setDesign((d) => ({ ...d, port: e.target.value }))} placeholder="auto" style={inputStyle()} />
          </div>

          <div className="topology-side-section">
            <div className="topology-side-section-label">{selectedSvcConn ? 'SELECTED SERVICE' : 'SELECTED DATABASE'}</div>
            {selectedSvcConn ? (
              <>
                <div className="setting-row"><span className="key">Service</span><span className="val" style={{ fontFamily: 'var(--mono)', fontSize: 11 }}>{selectedSvcConn.name}</span></div>
                <div style={{ marginTop: 8, fontSize: 11, color: 'var(--gray-500)' }}>Launch command (debug / testing):</div>
                <pre className="code-block" style={{ margin: '4px 0 0', fontSize: 11, whiteSpace: 'pre-wrap', wordBreak: 'break-all' }}>{serveCommand(selectedSvcConn)}</pre>
                <button onClick={() => { navigator.clipboard?.writeText(serveCommand(selectedSvcConn)).catch(() => {}); }} style={{ ...ghostBtn(), marginTop: 6, width: '100%' }}>Copy launch command</button>
              </>
            ) : !selected ? (
              <div style={{ fontSize: 12, color: 'var(--gray-500)' }}>Click a DB to rename / claim it — or a service for its launch command.</div>
            ) : (
              <>
                <div className="setting-row"><span className="key">Source</span><span className="val" style={{ fontFamily: 'var(--mono)', fontSize: 11 }}>{selected.conn.name}/{selected.dbName}</span></div>
                <div style={{ margin: '8px 0' }}>
                  <label style={labelStyle()}>Shard name</label>
                  <input
                    type="text"
                    value={design.shardNames[selected.id] ?? ''}
                    onChange={(e) => setDesign((d) => ({ ...d, shardNames: { ...d.shardNames, [selected.id]: e.target.value } }))}
                    placeholder={defaultShardName(selected.conn, selected.dbName, selected.isDefault)}
                    style={inputStyle()}
                  />
                </div>
                <button
                  onClick={() => setDesign((d) => (d.claimed.includes(selected.id) ? { ...d, claimed: d.claimed.filter((x) => x !== selected.id) } : { ...d, claimed: [...d.claimed, selected.id] }))}
                  style={design.claimed.includes(selected.id) ? { ...dangerBtn(), width: '100%' } : { ...primaryBtn(), width: '100%' }}
                >
                  {design.claimed.includes(selected.id) ? 'Unclaim from cluster' : 'Claim into cluster'}
                </button>
              </>
            )}
          </div>

          <div className="topology-side-section">
            <div className="topology-side-section-label">GESTURES</div>
            <div style={{ fontSize: 11, color: 'var(--gray-500)', lineHeight: 1.6 }}>
              <strong>cluster → DB</strong>: claim as a shard.<br />
              <strong>DB → DB</strong>: wire replication (leader → follower).<br />
              Click a red edge to unclaim.
            </div>
          </div>
        </aside>
      </div>

      {/* Collections + generated config */}
      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 16, marginTop: 16 }}>
        <div className="card">
          <h3 style={{ marginTop: 0 }}>Collections</h3>
          <p style={{ fontSize: 12, color: 'var(--gray-500)', marginTop: -6 }}>How the router distributes each collection across the claimed shards.</p>
          <table className="table">
            <thead><tr><th>Name</th><th>Strategy</th><th>Shard key</th><th></th></tr></thead>
            <tbody>
              {design.collections.map((c, i) => (
                <tr key={i}>
                  <td><input type="text" value={c.name} onChange={(e) => updateColl(i, { name: e.target.value })} placeholder="orders" style={cellInput()} /></td>
                  <td>
                    <select value={c.strategy} onChange={(e) => updateColl(i, { strategy: e.target.value as Strategy })} style={{ padding: 6, border: '1px solid var(--gray-200)', fontSize: 13, fontFamily: 'var(--mono)' }}>
                      <option value="hash">hash</option>
                      <option value="replicated">replicated</option>
                      <option value="single">single</option>
                    </select>
                  </td>
                  <td>{c.strategy === 'hash' ? <input type="text" value={c.shardKeyPath ?? ''} onChange={(e) => updateColl(i, { shardKeyPath: e.target.value })} placeholder="e.g. pnr" style={cellInput()} /> : <span style={{ color: 'var(--gray-500)', fontSize: 12 }}>—</span>}</td>
                  <td><button onClick={() => removeColl(i)} style={{ ...ghostBtn(), fontSize: 11, padding: '4px 10px' }}>Remove</button></td>
                </tr>
              ))}
            </tbody>
          </table>
          <div style={{ marginTop: 12 }}><button onClick={addColl} style={ghostBtn()}>+ Add collection</button></div>
        </div>

        <div className="card">
          <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 8 }}>
            <h3 style={{ margin: 0, flex: 1 }}>cluster.json</h3>
            <button onClick={copy} style={ghostBtn()}>{copied ? '✓ Copied' : 'Copy'}</button>
            <button onClick={download} style={ghostBtn()}>Download</button>
            <button onClick={handleSpawn} disabled={spawning || !valid} style={{ ...primaryBtn(), opacity: !valid || spawning ? 0.5 : 1 }}>{spawning ? 'Spawning…' : '⚡ Spawn router'}</button>
          </div>
          <div style={{ fontSize: 11, color: 'var(--gray-500)', marginBottom: 8 }}>↳ Auto-saved in this browser. Download to export the file, or Spawn to launch a live router.</div>
          {errors.length > 0 && (
            <div style={{ marginBottom: 10, padding: '8px 12px', background: 'rgba(217,4,41,0.06)', borderLeft: '3px solid var(--red)', fontSize: 12 }}>
              {errors.map((e, i) => <div key={i} style={{ color: 'var(--red)' }}>• {e}</div>)}
            </div>
          )}
          <pre className="code-block" style={{ maxHeight: 300, overflow: 'auto', margin: 0 }}>{json}</pre>
        </div>
      </div>

      <div className="card" style={{ marginTop: 16 }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 8 }}>
          <h3 style={{ margin: 0, flex: 1 }}>Reproduce this setup</h3>
          <button onClick={() => { navigator.clipboard?.writeText(setupScript).then(() => { setScriptCopied(true); window.setTimeout(() => setScriptCopied(false), 1500); }).catch(() => {}); }} style={ghostBtn()}>{scriptCopied ? '✓ Copied' : 'Copy'}</button>
          <button onClick={downloadScript} style={ghostBtn()}>Download .ps1</button>
        </div>
        <div style={{ fontSize: 11, color: 'var(--gray-500)', marginBottom: 8 }}>
          Commands to recreate every service, its databases, replication wiring, and the router — handy for spinning the topology back up for debugging / testing.
        </div>
        <pre className="code-block" style={{ maxHeight: 280, overflow: 'auto', margin: 0 }}>{setupScript}</pre>
      </div>

      {wireDraft && (
        <WireModal
          busy={wiring}
          source={`${wireDraft.sourceConn.name} / ${wireDraft.sourceName}`}
          target={`${wireDraft.targetConn.name} / ${wireDraft.targetName}`}
          suggestedPort={wireDraft.suggestedPort}
          onCancel={() => setWireDraft(null)}
          onConfirm={confirmWire}
        />
      )}
    </>
  );
}

function claimedOrDbInfo(id: string, snapshots: Snapshot[]): ClaimInfo | null {
  const { connId, dbName } = parseDbNodeId(id);
  for (const s of snapshots) {
    if (s.conn.id !== connId) continue;
    const db = s.databases.find((d) => d.name === dbName);
    if (db) return { id, conn: s.conn, dbName, isDefault: db.isDefault };
  }
  return null;
}

// ---------------------------------------------------------------- palette chip

function PaletteChip({ type, label }: { type: string; label: string }) {
  return (
    <div
      draggable
      onDragStart={(e) => { e.dataTransfer.setData('application/df-node', type); e.dataTransfer.effectAllowed = 'move'; }}
      style={{
        padding: '6px 12px', border: '1px dashed var(--gray-200)', borderRadius: 4, fontSize: 12, fontWeight: 600,
        cursor: 'grab', background: 'white', userSelect: 'none',
      }}
      title={`Drag onto the canvas to add a ${type}`}
    >
      {label}
    </div>
  );
}

// ---------------------------------------------------------------- wire modal

function WireModal({ busy, source, target, suggestedPort, onCancel, onConfirm }: {
  busy: boolean; source: string; target: string; suggestedPort: number; onCancel: () => void; onConfirm: (port: number) => void;
}) {
  const [port, setPort] = useState(suggestedPort);
  return (
    <div className="topology-modal-backdrop" onClick={onCancel}>
      <div className="topology-modal" onClick={(e) => e.stopPropagation()}>
        <div style={{ fontFamily: 'var(--mono)', fontSize: 11, letterSpacing: '0.08em', textTransform: 'uppercase', color: 'var(--red)', fontWeight: 700, marginBottom: 12 }}>Wire replication</div>
        <div style={{ marginBottom: 16, display: 'flex', alignItems: 'center', gap: 12, fontSize: 14, flexWrap: 'wrap' }}>
          <span style={{ padding: '6px 12px', background: 'rgba(217,4,41,0.08)', border: '1px solid var(--red)', fontWeight: 600 }}>{source}</span>
          <span style={{ color: 'var(--gray-500)' }}>(leader)</span>
          <span style={{ color: 'var(--red)', fontSize: 18 }}>→</span>
          <span style={{ padding: '6px 12px', background: 'rgba(59,130,246,0.08)', border: '1px solid #3b82f6', fontWeight: 600 }}>{target}</span>
          <span style={{ color: 'var(--gray-500)' }}>(follower)</span>
        </div>
        <div style={{ marginBottom: 16 }}>
          <label style={labelStyle()}>Replication TCP port</label>
          <input type="number" value={port} onChange={(e) => setPort(parseInt(e.target.value) || 0)} style={{ width: 140, padding: '8px 10px', fontSize: 14, border: '1px solid var(--gray-200)', fontFamily: 'var(--mono)' }} />
          <div style={{ marginTop: 6, fontSize: 11, color: 'var(--gray-500)' }}>Leader listens here for the follower. Must be free + ≥ 1024.</div>
        </div>
        <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end' }}>
          <button type="button" onClick={onCancel} style={ghostBtn()}>Cancel</button>
          <button type="button" onClick={() => onConfirm(port)} disabled={busy || port < 1024} style={{ ...primaryBtn(), padding: '8px 18px' }}>{busy ? 'Wiring…' : 'Wire replication'}</button>
        </div>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------- styles

function primaryBtn(): React.CSSProperties { return { background: 'var(--red)', color: 'white', border: 'none', padding: '6px 14px', fontSize: 12, fontWeight: 600, cursor: 'pointer' }; }
function ghostBtn(): React.CSSProperties { return { background: 'transparent', color: 'var(--ink)', border: '1px solid var(--gray-200)', padding: '6px 12px', fontSize: 12, cursor: 'pointer' }; }
function dangerBtn(): React.CSSProperties { return { background: 'transparent', color: 'var(--red)', border: '1px solid var(--red)', padding: '6px 12px', fontSize: 12, fontWeight: 600, cursor: 'pointer' }; }
function labelStyle(): React.CSSProperties { return { display: 'block', fontFamily: 'var(--mono)', fontSize: 10, letterSpacing: '0.08em', textTransform: 'uppercase', color: 'var(--gray-500)', marginBottom: 4, fontWeight: 700 }; }
function inputStyle(): React.CSSProperties { return { width: '100%', padding: '8px 10px', fontSize: 14, border: '1px solid var(--gray-200)', fontFamily: 'var(--sans)', background: 'white' }; }
function cellInput(): React.CSSProperties { return { background: 'transparent', border: '1px solid var(--gray-200)', padding: '4px 6px', fontFamily: 'var(--mono)', fontSize: 13, width: '100%' }; }
function toggleLabel(): React.CSSProperties { return { display: 'flex', alignItems: 'center', gap: 5, fontSize: 12, cursor: 'pointer' }; }
