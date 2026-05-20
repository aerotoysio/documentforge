'use client';

// Studio Explorer — the full picture: every registered connection → its
// attached databases → their collections. The default DB of each connection
// is fetched richly (doc counts + indexes via /stats); other attached DBs list
// their collection names via /db/{name}/collections. Clicking a collection
// opens a query tab pinned to that connection + database.

import { Fragment, useEffect, useState } from 'react';
import { getStats, listDatabases, listDbCollections, getReplicationStatus } from '@/lib/api';
import { useConnections } from '@/lib/connections-context';
import type { Connection } from '@/lib/connections';

export interface ExplorerProps {
  refreshKey: number;
  onRefresh: () => void;
  onOpenBrowse: (collection: string, opts: { connectionId: string; database?: string }) => void;
  onOpenIndexes: (collection: string, opts: { connectionId: string }) => void;
  onSetStatusMeta: (meta: { node?: string; role?: string; readOnly?: boolean; endpoint?: string }) => void;
}

interface IndexInfo { name: string; path: string; unique: boolean; entries?: number }
interface CollInfo { name: string; documentCount?: number; indexes?: IndexInfo[] }
interface DbInfo { name: string; isDefault: boolean; collections: CollInfo[] }
interface ConnTree { conn: Connection; dbs: DbInfo[]; error: string | null }

