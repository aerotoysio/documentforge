namespace DocumentForge.Engine.Cluster;

/// <summary>
/// A consistent hash ring used to map shard keys to shards.
///
/// Why this matters: a naive hash(key) % shardCount re-routes ~75% of your data when
/// you go from 3 shards to 4. A consistent hash ring maps each shard to many points on
/// a 2^32 ring; each key lands on the next point clockwise. Adding a shard only moves
/// ~1/N of the keys (the ones nearest the new shard's ring positions).
///
/// We use "virtual nodes" (default 150 per shard) for even distribution.
/// </summary>
public sealed class ConsistentHashRing
{
    // Sorted array of (ringPosition, shardIndex) pairs
    private readonly uint[] _positions;
    private readonly int[] _shardIndexForPosition;

    public int ShardCount { get; }

    public ConsistentHashRing(IReadOnlyList<string> shardNames, int virtualNodesPerShard)
    {
        ShardCount = shardNames.Count;
        var pairs = new List<(uint Pos, int ShardIdx)>(shardNames.Count * virtualNodesPerShard);

        for (int i = 0; i < shardNames.Count; i++)
        {
            for (int v = 0; v < virtualNodesPerShard; v++)
            {
                // Hash "shardname#vnodeIndex" - deterministic across all nodes
                var key = $"{shardNames[i]}#{v}";
                pairs.Add((Fnv1a(key), i));
            }
        }

        pairs.Sort((a, b) => a.Pos.CompareTo(b.Pos));
        _positions = pairs.Select(p => p.Pos).ToArray();
        _shardIndexForPosition = pairs.Select(p => p.ShardIdx).ToArray();
    }

    /// <summary>
    /// Given a key value (anything ToString-able), return the shard index it routes to.
    /// </summary>
    public int PickShardIndex(string keyValue)
    {
        if (_positions.Length == 0)
            throw new InvalidOperationException("Ring is empty.");

        var keyHash = Fnv1a(keyValue);
        // Find the first position >= keyHash (wrap around if past the end)
        int lo = 0, hi = _positions.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_positions[mid] < keyHash) lo = mid + 1;
            else hi = mid;
        }
        if (lo == _positions.Length) lo = 0; // wrap
        return _shardIndexForPosition[lo];
    }

    /// <summary>
    /// FNV-1a 32-bit hash. Deterministic, fast, good distribution for our purpose.
    /// </summary>
    public static uint Fnv1a(string s)
    {
        unchecked
        {
            uint h = 2166136261u;
            foreach (var c in s) h = (h ^ c) * 16777619u;
            return h;
        }
    }
}
