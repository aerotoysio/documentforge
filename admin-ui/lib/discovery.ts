// Network discovery — walks the replication graph from a single seed node.
//
// Given one (baseUrl, apiKey), probes /replication/status and recursively
// follows peer endpoints (leader.followers[].httpEndpoint and
// follower.leader.httpEndpoint when populated, port-guessed otherwise).
//
// Returns one DiscoveredNode per unique baseUrl reached, plus an outline of
// the leader→follower edges for the topology view.
//
// What this can and can't do:
//   * Seed = leader  → finds the entire shard (leader + every follower).
//   * Seed = follower → port-guesses to reach the leader, then walks normally.
//   * Multi-shard cluster → only walks the seed's shard (other shards are
//     independent and don't know about each other). User seeds once per shard.

import { ReplicationStatus, getHealth, getReplicationStatus } from './api';

export type DiscoverySource =
  | 'seed'          // user provided this URL
  | 'peer'          // engine reported the httpEndpoint directly
  | 'port-guess';   // we guessed the HTTP port from a replication endpoint

export interface DiscoveredNode {
  baseUrl: string;                  // canonical, no trailing slash
  source: DiscoverySource;
  reachable: boolean;               // /health responded 200
  authOk: boolean;                  // /replication/status responded 200 (vs 401)
  status?: ReplicationStatus;       // populated when authOk
  errorMessage?: string;            // when !reachable or !authOk, what went wrong
  shardLeaderUrl?: string;          // for grouping in the topology view
  rttMs?: number;
}

export interface DiscoveryResult {
  seedUrl: string;
  nodes: DiscoveredNode[];
  // Edges: [leaderBaseUrl, followerBaseUrl]. Useful for rendering connector
  // lines in the topology view without re-walking.
  edges: Array<[string, string]>;
}

// ---------- entry point ----------

export async function discoverNetwork(seedUrl: string, apiKey: string | undefined): Promise<DiscoveryResult> {
  const seed = normalizeUrl(seedUrl);
  const seedPort = portFromUrl(seed);
  const seedScheme = schemeFromUrl(seed);

  const visited = new Map<string, DiscoveredNode>();
  const edges: Array<[string, string]> = [];
  const queue: Array<{ url: string; source: DiscoverySource }> = [{ url: seed, source: 'seed' }];

  while (queue.length > 0) {
    const { url, source } = queue.shift()!;
    if (visited.has(url)) continue;

    const node = await probe(url, apiKey, source);
    visited.set(url, node);

    if (!node.authOk || !node.status) continue;
    const status = node.status;

    // Walk to followers.
    if (status.role === 'leader' && status.leader?.followers) {
      for (const f of status.leader.followers) {
        const candidate = candidateUrlForPeer(f.httpEndpoint, f.endpoint, seedScheme, seedPort);
        if (!candidate) continue;
        const peerSource: DiscoverySource = f.httpEndpoint ? 'peer' : 'port-guess';
        if (!visited.has(candidate)) queue.push({ url: candidate, source: peerSource });
        edges.push([url, candidate]);
      }
    }

    // Walk to leader (port-guess: leader.httpEndpoint is null until the
    // bidirectional handshake ships — engine deferred it from #51).
    if (status.role === 'follower' && status.follower?.leader) {
      const lead = status.follower.leader;
      const candidate = candidateUrlForPeer(lead.httpEndpoint, lead.endpoint, seedScheme, seedPort);
      if (candidate) {
        const peerSource: DiscoverySource = lead.httpEndpoint ? 'peer' : 'port-guess';
        if (!visited.has(candidate)) queue.push({ url: candidate, source: peerSource });
        edges.push([candidate, url]);
      }
    }
  }

  // Compute shardLeaderUrl per node so the topology view doesn't need to
  // re-walk. Anything we can't trace back to a leader is left undefined and
  // falls back to the manual `shard` field.
  const leaderByUrl = new Map<string, string>();
  for (const [leaderUrl, followerUrl] of edges) {
    leaderByUrl.set(followerUrl, leaderUrl);
  }
  for (const [url, node] of visited) {
    if (node.status?.role === 'leader') node.shardLeaderUrl = url;
    else if (leaderByUrl.has(url)) node.shardLeaderUrl = leaderByUrl.get(url);
  }

  return { seedUrl: seed, nodes: Array.from(visited.values()), edges };
}

// ---------- single-node probe ----------

async function probe(baseUrl: string, apiKey: string | undefined, source: DiscoverySource): Promise<DiscoveredNode> {
  const conn = { id: '', name: '', baseUrl, apiKey, createdAt: '' };
  const t0 = performance.now();
  let reachable = false;
  let authOk = false;
  let status: ReplicationStatus | undefined;
  let errorMessage: string | undefined;

  try {
    await getHealth({ connection: conn });
    reachable = true;
  } catch (e: any) {
    errorMessage = e?.message || String(e);
    return { baseUrl, source, reachable: false, authOk: false, errorMessage, rttMs: Math.round(performance.now() - t0) };
  }

  try {
    status = await getReplicationStatus({ connection: conn });
    authOk = true;
  } catch (e: any) {
    const msg = e?.message || String(e);
    errorMessage = msg;
    authOk = !msg.startsWith('UNAUTHORIZED');
  }

  return {
    baseUrl,
    source,
    reachable,
    authOk,
    status,
    errorMessage,
    rttMs: Math.round(performance.now() - t0),
  };
}

// ---------- url helpers ----------

export function normalizeUrl(u: string): string {
  return u.trim().replace(/\/+$/, '');
}

function schemeFromUrl(u: string): string {
  const m = /^(https?):/i.exec(u);
  return m ? m[1].toLowerCase() : 'http';
}

function portFromUrl(u: string): number | null {
  try {
    const parsed = new URL(u);
    if (parsed.port) return parseInt(parsed.port, 10);
    return parsed.protocol === 'https:' ? 443 : 80;
  } catch {
    return null;
  }
}

function hostFromReplicationEndpoint(endpoint: string): string | null {
  // Replication endpoints are "host:port". IPv6 form is "[::1]:5500".
  if (endpoint.startsWith('[')) {
    const close = endpoint.indexOf(']');
    if (close === -1) return null;
    return endpoint.slice(0, close + 1);
  }
  const colon = endpoint.lastIndexOf(':');
  if (colon === -1) return null;
  return endpoint.slice(0, colon);
}

/**
 * Pick the best candidate URL for a peer:
 *   1. If the engine reported a real httpEndpoint (post-#51 follower), use it.
 *   2. Otherwise port-guess: take the host from the replication endpoint and
 *      pair it with the seed's HTTP scheme + port. Works for default
 *      deployments; documented as a fallback users can correct manually.
 */
function candidateUrlForPeer(
  httpEndpoint: string | null,
  replicationEndpoint: string,
  seedScheme: string,
  seedPort: number | null,
): string | null {
  if (httpEndpoint) return normalizeUrl(httpEndpoint);
  const host = hostFromReplicationEndpoint(replicationEndpoint);
  if (!host || seedPort === null) return null;
  const portSegment = (seedScheme === 'http' && seedPort === 80) || (seedScheme === 'https' && seedPort === 443)
    ? '' : `:${seedPort}`;
  return `${seedScheme}://${host}${portSegment}`;
}
