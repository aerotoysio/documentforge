'use client';

import { useEffect, useState } from 'react';
import { getStats, getReplicationStatus } from '@/lib/api';
import { useActiveConnection } from '@/lib/connections-context';

export interface ExplorerProps {
  refreshKey: number;
  onRefresh: () => void;
  onOpenBrowse: (collection: string) => void;
  onOpenIndexes: (collection: string) => void;
  onSetStatusMeta: (meta: { node?: string; role?: string; readOnly?: boolean; endpoint?: string }) => void;
}

interface CollNode {
  name: string;
  documentCount: number;
  indexes: { name: string; path: string; unique: boolean; entries?: number }[];
  expanded: boolean;
  indexesExpanded: boolean;
}

export function Explorer({ refreshKey, onRefresh, onOpenBrowse, onOpenIndexes, onSetStatusMeta }: ExplorerProps) {
  const active = useActiveConnection();
  const endpointLabel = active?.baseUrl ?? '—';
  const [colls, setColls] = useState<CollNode[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [serverExpanded, setServerExpanded] = useState(true);
  const [replOpen, setReplOpen] = useState(false);
  const [repl, setRepl] = useState<any>(null);

  useEffect(() => {
    let cancelled = false;
    setError(null);

    Promise.all([getStats(), getReplicationStatus().catch(() => null)])
      .then(([stats, replication]) => {
        if (cancelled) return;
        const fetched: CollNode[] = (stats.collections ?? []).map((c: any) => ({
          name: c.name,
          documentCount: c.documentCount,
          indexes: (c.indexes ?? []).map((i: any) => ({ name: i.name, path: i.path, unique: i.unique, entries: i.entries })),
          expanded: false,
          indexesExpanded: false,
        }));
        // Preserve expansion state from previous render
        setColls(prev => fetched.map(f => {
          const existing = prev.find(p => p.name === f.name);
          return existing ? { ...f, expanded: existing.expanded, indexesExpanded: existing.indexesExpanded } : f;
        }));
        setRepl(replication);
        onSetStatusMeta({
          node: replication?.node,
          role: replication?.role,
          readOnly: replication?.readOnly,
          endpoint: endpointLabel,
        });
      })
      .catch(e => { if (!cancelled) setError(e.message || String(e)); });

    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [refreshKey, endpointLabel]);

  function toggle(idx: number, key: 'expanded' | 'indexesExpanded') {
    setColls(prev => prev.map((c, i) => i === idx ? { ...c, [key]: !c[key] } : c));
  }

  return (
    <aside className="studio-explorer">
      <div className="explorer-header">
        Explorer
        <span className="refresh" onClick={onRefresh} title="Refresh">⟲</span>
      </div>

      {/* Server node */}
      <div className={`tree-node depth-0`} onClick={() => setServerExpanded(s => !s)}>
        <span className="twisty">{serverExpanded ? '▼' : '▶'}</span>
        <span className="icon">▣</span>
        <span className="label">{endpointLabel.replace(/^https?:\/\//, '')}</span>
      </div>

      {serverExpanded && (
        <>
          {/* Collections folder */}
          <div className="tree-node depth-1" onClick={() => {/* pseudo-folder, always open */}}>
            <span className="twisty">▼</span>
            <span className="icon">📁</span>
            <span className="label">Collections</span>
            <span className="badge">{colls.length}</span>
          </div>
          {colls.length === 0 && !error && (
            <div className="tree-node depth-2" style={{ color: 'var(--gray-500)', fontStyle: 'italic', cursor: 'default' }}>
              <span className="twisty"></span>
              <span>(none yet)</span>
            </div>
          )}
          {error && (
            <div className="tree-node depth-2" style={{ color: 'var(--red)', cursor: 'default' }}>
              <span className="twisty">!</span>
              <span style={{ fontFamily: 'var(--mono)', fontSize: 11 }}>{error}</span>
            </div>
          )}
          {colls.map((c, i) => (
            <CollectionRow
              key={c.name}
              coll={c}
              onToggle={() => toggle(i, 'expanded')}
              onToggleIndexes={() => toggle(i, 'indexesExpanded')}
              onBrowse={() => onOpenBrowse(c.name)}
              onIndexes={() => onOpenIndexes(c.name)}
            />
          ))}

          {/* Replication folder */}
          <div className="tree-node depth-1" onClick={() => setReplOpen(o => !o)}>
            <span className="twisty">{replOpen ? '▼' : '▶'}</span>
            <span className="icon">⇄</span>
            <span className="label">Replication</span>
            {repl && <span className="badge">{repl.role}</span>}
          </div>
          {replOpen && repl && (
            <>
              <div className="tree-node depth-2" style={{ cursor: 'default' }}>
                <span></span><span>role: <strong>{repl.role}</strong></span>
              </div>
              <div className="tree-node depth-2" style={{ cursor: 'default' }}>
                <span></span><span>read-only: {String(repl.readOnly)}</span>
              </div>
              {repl.role === 'leader' && (
                <>
                  <div className="tree-node depth-2" style={{ cursor: 'default' }}>
                    <span></span><span>seq: {repl.leader?.currentSeq}</span>
                  </div>
                  <div className="tree-node depth-2" style={{ cursor: 'default' }}>
                    <span></span><span>followers: {repl.leader?.followerCount}</span>
                  </div>
                </>
              )}
              {repl.role === 'follower' && (
                <>
                  <div className="tree-node depth-2" style={{ cursor: 'default' }}>
                    <span></span><span>last seq: {repl.follower?.lastAppliedSeq}</span>
                  </div>
                  <div className="tree-node depth-2" style={{ cursor: 'default' }}>
                    <span></span><span>gaps: {repl.follower?.gapsDetected}</span>
                  </div>
                </>
              )}
            </>
          )}
        </>
      )}
    </aside>
  );
}

function CollectionRow({ coll, onToggle, onToggleIndexes, onBrowse, onIndexes }: {
  coll: CollNode;
  onToggle: () => void;
  onToggleIndexes: () => void;
  onBrowse: () => void;
  onIndexes: () => void;
}) {
  return (
    <>
      <div className="tree-node depth-2" onClick={onToggle} onDoubleClick={onBrowse} title="Click to expand · Double-click to browse documents">
        <span className="twisty">{coll.expanded ? '▼' : '▶'}</span>
        <span className="icon">▤</span>
        <span className="label">{coll.name}</span>
        <span className="badge">{coll.documentCount.toLocaleString()}</span>
      </div>
      {coll.expanded && (
        <>
          <div className="tree-node depth-3" onClick={onBrowse} title="Open browse tab">
            <span></span><span style={{ fontFamily: 'var(--sans)' }}>📄 Documents</span>
          </div>
          <div className="tree-node depth-3" onClick={onToggleIndexes} onDoubleClick={onIndexes} title="Click to expand · Double-click to manage">
            <span className="twisty">{coll.indexesExpanded ? '▼' : '▶'}</span>
            <span style={{ fontFamily: 'var(--sans)' }}>🔑 Indexes</span>
            <span className="badge">{coll.indexes.length}</span>
          </div>
          {coll.indexesExpanded && coll.indexes.map(idx => (
            <div key={idx.name} className={`tree-node depth-3 ${idx.unique ? 'unique' : ''}`} style={{ paddingLeft: 84 }} title={`${idx.path}${idx.unique ? ' (unique)' : ''}`}>
              <span></span><span>{idx.name}</span>
              {idx.entries != null && <span className="badge">{idx.entries.toLocaleString()}</span>}
            </div>
          ))}
        </>
      )}
    </>
  );
}
