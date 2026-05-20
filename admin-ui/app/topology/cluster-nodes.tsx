// Custom react-flow nodes for the unified topology / cluster canvas (Phase 6c).
//
//   - ClusterNode: the router meta-node. Its bottom source handle is dragged
//     onto a DB to claim that DB as a shard ring of the cluster.
//   - ServiceFrameNode: a registered connection (service). A resizable group
//     frame that contains its database nodes.
//   - BuilderDBNode: a live attached database. Shows replication role + whether
//     it's claimed into the cluster (a "shard"). Two handles:
//       top    = target  → receives a cluster-claim edge OR a replication edge
//                          (the gesture's meaning is disambiguated by the
//                           SOURCE node type, so no editing "mode" is needed)
//       bottom = source  → drag to another DB to make this one its leader.

import { Handle, Position, NodeResizer, type NodeProps, type Node } from '@xyflow/react';

export interface ClusterNodeData extends Record<string, unknown> {
  name: string;
  port: number | null;
  shardCount: number;
}

export function ClusterNode({ data, selected }: NodeProps<Node<ClusterNodeData>>) {
  return (
    <div style={{
      background: 'var(--red)',
      color: 'white',
      border: `2px solid ${selected ? 'var(--ink)' : 'var(--red)'}`,
      borderRadius: 4,
      padding: '10px 16px',
      minWidth: 196,
      textAlign: 'center',
      boxShadow: '0 4px 14px rgba(217,4,41,0.30)',
      fontFamily: 'var(--sans)',
      cursor: 'grab',
    }}>
      <div style={{ fontFamily: 'var(--mono)', fontSize: 9, letterSpacing: '0.12em', textTransform: 'uppercase', opacity: 0.85 }}>
        Cluster router · design
      </div>
      <div style={{ fontWeight: 800, fontSize: 15, marginTop: 2 }}>{data.name || 'unnamed-cluster'}</div>
      <div style={{ fontFamily: 'var(--mono)', fontSize: 11, marginTop: 3, opacity: 0.9 }}>
        {data.port != null ? `:${data.port}` : 'auto port'} · {data.shardCount} shard{data.shardCount === 1 ? '' : 's'}
      </div>
      <div style={{ fontSize: 10, marginTop: 5, opacity: 0.85 }}>drag ↓ onto a database to claim it</div>
      <Handle
        type="source"
        position={Position.Bottom}
        style={{ background: 'white', width: 11, height: 11, border: '2px solid var(--red)' }}
      />
    </div>
  );
}

export interface ServiceFrameData extends Record<string, unknown> {
  connName: string;
  baseUrl: string;
  error: string | null;
}

export function ServiceFrameNode({ data, selected }: NodeProps<Node<ServiceFrameData>>) {
  return (
    <div style={{
      width: '100%',
      height: '100%',
      position: 'relative',
      background: data.error ? 'rgba(217,4,41,0.04)' : 'rgba(250,250,250,0.6)',
      border: `1px solid ${selected ? 'var(--ink)' : (data.error ? 'var(--red)' : 'var(--gray-200)')}`,
      borderRadius: 3,
    }}>
      <NodeResizer
        minWidth={170}
        minHeight={90}
        isVisible={selected}
        lineStyle={{ borderColor: 'var(--red)' }}
        handleStyle={{ width: 8, height: 8, background: 'var(--red)', border: 'none' }}
      />
      {/* Target handle so the cluster can be dropped on the whole service
          (claims its default DB), not just an individual database. */}
      <Handle type="target" position={Position.Top} style={{ background: 'var(--gray-500)', width: 9, height: 9, border: 'none' }} />
      <div className="svc-drag-handle" style={{ padding: '5px 9px', userSelect: 'none', display: 'flex', alignItems: 'flex-start', gap: 7, cursor: 'grab' }}>
        <span
          title={data.error ? 'unreachable' : 'live · reachable'}
          style={{ width: 8, height: 8, borderRadius: '50%', marginTop: 3, flexShrink: 0, background: data.error ? 'var(--red)' : 'var(--green)' }}
        />
        <div>
          <div style={{ fontWeight: 700, fontSize: 12, color: data.error ? 'var(--red)' : 'var(--ink)' }}>{data.connName}</div>
          <div style={{ fontFamily: 'var(--mono)', fontSize: 9, color: 'var(--gray-500)' }}>
            {data.baseUrl.replace(/^https?:\/\//, '')}{data.error ? ' · unreachable' : ' · live'}
          </div>
        </div>
      </div>
    </div>
  );
}

export interface BuilderDBNodeData extends Record<string, unknown> {
  dbName: string;
  role: 'leader' | 'follower' | 'none' | 'unknown';
  isDefault: boolean;
  claimed: boolean;
  leaderPort: number | null;
}

export function BuilderDBNode({ data, selected }: NodeProps<Node<BuilderDBNodeData>>) {
  const claimed = data.claimed;
  const roleAccent = data.role === 'leader' ? 'var(--red)'
    : data.role === 'follower' ? '#3b82f6'
    : 'var(--gray-200)';
  return (
    <div style={{
      background: 'white',
      border: `1px solid ${selected ? 'var(--ink)' : (claimed ? 'var(--red)' : 'var(--gray-200)')}`,
      borderLeft: `4px solid ${claimed ? 'var(--red)' : roleAccent}`,
      borderRadius: 2,
      padding: '7px 11px',
      minWidth: 150,
      boxShadow: claimed ? '0 2px 10px rgba(217,4,41,0.15)' : '0 1px 2px rgba(0,0,0,0.04)',
      fontFamily: 'var(--sans)',
      cursor: 'pointer',
    }}>
      <Handle type="target" position={Position.Top} style={{ background: claimed ? 'var(--red)' : roleAccent, width: 8, height: 8, border: 'none' }} />
      <Handle type="source" position={Position.Bottom} style={{ background: roleAccent, width: 8, height: 8, border: 'none' }} />

      <div style={{ display: 'flex', alignItems: 'center', gap: 5 }}>
        <span style={{ fontWeight: 700, fontSize: 12, flex: 1 }}>{data.dbName}</span>
        {data.isDefault && <span className="pill gray" style={{ fontSize: 8, padding: '0 5px' }}>def</span>}
        {claimed && <span className="pill red" style={{ fontSize: 8, padding: '1px 5px' }}>SHARD</span>}
      </div>
      <div style={{ marginTop: 3 }}>
        {data.role === 'leader' && (
          <span className="pill red" style={{ fontSize: 8, padding: '0 5px' }}>
            LEADER{data.leaderPort != null ? ` :${data.leaderPort}` : ''}
          </span>
        )}
        {data.role === 'follower' && (
          <span className="pill" style={{ background: '#dbeafe', color: '#1d4ed8', fontSize: 8, padding: '0 5px' }}>FOLLOWER</span>
        )}
        {data.role === 'none' && (
          <span className="pill gray" style={{ fontSize: 8, padding: '0 5px' }}>standalone</span>
        )}
      </div>
    </div>
  );
}
