'use client';

import { createContext, useContext, useEffect, useState, useCallback } from 'react';
import {
  Connection,
  loadConnections,
  saveConnections,
  getActiveConnectionId,
  setActiveConnectionId,
  addConnection as registryAdd,
  updateConnection as registryUpdate,
  removeConnection as registryRemove,
  migrateLegacy,
} from './connections';

interface Ctx {
  connections: Connection[];
  active: Connection | null;
  setActive: (id: string | null) => void;
  add: (input: Omit<Connection, 'id' | 'createdAt'>) => Connection;
  update: (id: string, patch: Partial<Connection>) => Connection | null;
  remove: (id: string) => void;
  reload: () => void;
}

const ConnCtx = createContext<Ctx | null>(null);

export function ConnectionsProvider({ children }: { children: React.ReactNode }) {
  const [connections, setConnections] = useState<Connection[]>([]);
  const [activeId, setActiveIdState] = useState<string | null>(null);

  const reload = useCallback(() => {
    setConnections(loadConnections());
    setActiveIdState(getActiveConnectionId());
  }, []);

  // Hydrate once on mount + run legacy migration if needed.
  useEffect(() => {
    const envUrl = process.env.NEXT_PUBLIC_DFDB_URL;
    migrateLegacy(envUrl);
    reload();
  }, [reload]);

  // Refresh whenever localStorage changes from another tab.
  useEffect(() => {
    function onStorage(e: StorageEvent) {
      if (e.key === 'dfdb_connections' || e.key === 'dfdb_active_connection') reload();
    }
    window.addEventListener('storage', onStorage);
    return () => window.removeEventListener('storage', onStorage);
  }, [reload]);

  const setActive = useCallback((id: string | null) => {
    setActiveConnectionId(id);
    setActiveIdState(id);
  }, []);

  const add = useCallback((input: Omit<Connection, 'id' | 'createdAt'>) => {
    const c = registryAdd(input);
    reload();
    return c;
  }, [reload]);

  const update = useCallback((id: string, patch: Partial<Connection>) => {
    const c = registryUpdate(id, patch);
    reload();
    return c;
  }, [reload]);

  const remove = useCallback((id: string) => {
    registryRemove(id);
    reload();
  }, [reload]);

  const active = connections.find(c => c.id === activeId) ?? null;

  return (
    <ConnCtx.Provider value={{ connections, active, setActive, add, update, remove, reload }}>
      {children}
    </ConnCtx.Provider>
  );
}

export function useConnections(): Ctx {
  const ctx = useContext(ConnCtx);
  if (!ctx) throw new Error('useConnections() must be used inside <ConnectionsProvider>');
  return ctx;
}

/**
 * Convenience: just the active connection without exposing the rest of the registry.
 * Returns null when no connection is configured.
 */
export function useActiveConnection(): Connection | null {
  return useConnections().active;
}
