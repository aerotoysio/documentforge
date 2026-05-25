// Thin client for the dfdb REST API.
//
// Every call resolves URL + auth from the *active connection* in the
// connection registry (see lib/connections.ts). For a one-off override
// (e.g. health-check the swarm), pass `{ connection }` to any function.

import { Connection, getActiveConnection } from './connections';

const ENV_FALLBACK_URL = process.env.NEXT_PUBLIC_DFDB_URL || 'http://localhost:5000';
const LEGACY_KEY_KEY = 'dfdb_api_key';

export interface CallOptions {
  connection?: Connection | null;
}

// Shape of GET /replication/status (engine: ServeCommand.cs:895). Field
// nullability matches the engine — read it before assuming non-null.
export interface ReplicationStatus {
  node: string;
  role: 'leader' | 'follower' | 'none';
  readOnly: boolean;
  httpEndpoint: string | null;     // this node's own HTTP base URL (issue #51)
  leader: {
    currentSeq: number;
    followerCount: number;
    followers: ReplicationFollowerInfo[];
  };
  follower: {
    lastAppliedSeq: number;
    opsApplied: number;
    gapsDetected: number;
    autoFailoverPromoted: boolean;
    leader: { endpoint: string; httpEndpoint: string | null } | null;
  };
}

export interface ReplicationFollowerInfo {
  endpoint: string;                // replication endpoint (host:port), always set
  httpEndpoint: string | null;     // null when follower runs a pre-#51 build
  connectedAt: string;
  handshakeSeq: number;
  worstCaseLagSeq: number;
}

function isBrowser() { return typeof window !== 'undefined'; }

function resolveConn(opts?: CallOptions): { baseUrl: string; apiKey?: string } {
  const explicit = opts?.connection;
  if (explicit) return { baseUrl: explicit.baseUrl, apiKey: explicit.apiKey };

  const active = getActiveConnection();
  if (active) return { baseUrl: active.baseUrl, apiKey: active.apiKey };

  // No registry yet (first paint, before migration runs). Use env + legacy key.
  const legacyKey = isBrowser() ? window.localStorage.getItem(LEGACY_KEY_KEY) ?? undefined : undefined;
  return { baseUrl: ENV_FALLBACK_URL, apiKey: legacyKey };
}

function urlFor(path: string, opts?: CallOptions): string {
  const { baseUrl } = resolveConn(opts);
  return baseUrl.replace(/\/+$/, '') + path;
}

function authHeaders(opts?: CallOptions): Record<string, string> {
  const { apiKey } = resolveConn(opts);
  return apiKey ? { Authorization: `Bearer ${apiKey}` } : {};
}

async function handle(r: Response) {
  const text = await r.text();
  let body: any;
  try { body = text ? JSON.parse(text) : null; } catch { body = text; }
  if (r.status === 401) {
    throw new Error('UNAUTHORIZED: API key missing or invalid for this connection.');
  }
  if (!r.ok) {
    const msg = (body && (body.error || body.message)) || `${r.status} ${r.statusText}`;
    const err: any = new Error(msg);
    err.status = r.status;
    err.body = body;
    throw err;
  }
  return body;
}

// ---------- Legacy compat ----------
// Keep the old API_URL export so older components don't break; new code should
// read from the active connection directly via useActiveConnection().
export const API_URL = ENV_FALLBACK_URL;

export function getApiKey(): string | null {
  const { apiKey } = resolveConn();
  return apiKey ?? null;
}
export function setApiKey(key: string | null) {
  // Legacy seam: writes to the legacy storage key. Not used by the new
  // connection-aware flow, but kept so /settings still works.
  if (!isBrowser()) return;
  if (key) window.localStorage.setItem(LEGACY_KEY_KEY, key);
  else window.localStorage.removeItem(LEGACY_KEY_KEY);
}

// ---------- Health & meta ----------
export async function getHealth(opts?: CallOptions) {
  const r = await fetch(urlFor('/health', opts));
  return handle(r);
}

export async function getStats(opts?: CallOptions) {
  const r = await fetch(urlFor('/stats', opts), { headers: authHeaders(opts) });
  return handle(r);
}

export async function getCollections(opts?: CallOptions) {
  const r = await fetch(urlFor('/collections', opts), { headers: authHeaders(opts) });
  return handle(r);
}