export function Explorer({ refreshKey, onRefresh, onOpenBrowse, onOpenIndexes, onSetStatusMeta }: ExplorerProps) {
  const { connections, active } = useConnections();
  const [trees, setTrees] = useState<ConnTree[]>([]);
  const [loading, setLoading] = useState(true);
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    (async () => {
      const result: ConnTree[] = await Promise.all(connections.map(async (conn): Promise<ConnTree> => {
        try {
          const list = await listDatabases({ connection: conn });
          const dbs: DbInfo[] = await Promise.all(list.databases.map(async (db): Promise<DbInfo> => {
            if (db.isDefault) {
              try {
                const stats = await getStats({ connection: conn });
                return {
                  name: db.name, isDefault: true,
                  collections: (stats.collections ?? []).map((c: any) => ({
                    name: c.name,
                    documentCount: c.documentCount,
                    indexes: (c.indexes ?? []).map((i: any) => ({ name: i.name, path: i.path, unique: i.unique, entries: i.entries })),
                  })),
                };
              } catch { return { name: db.name, isDefault: true, collections: [] }; }
            }
            try {
              const r = await listDbCollections(db.name, { connection: conn });
              return { name: db.name, isDefault: false, collections: r.collections.map((n) => ({ name: n })) };
            } catch { return { name: db.name, isDefault: false, collections: [] }; }
          }));
          return { conn, dbs, error: null };
        } catch (e: any) {
          return { conn, dbs: [], error: e?.message || String(e) };
        }
      }));
      if (cancelled) return;
      setTrees(result);
      setLoading(false);

      // Status bar still reflects the active connection.
      const act = connections.find((c) => c.id === active?.id);
      if (act) {
        getReplicationStatus({ connection: act })
          .then((repl) => { if (!cancelled) onSetStatusMeta({ node: repl?.node, role: repl?.role, readOnly: repl?.readOnly, endpoint: act.baseUrl }); })
          .catch(() => { if (!cancelled) onSetStatusMeta({ endpoint: act.baseUrl }); });
      }
    })();
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [refreshKey, connections.map((c) => c.id + c.baseUrl).join('|'), active?.id]);

  const isExp = (key: string, def = false) => expanded[key] ?? def;
  const toggle = (key: string) => setExpanded((p) => ({ ...p, [key]: !(p[key] ?? false) }));

  return (
    <aside className="studio-explorer">
      <div className="explorer-header">
        Explorer
        <span className="refresh" onClick={onRefresh} title="Refresh">⟲</span>
      </div>

      {connections.length === 0 && (
        <div className="tree-node depth-1" style={{ color: 'var(--gray-500)', fontStyle: 'italic', cursor: 'default' }}>
          <span className="twisty"></span><span>no connections — add one on Connections</span>
        </div>
      )}

      {trees.map((tree) => {
        const cKey = `c:${tree.conn.id}`;
        const cOpen = isExp(cKey, tree.conn.id === active?.id);
        const dbCount = tree.dbs.length;
        return (
          <Fragment key={tree.conn.id}>
            <div className="tree-node depth-0" onClick={() => toggle(cKey)} title={tree.conn.baseUrl}>
              <span className="twisty">{cOpen ? '▼' : '▶'}</span>
              <span style={{ display: 'inline-block', width: 7, height: 7, borderRadius: '50%', marginRight: 6, background: tree.error ? 'var(--red)' : 'var(--green)' }} />
              <span className="label">{tree.conn.name}</span>
              {tree.conn.id === active?.id && <span className="badge">default</span>}
              {!tree.error && <span className="badge">{dbCount} db</span>}
            </div>

            {cOpen && tree.error && (
              <div className="tree-node depth-1" style={{ color: 'var(--red)', cursor: 'default' }}>
                <span className="twisty">!</span>
                <span style={{ fontFamily: 'var(--mono)', fontSize: 11 }}>unreachable</span>
              </div>
            )}

            {cOpen && !tree.error && tree.dbs.map((db) => {
              const dKey = `d:${tree.conn.id}/${db.name}`;
              const dOpen = isExp(dKey, db.isDefault);
              const browseOpts = { connectionId: tree.conn.id, database: db.isDefault ? undefined : db.name };
              return (
                <Fragment key={db.name}>
                  <div className="tree-node depth-1" onClick={() => toggle(dKey)}>
                    <span className="twisty">{dOpen ? '▼' : '▶'}</span>
                    <span className="icon">🗄</span>
                    <span className="label">{db.name}</span>
                    {db.isDefault && <span className="badge">default</span>}
                    <span className="badge">{db.collections.length}</span>
                  </div>

                  {dOpen && db.collections.length === 0 && (
                    <div className="tree-node depth-2" style={{ color: 'var(--gray-500)', fontStyle: 'italic', cursor: 'default' }}>
                      <span className="twisty"></span><span>(no collections)</span>
                    </div>
                  )}

                  {dOpen && db.collections.map((coll) => {
                    // Non-default DBs only support /db/{name}/query, so their
                    // collections are leaves that open a scoped query.
                    if (!db.isDefault) {
                      return (
                        <div key={coll.name} className="tree-node depth-2" onClick={() => onOpenBrowse(coll.name, browseOpts)} title="Open a query against this database">
                          <span className="twisty"></span>
                          <span className="icon">▤</span>
                          <span className="label">{coll.name}</span>
                        </div>
                      );
                    }
                    // Default DB: expandable to Documents / Indexes.
                    const collKey = `coll:${tree.conn.id}/${db.name}/${coll.name}`;
                    const open = isExp(collKey, false);
                    return (
                      <Fragment key={coll.name}>
                        <div className="tree-node depth-2" onClick={() => toggle(collKey)} onDoubleClick={() => onOpenBrowse(coll.name, browseOpts)} title="Click to expand · Double-click to query">
                          <span className="twisty">{open ? '▼' : '▶'}</span>
                          <span className="icon">▤</span>
                          <span className="label">{coll.name}</span>
                          {coll.documentCount != null && <span className="badge">{coll.documentCount.toLocaleString()}</span>}
                        </div>
                        {open && (
                          <>
                            <div className="tree-node depth-3" onClick={() => onOpenBrowse(coll.name, browseOpts)} title="Open query tab">
                              <span></span><span style={{ fontFamily: 'var(--sans)' }}>📄 Documents</span>
                            </div>
                            <div className="tree-node depth-3" onClick={() => onOpenIndexes(coll.name, { connectionId: tree.conn.id })} title="Manage indexes">
                              <span></span><span style={{ fontFamily: 'var(--sans)' }}>🔑 Indexes</span>
                              {coll.indexes && <span className="badge">{coll.indexes.length}</span>}
                            </div>
                          </>
                        )}
                      </Fragment>
                    );
                  })}
                </Fragment>
              );
            })}
          </Fragment>
        );
      })}

      {loading && trees.length === 0 && (
        <div className="tree-node depth-1" style={{ color: 'var(--gray-500)', fontStyle: 'italic', cursor: 'default' }}>
          <span className="twisty"></span><span>scanning connections…</span>
        </div>
      )}
    </aside>
  );
}
