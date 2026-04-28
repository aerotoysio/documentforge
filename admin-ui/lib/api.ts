// Thin client for the dfdb REST API (from `dfdb serve`).
// Override the default URL with NEXT_PUBLIC_DFDB_URL.
// API key (bearer token) is stored in localStorage and attached to every request.

export const API_URL = process.env.NEXT_PUBLIC_DFDB_URL || 'http://localhost:5000';
const KEY_STORAGE = 'dfdb_api_key';

export function getApiKey(): string | null {
  if (typeof window === 'undefined') return null;
  return window.localStorage.getItem(KEY_STORAGE);
}

export function setApiKey(key: string | null) {
  if (typeof window === 'undefined') return;
  if (key) window.localStorage.setItem(KEY_STORAGE, key);
  else window.localStorage.removeItem(KEY_STORAGE);
}

function authHeaders(): Record<string, string> {
  const key = getApiKey();
  return key ? { Authorization: `Bearer ${key}` } : {};
}

async function handle(r: Response) {
  const text = await r.text();
  let body: any;
  try { body = text ? JSON.parse(text) : null; } catch { body = text; }
  if (r.status === 401) {
    throw new Error('UNAUTHORIZED: API key missing or invalid. Set one in Settings.');
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

// ---------- Health & meta ----------
export async function getHealth() {
  const r = await fetch(`${API_URL}/health`);
  return handle(r);
}

export async function getStats() {
  const r = await fetch(`${API_URL}/stats`, { headers: authHeaders() });
  return handle(r);
}

export async function getCollections() {
  const r = await fetch(`${API_URL}/collections`, { headers: authHeaders() });
  return handle(r);
}

export async function getReplicationStatus() {
  const r = await fetch(`${API_URL}/replication/status`, { headers: authHeaders() });
  return handle(r);
}

// ---------- Query ----------
export async function query(sql: string) {
  const r = await fetch(`${API_URL}/query`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: JSON.stringify({ sql }),
  });
  return handle(r);
}

// ---------- Document CRUD ----------
export async function listDocs(collection: string, limit = 100) {
  const r = await fetch(`${API_URL}/collections/${collection}?limit=${limit}`, { headers: authHeaders() });
  return handle(r);
}

export async function findById(collection: string, id: string) {
  const r = await fetch(`${API_URL}/collections/${collection}/${id}`, { headers: authHeaders() });
  return handle(r);
}

export async function findByField(collection: string, field: string, value: string) {
  const r = await fetch(`${API_URL}/collections/${collection}/by/${field}/${encodeURIComponent(value)}`, { headers: authHeaders() });
  return handle(r);
}

export async function insertDoc(collection: string, doc: any) {
  const r = await fetch(`${API_URL}/collections/${collection}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: typeof doc === 'string' ? doc : JSON.stringify(doc),
  });
  return handle(r);
}

export async function bulkInsert(
  collection: string,
  docs: any[],
  opts: { atomic?: boolean; skipIndexes?: boolean } = {},
) {
  const params = new URLSearchParams();
  if (opts.atomic) params.set('atomic', 'true');
  if (opts.skipIndexes) params.set('skipIndexes', 'true');
  const qs = params.toString();
  const r = await fetch(`${API_URL}/collections/${collection}/bulk${qs ? '?' + qs : ''}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: JSON.stringify(docs),
  });
  return handle(r);
}

export async function replaceById(collection: string, id: string, doc: any) {
  const r = await fetch(`${API_URL}/collections/${collection}/${id}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: typeof doc === 'string' ? doc : JSON.stringify(doc),
  });
  return handle(r);
}

export async function replaceByField(collection: string, field: string, value: string, doc: any) {
  const r = await fetch(`${API_URL}/collections/${collection}/by/${field}/${encodeURIComponent(value)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: typeof doc === 'string' ? doc : JSON.stringify(doc),
  });
  return handle(r);
}

export async function deleteById(collection: string, id: string) {
  const r = await fetch(`${API_URL}/collections/${collection}/${id}`, {
    method: 'DELETE',
    headers: authHeaders(),
  });
  return handle(r);
}

export async function deleteByField(collection: string, field: string, value: string) {
  const r = await fetch(`${API_URL}/collections/${collection}/by/${field}/${encodeURIComponent(value)}`, {
    method: 'DELETE',
    headers: authHeaders(),
  });
  return handle(r);
}

export async function dropCollection(collection: string) {
  const r = await fetch(`${API_URL}/collections/${collection}`, {
    method: 'DELETE',
    headers: { 'X-Confirm': 'true', ...authHeaders() },
  });
  return handle(r);
}

// ---------- Indexes ----------
export async function getIndexes(collection: string) {
  const r = await fetch(`${API_URL}/indexes/${collection}`, { headers: authHeaders() });
  return handle(r);
}

export async function createIndex(collection: string, path: string, name: string, unique: boolean) {
  const r = await fetch(`${API_URL}/index`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: JSON.stringify({ collection, path, name, unique }),
  });
  return handle(r);
}

// ---------- Admin ----------
export async function flushDb() {
  const r = await fetch(`${API_URL}/admin/flush`, { method: 'POST', headers: authHeaders() });
  return handle(r);
}

export async function compactCollection(collection: string) {
  const r = await fetch(`${API_URL}/admin/compact/${collection}`, { method: 'POST', headers: authHeaders() });
  return handle(r);
}

export async function rebuildIndexes(collection: string) {
  const r = await fetch(`${API_URL}/admin/rebuild-indexes/${collection}`, { method: 'POST', headers: authHeaders() });
  return handle(r);
}

export async function rebuildIndex(collection: string, indexName: string) {
  const r = await fetch(`${API_URL}/admin/rebuild-index/${collection}/${indexName}`, { method: 'POST', headers: authHeaders() });
  return handle(r);
}
