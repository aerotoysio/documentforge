// Thin client for the DocumentForge.Api sample.
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
  if (r.status === 401) {
    throw new Error('UNAUTHORIZED: API key missing or invalid. Set one in Settings.');
  }
  return r.json();
}

export async function query(sql: string) {
  const r = await fetch(`${API_URL}/query`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: JSON.stringify({ sql }),
  });
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

export async function getIndexes(collection: string) {
  const r = await fetch(`${API_URL}/indexes/${collection}`, { headers: authHeaders() });
  return handle(r);
}

export async function insertDoc(collection: string, doc: any) {
  const r = await fetch(`${API_URL}/collections/${collection}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeaders() },
    body: JSON.stringify(doc),
  });
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
