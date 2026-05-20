'use client';

// Topology = the unified cluster canvas (Issue #66 Phase 6c). The earlier
// Graph / List / Across-services / Collections tabs were folded into this one
// canvas, so the page is now just a thin host around ClusterBuilderView.

import { useConnections } from '@/lib/connections-context';
import { ClusterBuilderView } from './cluster-builder';

export default function TopologyPage() {
  const { connections } = useConnections();
  return (
    <>
      <div className="eyebrow">Topology</div>
      <h1 className="page-title" style={{ marginBottom: 16 }}>Cluster builder</h1>
      <ClusterBuilderView connections={[...connections]} />
    </>
  );
}
