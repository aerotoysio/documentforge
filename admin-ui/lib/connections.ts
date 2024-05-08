// Connection registry for the admin UI.
// Stored in localStorage so it persists across sessions and survives reloads.
// Each connection is a logical pointer to a `dfdb serve` endpoint; the active
// one drives every API call elsewhere in the app.

export interface Connection {
  id: string;
  name: string;          // human label - "Production", "Render staging", "shard-a"
  baseUrl: string;       // https://documentforge.onrender.com  (no trailing slash)
  apiKey?: string;       // bearer token for /admin/* and authenticated routes
  color?: string;        // optional accent (used for status-bar / swarm-cards)
  tags?: string[];       // free-form labels: 'prod', 'shard-a', 'leader', 'staging'
  createdAt: string;     // ISO timestamp
}

const CONNECTIONS_KEY = 'dfdb_connections';
const ACTIVE_KEY = 'dfdb_active_connection';
const LEGACY_KEY_KEY = 'dfdb_api_key';

// ---------- Storage IO ----------

function isBrowser(): boolean {
  return typeof window !== 'undefined' && typeof window.localStorage !== 'undefined';
}

export function loadConnections(): Connection[] {
  if (!isBrowser()) return [];
  try {
    const raw = window.localStorage.getItem(CONNECTIONS_KEY);
    if (!raw) return [];
    const parsed = JSON.parse(raw);
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

export function saveConnections(conns: Connection[]) {
  if (!isBrowser()) return;
  window.localStorage.setItem(CONNECTIONS_KEY, JSON.stringify(conns));
}

export function getActiveConnectionId(): string | null {
  if (!isBrowser()) return null;
  return window.localStorage.getItem(ACTIVE_KEY);
}

export function setActiveConnectionId(id: string | null) {
  if (!isBrowser()) return;
  if (id) window.localStorage.setItem(ACTIVE_KEY, id);
  else window.localStorage.removeItem(ACTIVE_KEY);
}

export function getActiveConnection(): Connection | null {
  const id = getActiveConnectionId();
  if (!id) return null;
  return loadConnections().find(c => c.id === id) ?? null;
}

// ---------- CRUD ----------

export function addConnection(input: Omit<Connection, 'id' | 'createdAt'>): Connection {
  const conn: Connection = {
    ...input,
    id: cryptoId(),
    createdAt: new Date().toISOString(),
    baseUrl: input.baseUrl.replace(/\/+$/, ''),
  };
  const all = loadConnections();
  all.push(conn);
  saveConnections(all);
  // Auto-activate the first one so the UI never has nothing pointed at.
  if (all.length === 1) setActiveConnectionId(conn.id);
  return conn;
}

export function updateConnection(id: string, patch: Partial<Connection>) {
  const all = loadConnections();
  const idx = all.findIndex(c => c.id === id);
  if (idx === -1) return null;
  all[idx] = { ...all[idx], ...patch, baseUrl: (patch.baseUrl ?? all[idx].baseUrl).replace(/\/+$/, '') };
  saveConnections(all);
  return all[idx];
}

export function removeConnection(id: string) {
  const all = loadConnections().filter(c => c.id !== id);
  saveConnections(all);
  // If we just deleted the active one, fall back to the first remaining.
  if (getActiveConnectionId() === id) {
    setActiveConnectionId(all.length ? all[0].id : null);
  }
}

// ---------- Migration ----------

/**
 * If there is no connection registry yet but a legacy api key + the env-var
 * default URL exist, seed a "Default" connection so existing users wake up
 * to a working app instead of a blank one.
 */
export function migrateLegacy(envUrl: string | undefined): Connection | null {
  if (!isBrowser()) return null;
  if (loadConnections().length > 0) return null;

  const url = envUrl ?? 'http://localhost:5000';
  const legacyKey = window.localStorage.getItem(LEGACY_KEY_KEY) ?? undefined;

  return addConnection({
    name: 'Default',
    baseUrl: url,
    apiKey: legacyKey,
    color: '#d90429',
    tags: [],
  });
}

// ---------- Helpers ----------

function cryptoId(): string {
  if (isBrowser() && 'crypto' in window && 'randomUUID' in window.crypto) {
    return window.crypto.randomUUID();
  }
  return 'c_' + Math.random().toString(36).slice(2, 12);
}
