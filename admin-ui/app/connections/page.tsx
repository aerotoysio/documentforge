'use client';

import { useEffect, useMemo, useState } from 'react';
import { useConnections } from '@/lib/connections-context';
import { getHealth, listServices, spawnService, stopService, spawnRouter, listDatabases, type ManagedServiceInfo } from '@/lib/api';
import { discoverNetwork, normalizeUrl, type DiscoveredNode, type DiscoveryResult } from '@/lib/discovery';
import type { Connection } from '@/lib/connections';

const PALETTE = ['#d90429', '#ef476f', '#f59e0b', '#10b981', '#3b82f6', '#8b5cf6', '#ec4899', '#0ea5e9'];

export default function ConnectionsPage() {
  const { connections, active, setActive, add, update, remove } = useConnections();
  const [draft, setDraft] = useState<Partial<Connection>>({ name: '', baseUrl: '', apiKey: '', color: PALETTE[0] });
  const [editingId, setEditingId] = useState<string | null>(null);
  const [testing, setTesting] = useState<Record<string, { ok?: boolean; msg?: string; pinging?: boolean }>>({});

  // Issue #66 Phase 5 — spawn-local-service state.
  const [spawnName, setSpawnName] = useState('');
  const [spawnPort, setSpawnPort] = useState('');
  const [spawnDataDir, setSpawnDataDir] = useState('');
  // Shard membership for the new Connection. Backend ignores it — it's
  // purely a Studio-side grouping label that the Across-services view
  // uses to draw shard frames.
  const [spawnShard, setSpawnShard] = useState('');
  const [spawning, setSpawning] = useState(false);
  const [spawnError, setSpawnError] = useState<string | null>(null);
  const [managedServices, setManagedServices] = useState<ManagedServiceInfo[]>([]);

  // Poll the active host's managed children so the user can see what was
  // spawned through this Studio session. Silent on services that predate
  // Phase 5 — feature is purely additive.
  useEffect(() => {
    if (!active) { setManagedServices([]); return; }
    let cancelled = false;
    async function refresh() {
      try {
        const r = await listServices({ connection: active ?? undefined });
        if (!cancelled) setManagedServices(r.services);
      } catch {
        if (!cancelled) setManagedServices([]);  // pre-#66 service or transient
      }
    }
    refresh();
    const id = window.setInterval(refresh, 4000);
    return () => { cancelled = true; window.clearInterval(id); };
  }, [active?.id, active?.baseUrl]);

  async function handleSpawn(e?: React.FormEvent) {
    e?.preventDefault();
    if (!active) {
      setSpawnError('Pick an active connection first — that service hosts the spawned children.');
      return;
    }
    setSpawnError(null);
    setSpawning(true);
    try {
      const port = spawnPort.trim() ? parseInt(spawnPort.trim(), 10) : undefined;
      const result = await spawnService({
        connection: active,
        nodeName: spawnName.trim() || undefined,
        port,
        dataDir: spawnDataDir.trim() || undefined,
      });
      // Auto-register the new service as a Connection so it's immediately
      // usable. Inherit the host's API key — children spawned by the host
      // run unauthenticated by default (dev mode). The optional shard tag
      // lands the new connection in the right shard frame on the
      // Across-services topology view.
      add({
        name: result.nodeName || `spawned-:${result.port}`,
        baseUrl: result.baseUrl,
        apiKey: undefined,
        color: PALETTE[(connections.length + 1) % PALETTE.length],
        tags: ['spawned', `parent:${active.baseUrl}`],
        shard: spawnShard.trim() || undefined,
      });
      setSpawnName(''); setSpawnPort(''); setSpawnDataDir(''); setSpawnShard('');
      // Refresh the managed-services list.
      try {
        const r = await listServices({ connection: active });
        setManagedServices(r.services);
      } catch { /* ignore */ }
    } catch (e: any) {
      setSpawnError(e.message || String(e));
    } finally {
      setSpawning(false);
    }
  }

  // ---- Phase 6b — spawn-router state + handlers ----
  const [routerJson, setRouterJson] = useState('');
  const [routerName, setRouterName] = useState('');
  const [routerPort, setRouterPort] = useState('');
  const [routerSpawning, setRouterSpawning] = useState(false);
  const [routerError, setRouterError] = useState<string | null>(null);

  // Pre-populate the cluster config textarea by probing the active
  // connection's databases. Each attached DB becomes a candidate
  // ring leader; the operator can prune + add collections to taste
  // before clicking Spawn. The result is a starter cluster.json
  // that's immediately routable.
  async function buildStarterClusterConfig() {
    if (!active) return;
    try {
      const r = await listDatabases({ connection: active });
      const shards = r.databases.map((d) => ({
        name: `ring-${d.name}`,
        leader: { baseUrl: active.baseUrl, database: d.name },
      }));
      const config = {
        router: { name: 'studio-cluster', port: 5500 },
        shards,
        collections: [
          { name: 'orders', strategy: 'hash', shardKeyPath: 'pnr' },
          { name: 'lookup', strategy: 'replicated' },
        ],
      };
      setRouterJson(JSON.stringify(config, null, 2));
    } catch (e: any) {
      setRouterError(`Could not probe ${active.name}: ${e.message || String(e)}`);
    }
  }

  async function handleSpawnRouter(e?: React.FormEvent) {
    e?.preventDefault();
    if (!active) {
      setRouterError('Pick an active connection — its host is what spawns the router process.');
      return;
    }
    if (!routerJson.trim()) {
      setRouterError('Paste or build a cluster.json first.');
      return;
    }
    // Quick syntax check so we fail fast in the UI rather than at the
    // backend. Backend's ClusterConfig.Validate() catches semantic issues.
    try { JSON.parse(routerJson); }
    catch (e: any) { setRouterError(`Invalid JSON: ${e.message}`); return; }

    setRouterError(null);
    setRouterSpawning(true);
    try {
      const port = routerPort.trim() ? parseInt(routerPort.trim(), 10) : undefined;
      const result = await spawnRouter(routerJson, {
        connection: active,
        name: routerName.trim() || undefined,
        port,
      });
      // Auto-register the router URL as a Connection so the operator
      // can immediately point Studio at it. The router speaks the
      // same /collections + /query surface as a service, so it Just
      // Works as a connection target.
      add({
        name: result.name ?? `router-:${result.port}`,
        baseUrl: result.baseUrl,
        apiKey: undefined,
        color: PALETTE[(connections.length + 1) % PALETTE.length],
        tags: ['router', `parent:${active.baseUrl}`],
      });
      // Refresh managed services list.
      try {
        const list = await listServices({ connection: active });
        setManagedServices(list.services);
      } catch { /* ignore */ }
      setRouterJson('');
      setRouterName('');
      setRouterPort('');
    } catch (e: any) {
      setRouterError(e.message || String(e));
    } finally {
      setRouterSpawning(false);
    }
  }

  async function handleStopSpawned(port: number) {
    if (!active) return;
    if (!confirm(`Stop the spawned service on port ${port}? The data files stay on disk.`)) return;
    try {
      await stopService(port, { connection: active });
      const r = await listServices({ connection: active });
      setManagedServices(r.services);
      // Remove its Connection too (best-effort match by baseUrl).
      const dead = connections.find(c => c.baseUrl.endsWith(`:${port}`));
      if (dead) remove(dead.id);
    } catch (e: any) {
      alert(`Could not stop :${port}: ${e.message || String(e)}`);
    }
  }

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

      {/* Issue #66 Phase 5 — spawn a child dfdb serve process on the
          machine that's hosting the active connection. Studio auto-
          registers the new service as a Connection. The fastest way
          to build a local cluster for testing. */}
      <div className="card" style={{ borderTop: '3px solid var(--red)', marginBottom: 24 }}>
        <h3 style={{ marginTop: 0 }}>Spawn local service</h3>
        <p style={{ fontSize: 13, color: 'var(--gray-500)', marginTop: -8 }}>
          Start a sibling <code>dfdb serve</code> on the host that's running your active connection.
          Studio auto-registers it here so you can immediately work against it — pair this with
          per-DB replication on the <a href="/topology">Topology</a> page to build a local cluster.
        </p>
        {!active && (
          <div style={{ padding: '10px 12px', background: 'rgba(217,4,41,0.06)', color: 'var(--red)', fontSize: 12, borderLeft: '3px solid var(--red)' }}>
            Pick an active connection in the sidebar — its service is what spawns the children.
          </div>
        )}
        {active && (
          <>
            <form onSubmit={handleSpawn} style={{ display: 'grid', gridTemplateColumns: '2fr 1fr 1.5fr 2fr auto', gap: 10, alignItems: 'end' }}>
              <div>
                <div className="key">Node name <span style={{ color: 'var(--gray-500)', fontWeight: 400 }}>(optional)</span></div>
                <input
                  type="text"
                  value={spawnName}
                  onChange={e => setSpawnName(e.target.value)}
                  placeholder="shard-a"
                  disabled={spawning}
                />
              </div>
              <div>
                <div className="key">Port <span style={{ color: 'var(--gray-500)', fontWeight: 400 }}>(blank = free)</span></div>
                <input
                  type="text"
                  value={spawnPort}
                  onChange={e => setSpawnPort(e.target.value)}
                  placeholder="auto"
                  disabled={spawning}
                />
              </div>
              <div>
                <div className="key">Shard <span style={{ color: 'var(--gray-500)', fontWeight: 400 }}>(Studio grouping)</span></div>
                <input
                  type="text"
                  value={spawnShard}
                  onChange={e => setSpawnShard(e.target.value)}
                  placeholder="orders"
                  disabled={spawning}
                />
              </div>
              <div>
                <div className="key">Data dir <span style={{ color: 'var(--gray-500)', fontWeight: 400 }}>(blank = under host)</span></div>
                <input
                  type="text"
                  value={spawnDataDir}
                  onChange={e => setSpawnDataDir(e.target.value)}
                  placeholder="C:\\path\\to\\dir"
                  disabled={spawning}
                />
              </div>
              <button
                type="submit"
                disabled={spawning}
                style={{ background: 'var(--red)', color: 'white', border: 'none', padding: '8px 18px', fontWeight: 700, cursor: spawning ? 'wait' : 'pointer', minHeight: 38 }}
              >
                {spawning ? 'Spawning…' : '⚡ Spawn'}
              </button>
            </form>
            {spawnError && (
              <div style={{ marginTop: 12, padding: '10px 12px', background: 'rgba(217,4,41,0.08)', color: 'var(--red)', fontFamily: 'var(--mono)', fontSize: 12, borderLeft: '3px solid var(--red)' }}>
                ✗ {spawnError}
              </div>
            )}
            {managedServices.length > 0 && (
              <div style={{ marginTop: 16, paddingTop: 12, borderTop: '1px solid var(--gray-100)' }}>
                <div style={{ fontFamily: 'var(--mono)', fontSize: 10, letterSpacing: '0.08em', textTransform: 'uppercase', color: 'var(--gray-500)', fontWeight: 700, marginBottom: 8 }}>
                  Managed by {active.name} — {managedServices.length} child{managedServices.length === 1 ? '' : 'ren'}
                </div>
                <div style={{ display: 'grid', gap: 6 }}>
                  {managedServices.map(s => (
                    <div key={s.port} style={{ display: 'grid', gridTemplateColumns: '1fr auto', alignItems: 'center', gap: 12, padding: '6px 0' }}>
                      <div>
                        <div style={{ fontSize: 13, fontWeight: 600 }}>
                          {s.nodeName ?? `port-${s.port}`}
                          <span style={{ marginLeft: 8, fontFamily: 'var(--mono)', fontSize: 11, color: s.running ? 'var(--green)' : 'var(--red)' }}>
                            {s.running ? '● running' : `✗ exited${s.exitCode != null ? ` (${s.exitCode})` : ''}`}
                          </span>
                        </div>
                        <div style={{ fontFamily: 'var(--mono)', fontSize: 11, color: 'var(--gray-500)' }}>
                          {s.baseUrl} · {s.dataDir}
                        </div>
                      </div>
                      <button
                        onClick={() => handleStopSpawned(s.port)}
                        style={{ background: 'transparent', color: 'var(--red)', border: '1px solid var(--red)', padding: '4px 10px', fontSize: 11, cursor: 'pointer' }}
                      >
                        Stop
                      </button>
                    </div>
                  ))}
                </div>
              </div>
            )}
          </>
        )}
      </div>

      {/* Phase 6b — spawn a cluster router process. Same hosting model
          as "Spawn local service" (the active connection's machine runs
          the child), but the child is a `dfdb router` rather than a
          `dfdb serve`. Studio auto-adds the router as a Connection so
          it appears in the sidebar picker immediately. */}
      <div className="card" style={{ borderTop: '3px solid var(--red)', marginBottom: 24 }}>
        <h3 style={{ marginTop: 0 }}>Spawn cluster router</h3>
        <p style={{ fontSize: 13, color: 'var(--gray-500)', marginTop: -8 }}>
          Front an entire cluster behind one URL. Paste a <code>cluster.json</code> or hit{' '}
          <em>Build starter config</em> to scaffold one from the databases on your active
          connection. Studio auto-registers the new router as a Connection so you can switch
          to it in the sidebar instantly.
        </p>
        {!active && (
          <div style={{ padding: '10px 12px', background: 'rgba(217,4,41,0.06)', color: 'var(--red)', fontSize: 12, borderLeft: '3px solid var(--red)' }}>
            Pick an active connection in the sidebar — its host is what runs the router process.
          </div>
        )}
        {active && (
          <>
            <form onSubmit={handleSpawnRouter} style={{ display: 'grid', gap: 10 }}>
              <div style={{ display: 'grid', gridTemplateColumns: '2fr 1fr auto auto', gap: 10, alignItems: 'end' }}>
                <div>
                  <div className="key">Router name <span style={{ color: 'var(--gray-500)', fontWeight: 400 }}>(optional)</span></div>
                  <input
                    type="text"
                    value={routerName}
                    onChange={e => setRouterName(e.target.value)}
                    placeholder="studio-cluster"
                    disabled={routerSpawning}
                  />
                </div>
                <div>
                  <div className="key">Port <span style={{ color: 'var(--gray-500)', fontWeight: 400 }}>(blank = free)</span></div>
                  <input
                    type="text"
                    value={routerPort}
                    onChange={e => setRouterPort(e.target.value)}
                    placeholder="auto"
                    disabled={routerSpawning}
                  />
                </div>
                <button
                  type="button"
                  onClick={buildStarterClusterConfig}
                  disabled={routerSpawning}
                  style={{ background: 'transparent', color: 'var(--ink)', border: '1px solid var(--gray-200)', padding: '8px 14px', fontSize: 12, cursor: 'pointer', minHeight: 38 }}
                  title="Generate a cluster.json from the databases on the active connection"
                >
                  ↺ Build starter config
                </button>
                <button
                  type="submit"
                  disabled={routerSpawning || !routerJson.trim()}
                  style={{ background: 'var(--red)', color: 'white', border: 'none', padding: '8px 18px', fontWeight: 700, cursor: routerSpawning ? 'wait' : 'pointer', minHeight: 38 }}
                >
                  {routerSpawning ? 'Spawning…' : '⚡ Spawn router'}
                </button>
              </div>
              <div>
                <div className="key">cluster.json</div>
                <textarea
                  value={routerJson}
                  onChange={e => setRouterJson(e.target.value)}
                  disabled={routerSpawning}
                  rows={14}
                  spellCheck={false}
                  placeholder={`{\n  "shards": [\n    { "name": "ring-a", "leader": { "baseUrl": "http://localhost:5099", "database": "ring_a" } },\n    { "name": "ring-b", "leader": { "baseUrl": "http://localhost:5099", "database": "ring_b" } }\n  ],\n  "collections": [\n    { "name": "orders", "strategy": "hash", "shardKeyPath": "pnr" },\n    { "name": "lookup", "strategy": "replicated" }\n  ]\n}`}
                  style={{ width: '100%', minHeight: 240, fontFamily: 'var(--mono)', fontSize: 12, padding: 10, border: '1px solid var(--gray-200)', background: '#fafafa', resize: 'vertical' }}
                />
              </div>
            </form>
            {routerError && (
              <div style={{ marginTop: 12, padding: '10px 12px', background: 'rgba(217,4,41,0.08)', color: 'var(--red)', fontFamily: 'var(--mono)', fontSize: 12, borderLeft: '3px solid var(--red)' }}>
                ✗ {routerError}
              </div>
            )}
          </>
        )}
      </div>

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
