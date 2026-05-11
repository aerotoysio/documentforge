// Custom react-flow node renderer for an attached DocumentForge database.
// Carries enough state to render role-coloured chrome, follower count,
// active-default indicator, and the read-only badge when the engine has
// flipped a follower into replica-mode.

import { Handle, Position, type NodeProps, type Node } from '@xyflow/react';

export interface DBNodeData extends Record<string, unknown> {
  name: string;
  filePath: string;
  isDefault: boolean;
  role: 'leader' | 'follower' | 'none' | 'unknown';
  leaderPort: number | null;
  followerCount: number;
  followerLeaderEndpoint: string | null;
  readOnly: boolean;
}

export function DBNode({ data, selected }: NodeProps<Node<DBNodeData>>) {
  const accent = data.role === 'leader' ? 'var(--red)'
    : data.role === 'follower' ? '#3b82f6'
    : 'var(--gray-200)';

  return (
    <div style={{
      background: 'white',
      border: `1px solid ${selected ? 'var(--red)' : 'var(--gray-200)'}`,
      borderLeft: `4px solid ${accent}`,
      outline: selected ? '2px solid rgba(217,4,41,0.18)' : 'none',
      outlineOffset: 1,
      borderRadius: 2,
      padding: '10px 14px',
      minWidth: 180,
      boxShadow: selected ? '0 4px 12px rgba(0,0,0,0.08)' : '0 1px 2px rgba(0,0,0,0.04)',
      fontFamily: 'var(--sans)',
      cursor: 'grab',
    }}>
      {/* Handles for incoming/outgoing edges. Source-on-bottom + target-on-top
          works for the natural "leader sits above followers" layout. */}
      <Handle type="target" position={Position.Top} style={{ background: accent, width: 6, height: 6, border: 'none' }} />
      <Handle type="source" position={Position.Bottom} style={{ background: accent, width: 6, height: 6, border: 'none' }} />

      <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
        <span style={{ fontWeight: 600, fontSize: 13, flex: 1 }}>{data.name}</span>
        {data.isDefault && (
          <span className="pill red" style={{ fontSize: 9, padding: '1px 6px' }}>ACTIVE</span>
        )}
      </div>

      <div style={{ marginTop: 4, display: 'flex', alignItems: 'center', gap: 4 }}>
        {data.role === 'leader' && (
          <>
            <span className="pill red" style={{ fontSize: 9, padding: '1px 6px' }}>LEADER</span>
            {data.leaderPort != null && (
              <span style={{ fontFamily: 'var(--mono)', fontSize: 9, color: 'var(--gray-500)' }}>:{data.leaderPort}</span>
            )}
          </>
        )}
        {data.role === 'follower' && (
          <span className="pill" style={{ background: '#dbeafe', color: '#1d4ed8', fontSize: 9, padding: '1px 6px' }}>FOLLOWER</span>
        )}
        {data.role === 'none' && (
          <span className="pill gray" style={{ fontSize: 9, padding: '1px 6px' }}>STANDALONE</span>
        )}
        {data.role === 'unknown' && (
          <span className="pill gray" style={{ fontSize: 9, padding: '1px 6px', color: 'var(--gray-500)' }}>—</span>
        )}
        {data.readOnly && (
          <span className="pill red" style={{ fontSize: 9, padding: '1px 6px' }}>RO</span>
        )}
      </div>

      {/* Tiny stats footer — keep it sparse, the details panel has the rest. */}
      {data.role === 'leader' && data.followerCount > 0 && (
        <div style={{ marginTop: 6, fontSize: 10, color: 'var(--gray-500)', fontFamily: 'var(--mono)' }}>
          {data.followerCount} follower{data.followerCount === 1 ? '' : 's'}
        </div>
      )}
      {data.role === 'follower' && data.followerLeaderEndpoint && (
        <div style={{ marginTop: 6, fontSize: 10, color: 'var(--gray-500)', fontFamily: 'var(--mono)' }}>
          ← {data.followerLeaderEndpoint}
        </div>
      )}
    </div>
  );
}