export async function getReplicationStatus(opts?: CallOptions): Promise<ReplicationStatus> {
  const r = await fetch(urlFor('/replication/status', opts), { headers: authHeaders(opts) });
  return handle(r);
}

// ---------- Query ----------
// Issue #66 Phase 3a: pass `database` to target a specific attached DB
// rather than the service's current default. Omitting it falls back to
// the flat /query route — the back-compat path for any service that
// predates multi-DB.
export async function query(sql: string, opts?: CallOptions & { database?: string }) {
  const path = opts?.database
    ? `/db/${encodeURIComponent(opts.database)}/query`
    : '/query';
  const r = await fetch(urlFor(path, opts), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders(opts) },
    body: JSON.stringify({ sql }),
  });
  return handle(r);
}

// ---------- Document CRUD ----------
export async function listDocs(collection: string, limit = 100, opts?: CallOptions) {
  const r = await fetch(urlFor(`/collections/${collection}?limit=${limit}`, opts), { headers: authHeaders(opts) });
  return handle(r);
}

export async function findById(collection: string, id: string, opts?: CallOptions) {
  const r = await fetch(urlFor(`/collections/${collection}/${id}`, opts), { headers: authHeaders(opts) });
  return handle(r);
}

export async function findByField(collection: string, field: string, value: string, opts?: CallOptions) {
  const r = await fetch(urlFor(`/collections/${collection}/by/${field}/${encodeURIComponent(value)}`, opts), { headers: authHeaders(opts) });
  return handle(r);
}

export async function insertDoc(collection: string, doc: any, opts?: CallOptions) {
  const r = await fetch(urlFor(`/collections/${collection}`, opts), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders(opts) },
    body: typeof doc === 'string' ? doc : JSON.stringify(doc),
  });
  return handle(r);
}

export async function bulkInsert(
  collection: string,
  docs: any[],
  opts?: CallOptions & { atomic?: boolean; skipIndexes?: boolean },
) {
  const params = new URLSearchParams();
  if (opts?.atomic) params.set('atomic', 'true');
  if (opts?.skipIndexes) params.set('skipIndexes', 'true');
  const qs = params.toString();
  const r = await fetch(urlFor(`/collections/${collection}/bulk${qs ? '?' + qs : ''}`, opts), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders(opts) },
    body: JSON.stringify(docs),
  });
  return handle(r);
}

export async function replaceById(collection: string, id: string, doc: any, opts?: CallOptions) {
  const r = await fetch(urlFor(`/collections/${collection}/${id}`, opts), {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json', ...authHeaders(opts) },
    body: typeof doc === 'string' ? doc : JSON.stringify(doc),
  });
  return handle(r);
}

export async function replaceByField(collection: string, field: string, value: string, doc: any, opts?: CallOptions) {
  const r = await fetch(urlFor(`/collections/${collection}/by/${field}/${encodeURIComponent(value)}`, opts), {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json', ...authHeaders(opts) },
    body: typeof doc === 'string' ? doc : JSON.stringify(doc),
  });
  return handle(r);
}

export async function deleteById(collection: string, id: string, opts?: CallOptions) {
  const r = await fetch(urlFor(`/collections/${collection}/${id}`, opts), {
    method: 'DELETE',
    headers: authHeaders(opts),
  });
  return handle(r);
}

export async function deleteByField(collection: string, field: string, value: string, opts?: CallOptions) {
  const r = await fetch(urlFor(`/collections/${collection}/by/${field}/${encodeURIComponent(value)}`, opts), {
    method: 'DELETE',
    headers: authHeaders(opts),
  });
  return handle(r);
}

export async function dropCollection(collection: string, opts?: CallOptions) {
  const r = await fetch(urlFor(`/collections/${collection}`, opts), {
    method: 'DELETE',
    headers: { 'X-Confirm': 'true', ...authHeaders(opts) },
  });
  return handle(r);
}

// ---------- Indexes ----------
export async function getIndexes(collection: string, opts?: CallOptions) {
  const r = await fetch(urlFor(`/indexes/${collection}`, opts), { headers: authHeaders(opts) });
  return handle(r);
}

export async function createIndex(collection: string, path: string, name: string, unique: boolean, opts?: CallOptions) {
  const r = await fetch(urlFor('/index', opts), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders(opts) },
    body: JSON.stringify({ collection, path, name, unique }),
  });
  return handle(r);
}

