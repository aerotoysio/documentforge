// Thin client for the DocumentForge.Api sample running locally.
// Override the default by setting NEXT_PUBLIC_DFDB_URL.

export const API_URL = process.env.NEXT_PUBLIC_DFDB_URL || 'http://localhost:5000';

export async function query(sql: string) {
  const r = await fetch(`${API_URL}/query`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ sql }),
  });
  return r.json();
}

export async function getStats() {
  const r = await fetch(`${API_URL}/stats`);
  return r.json();
}

export async function getCollections() {
  const r = await fetch(`${API_URL}/collections`);
  return r.json();
}

export async function getIndexes(collection: string) {
  const r = await fetch(`${API_URL}/indexes/${collection}`);
  return r.json();
}

export async function insertDoc(collection: string, doc: any) {
  const r = await fetch(`${API_URL}/collections/${collection}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(doc),
  });
  return r.json();
}

export async function createIndex(collection: string, path: string, name: string, unique: boolean) {
  const r = await fetch(`${API_URL}/index`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ collection, path, name, unique }),
  });
  return r.json();
}
