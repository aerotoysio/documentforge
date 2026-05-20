import { redirect } from 'next/navigation';

// The legacy form-based cluster.json builder emitted an outdated PascalCase
// format ({ Version, Shards, Collections: {...} }) that the router's
// ClusterConfig.FromJson can't load. The visual drag-and-drop builder
// replaces it as a tab on /topology (Issue #66 Phase 6c).
export default function ClusterPage() {
  redirect('/topology');
}