// ---------- Admin ----------
export async function flushDb(opts?: CallOptions) {
  const r = await fetch(urlFor('/admin/flush', opts), { method: 'POST', headers: authHeaders(opts) });
  return handle(r);
}

export async function checkpointDb(opts?: CallOptions) {
  const r = await fetch(urlFor('/admin/checkpoint', opts), { method: 'POST', headers: authHeaders(opts) });
  return handle(r);
}

export async function compactCollection(collection: string, opts?: CallOptions) {
  const r = await fetch(urlFor(`/admin/compact/${collection}`, opts), { method: 'POST', headers: authHeaders(opts) });
  return handle(r);
}

export async function rebuildIndexes(collection: string, opts?: CallOptions) {
  const r = await fetch(urlFor(`/admin/rebuild-indexes/${collection}`, opts), { method: 'POST', headers: authHeaders(opts) });
  return handle(r);
}

export async function rebuildIndex(collection: string, indexName: string, opts?: CallOptions) {
  const r = await fetch(urlFor(`/admin/rebuild-index/${collection}/${indexName}`, opts), { method: 'POST', headers: authHeaders(opts) });
  return handle(r);
}

// ---------- Databases (Issue #66 Phase 2) ----------
// One service can now host N attached databases. These verbs drive Studio's
// "+ Add Database" UX and the per-connection database picker. Until Phase 4
// lands per-request auth scoping, set-default is how the UI tells the
// service "all subsequent flat-route calls should target DB X."

export interface DatabaseEntry {
  name: string;
  filePath: string;
  isDefault: boolean;
}

export interface DatabasesListResponse {
  default: string | null;
  count: number;
  databases: DatabaseEntry[];
}

export async function listDatabases(opts?: CallOptions): Promise<DatabasesListResponse> {
  const r = await fetch(urlFor('/databases', opts), { headers: authHeaders(opts) });
  return handle(r);
}

export async function createDatabase(
  name: string,
  options?: { path?: string; createIfMissing?: boolean } & CallOptions,
): Promise<DatabaseEntry> {
  const r = await fetch(urlFor('/databases', options), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders(options) },
    body: JSON.stringify({
      name,
      path: options?.path,
      createIfMissing: options?.createIfMissing ?? true,
    }),
  });
  return handle(r);
}

export async function deleteDatabase(
  name: string,
  options?: { deleteFiles?: boolean } & CallOptions,
) {
  const qs = options?.deleteFiles ? '?deleteFiles=true' : '';
  const r = await fetch(urlFor(`/databases/${encodeURIComponent(name)}${qs}`, options), {
    method: 'DELETE',
    headers: authHeaders(options),
  });
  return handle(r);
}

export async function setDefaultDatabase(name: string, opts?: CallOptions) {
  const r = await fetch(urlFor(`/databases/${encodeURIComponent(name)}/set-default`, opts), {
    method: 'POST',
    headers: authHeaders(opts),
  });
  return handle(r);
}

// Issue #83 — list .dfdb files in data-dir that aren't currently attached.
// Powers Studio's "Browse & attach" panel: orphan files from a stale
// service restart, files dropped into a mounted volume, etc.
export interface UnattachedFile {
  suggestedName: string;
  nameConflict: boolean;     // basename collides with an already-attached DB
  path: string;
  sizeBytes: number;
  modifiedUtc: string;
}
export interface UnattachedResponse {
  dataDir: string;
  recursive: boolean;
  count: number;
  files: UnattachedFile[];
}
export async function listUnattachedDatabases(
  options?: { recursive?: boolean } & CallOptions,
): Promise<UnattachedResponse> {
  const qs = options?.recursive ? '?recursive=true' : '';
  const r = await fetch(urlFor(`/databases/unattached${qs}`, options), {
    headers: authHeaders(options),
  });
  return handle(r);
}

// Issue #84 — runtime rescan + auto-attach. One-click "I dropped N
// files into the volume, attach them all now." Honours Detach
// tombstones (those need explicit re-attach via the Browse panel).
export interface DiscoverResponse {
  dataDir: string;
  recursive: boolean;
  discovered: number;
  skippedTombstoned: number;
  errors: string[];
  attached: Array<{ name: string; path: string }>;
}
export async function discoverDatabases(
  options?: { recursive?: boolean } & CallOptions,
): Promise<DiscoverResponse> {
  const qs = options?.recursive ? '?recursive=true' : '';
  const r = await fetch(urlFor(`/databases/discover${qs}`, options), {
    method: 'POST',
    headers: authHeaders(options),
  });
  return handle(r);
}

// ---------- Per-DB replication (Issue #66 Phase 2.5) ----------
// Phase 2.5 scoped endpoints under /db/{name}/replication/*. Each attached
// DB has its own role; Studio's topology page uses these to render and
// configure the intra-service leader/follower graph.

export interface PerDbReplicationStatus {
  database: string;
  role: 'leader' | 'follower' | 'none';
  readOnly: boolean;
  leader: {
    currentSeq: number;
    port: number | null;        // port this DB is leading on; null when not a leader
    followerCount: number;
    followers: Array<{
      endpoint: string;
      httpEndpoint: string | null;
      connectedAt: string;
      handshakeSeq: number;
      worstCaseLagSeq: number;
    }>;
  };
  follower: {
    lastAppliedSeq: number;
    opsApplied: number;
    gapsDetected: number;
    autoFailoverPromoted: boolean;
    leader: { endpoint: string; httpEndpoint: string | null } | null;
  };
}

export async function getDbReplicationStatus(
  name: string,
  opts?: CallOptions,
): Promise<PerDbReplicationStatus> {
  const r = await fetch(
    urlFor(`/db/${encodeURIComponent(name)}/replication/status`, opts),
    { headers: authHeaders(opts) },
  );
  return handle(r);
}

// Issue #66 Phase 6b — collection list per attached DB. Used by the
// Collections tab to auto-discover what's where across the cluster.
export async function listDbCollections(
  name: string,
  opts?: CallOptions,
): Promise<{ database: string; collections: string[] }> {
  const r = await fetch(
    urlFor(`/db/${encodeURIComponent(name)}/collections`, opts),
    { headers: authHeaders(opts) },
  );
  return handle(r);
}

export async function startDbAsLeader(name: string, port: number, opts?: CallOptions) {
  const r = await fetch(
    urlFor(`/db/${encodeURIComponent(name)}/replication/start-leader`, opts),
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', ...authHeaders(opts) },
      body: JSON.stringify({ port }),
    },
  );
  return handle(r);
}

export async function startDbAsFollower(
  name: string,
  host: string,
  port: number,
  opts?: CallOptions,
) {
  const r = await fetch(
    urlFor(`/db/${encodeURIComponent(name)}/replication/start-follower`, opts),
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', ...authHeaders(opts) },
      body: JSON.stringify({ host, port }),
    },
  );
  return handle(r);
}

// ---------- Service orchestration (Issue #66 Phase 5) ----------
// Spawn / stop sibling `dfdb serve` processes from an existing service.
// The "+ Spawn local service" button in the Connections page calls these;
// the new HTTP base URL comes back and Studio auto-registers it as a
// Connection so the cluster grows in one click.

export interface ManagedServiceInfo {
  port: number;
  baseUrl: string;
  dataDir: string;
  nodeName: string | null;
  startedAt: string;
  running: boolean;
  exitCode: number | null;
  logPath: string;
}

export interface SpawnServiceResponse {
  port: number;
  baseUrl: string;
  dataDir: string;
  nodeName: string | null;
  startedAt: string;
  ready: boolean;
  logPath: string;
}

export async function listServices(opts?: CallOptions): Promise<{ count: number; services: ManagedServiceInfo[] }> {
  const r = await fetch(urlFor('/services', opts), { headers: authHeaders(opts) });
  return handle(r);
}

export async function spawnService(
  options?: { dataDir?: string; port?: number; nodeName?: string } & CallOptions,
): Promise<SpawnServiceResponse> {
  const r = await fetch(urlFor('/services', options), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders(options) },
    body: JSON.stringify({
      dataDir: options?.dataDir,
      port: options?.port,
      nodeName: options?.nodeName,
    }),
  });
  return handle(r);
}

export async function stopService(port: number, opts?: CallOptions) {
  const r = await fetch(urlFor(`/services/${port}`, opts), {
    method: 'DELETE',
    headers: authHeaders(opts),
  });
  return handle(r);
}

export async function getServiceLog(port: number, opts?: CallOptions): Promise<string> {
  const r = await fetch(urlFor(`/services/${port}/logs`, opts), { headers: authHeaders(opts) });
  if (!r.ok) throw new Error(`Failed to fetch log: ${r.status} ${r.statusText}`);
  return r.text();
}

// ---------- Managed API keys (Issue #73) ----------
// Stored in the service's _system database via the KeyStore. The
// admin token (legacy apiKey) is what unlocks these endpoints; from
// there operators mint per-database scoped keys for tenant apps.

export interface AdminKeyInfo {
  id: string;
  scopes: string[];
  description: string | null;
  createdAt: string;
}

export interface MintedKey {
  id: string;
  /** Plaintext secret — returned ONCE on mint. Show, store, then it's gone. */
  secret: string;
  scopes: string[];
  description: string | null;
  createdAt: string;
  warning: string;
}

export async function listAdminKeys(opts?: CallOptions): Promise<{ count: number; keys: AdminKeyInfo[] }> {
  const r = await fetch(urlFor('/admin/keys', opts), { headers: authHeaders(opts) });
  return handle(r);
}

export async function createAdminKey(
  scopes: string[],
  description: string | null | undefined,
  opts?: CallOptions,
): Promise<MintedKey> {
  const r = await fetch(urlFor('/admin/keys', opts), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders(opts) },
    body: JSON.stringify({ scopes, description: description || null }),
  });
  return handle(r);
}

export async function deleteAdminKey(id: string, opts?: CallOptions) {
  const r = await fetch(urlFor(`/admin/keys/${encodeURIComponent(id)}`, opts), {
    method: 'DELETE',
    headers: authHeaders(opts),
  });
  return handle(r);
}

// ---------- Spawn cluster router (Issue #66 Phase 6b) ----------
// One-click cluster router from Studio. The host service writes the
// cluster.json to disk and starts a `dfdb router` child against it.
// Studio auto-registers the new router as a Connection.

export interface SpawnRouterResponse {
  kind: 'router';
  port: number;
  baseUrl: string;
  configPath: string;
  name: string | null;
  startedAt: string;
  ready: boolean;
  logPath: string;
}

export async function spawnRouter(
  clusterConfigJson: string,
  options?: { port?: number; name?: string } & CallOptions,
): Promise<SpawnRouterResponse> {
  const r = await fetch(urlFor('/routers', options), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders(options) },
    body: JSON.stringify({
      clusterConfigJson,
      port: options?.port,
      name: options?.name,
    }),
  });
  return handle(r);
}

// ---------- Replication control ----------
export async function startLeader(port: number, sharedSecret?: string, opts?: CallOptions) {
  const r = await fetch(urlFor('/replication/start-leader', opts), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders(opts) },
    body: JSON.stringify({ port, sharedSecret }),
  });
  return handle(r);
}

export async function startFollower(host: string, port: number, sharedSecret?: string, opts?: CallOptions) {
  const r = await fetch(urlFor('/replication/start-follower', opts), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders(opts) },
    body: JSON.stringify({ host, port, sharedSecret }),
  });
  return handle(r);
}

export async function promoteToLeader(port: number, opts?: CallOptions) {
  const r = await fetch(urlFor('/replication/promote', opts), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders(opts) },
    body: JSON.stringify({ port }),
  });
  return handle(r);
}

export async function setReadOnly(readOnly: boolean, opts?: CallOptions) {
  const r = await fetch(urlFor(readOnly ? '/replication/read-only' : '/replication/read-write', opts), {
    method: 'POST',
    headers: authHeaders(opts),
  });
  return handle(r);
}

export async function enableAutoFailover(silenceSeconds: number, newLeaderPort: number, opts?: CallOptions) {
  const r = await fetch(urlFor('/replication/auto-failover/enable', opts), {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders(opts) },
    body: JSON.stringify({ silenceSeconds, newLeaderPort }),
  });
  return handle(r);
}

export async function disableAutoFailover(opts?: CallOptions) {
  const r = await fetch(urlFor('/replication/auto-failover/disable', opts), {
    method: 'POST',
    headers: authHeaders(opts),
  });
  return handle(r);
}
