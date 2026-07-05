using System.Text.Json;

namespace DocumentForge.Cli;

/// <summary>
/// Per-node configuration. Loaded in this priority order:
///   1. --config node.json
///   2. CLI flags (--port, --data-dir, --node-name, --api-key, --replication-secret, --bind-all,
///                 --insecure-dev-mode, --replication-role, --replication-port, --leader-host,
///                 --leader-port, --auto-failover-seconds)
///   3. Env vars (DFDB_PORT, DFDB_DATA_DIR, DFDB_NODE_NAME, DFDB_API_KEY, DFDB_REPLICATION_SECRET,
///                DFDB_INSECURE_DEV_MODE, DFDB_REPLICATION_ROLE, DFDB_REPLICATION_PORT,
///                DFDB_LEADER_HOST, DFDB_LEADER_PORT, DFDB_AUTO_FAILOVER_SECONDS)
///   4. Defaults (single-node localhost:4300 with CWD/data, no replication)
///
/// Issue #110 — 4300 is DocumentForge's standard, documented default port
/// (the way 1433 is SQL Server, 5432 Postgres, 27017 Mongo). It sits below the
/// Linux ephemeral range and doesn't collide with common dev servers on :5000
/// (Flask, ASP.NET dev, macOS AirPlay). Always overridable via --port.
/// </summary>
public sealed class NodeConfig
{
    public string NodeName { get; set; } = "node-1";

    /// <summary>Standard DocumentForge service port (issue #110). Override with --port.</summary>
    public const int DefaultPort = 4300;

    public int Port { get; set; } = DefaultPort;
    public string DataDir { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), "data");
    public bool BindAllInterfaces { get; set; } = false;

    /// <summary>
    /// Issue #101 — explicit opt-in to run WITHOUT any authentication.
    /// Off by default: a node with no admin key, no scoped keys and no
    /// persisted keys refuses to start unless this is set. Only honoured
    /// on loopback; combining it with <see cref="BindAllInterfaces"/> is
    /// always a startup error.
    /// </summary>
    public bool InsecureDevMode { get; set; } = false;
    public SecurityConfig? Security { get; set; }
    public ReplicationConfig? Replication { get; set; }
    public NetworkConfig? Network { get; set; }

    /// <summary>
    /// The HTTP base URL by which other nodes (and the admin-UI) should
    /// reach this node's REST API. If <c>Network.PublicBaseUrl</c> is set,
    /// that wins; otherwise we derive it from <see cref="BindAllInterfaces"/>,
    /// the resolved scheme, and <see cref="Port"/>. Used in the replication
    /// handshake so peers learn each other's HTTP addresses without the
    /// admin-UI having to guess (issue #51).
    /// </summary>
    public string ResolveHttpEndpoint()
    {
        if (!string.IsNullOrEmpty(Network?.PublicBaseUrl))
            return Network.PublicBaseUrl.TrimEnd('/');
        var scheme = Security?.Tls is not null ? "https" : "http";
        var host = BindAllInterfaces ? Environment.MachineName : "localhost";
        return $"{scheme}://{host}:{Port}";
    }

    public static NodeConfig Load(string[] args)
    {
        var c = new NodeConfig();

        var configIdx = Array.IndexOf(args, "--config");
        if (configIdx >= 0 && configIdx + 1 < args.Length)
        {
            var path = args[configIdx + 1];
            var loaded = JsonSerializer.Deserialize<NodeConfig>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (loaded is not null) c = loaded;
        }

        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--node-name": c.NodeName = args[i + 1]; break;
                case "--port":      c.Port = int.Parse(args[i + 1]); break;
                case "--data-dir":  c.DataDir = args[i + 1]; break;
                case "--api-key":
                    c.Security ??= new SecurityConfig();
                    c.Security.ApiKey = args[i + 1];
                    break;
                case "--replication-secret":
                    c.Security ??= new SecurityConfig();
                    c.Security.ReplicationSecret = args[i + 1];
                    break;
                case "--replication-role":
                    c.Replication ??= new ReplicationConfig();
                    c.Replication.Role = args[i + 1];
                    break;
                case "--replication-port":
                    c.Replication ??= new ReplicationConfig();
                    c.Replication.Port = int.Parse(args[i + 1]);
                    break;
                case "--leader-host":
                    c.Replication ??= new ReplicationConfig();
                    c.Replication.LeaderHost = args[i + 1];
                    break;
                case "--leader-port":
                    c.Replication ??= new ReplicationConfig();
                    c.Replication.LeaderPort = int.Parse(args[i + 1]);
                    break;
                case "--min-sync-replicas":
                    c.Replication ??= new ReplicationConfig();
                    c.Replication.MinSyncReplicas = int.Parse(args[i + 1]);
                    break;
                case "--auto-failover-seconds":
                    c.Replication ??= new ReplicationConfig();
                    c.Replication.AutoFailover ??= new AutoFailoverConfig();
                    c.Replication.AutoFailover.SilenceSeconds = int.Parse(args[i + 1]);
                    break;
                case "--auto-failover-new-port":
                    c.Replication ??= new ReplicationConfig();
                    c.Replication.AutoFailover ??= new AutoFailoverConfig();
                    c.Replication.AutoFailover.NewLeaderPort = int.Parse(args[i + 1]);
                    break;
                case "--public-base-url":
                    c.Network ??= new NetworkConfig();
                    c.Network.PublicBaseUrl = args[i + 1];
                    break;
            }
        }
        if (args.Contains("--bind-all")) c.BindAllInterfaces = true;
        if (args.Contains("--insecure-dev-mode")) c.InsecureDevMode = true;

        var envName = Environment.GetEnvironmentVariable("DFDB_NODE_NAME");
        var envPort = Environment.GetEnvironmentVariable("DFDB_PORT");
        var envDir  = Environment.GetEnvironmentVariable("DFDB_DATA_DIR");
        var envKey  = Environment.GetEnvironmentVariable("DFDB_API_KEY");
        var envRep  = Environment.GetEnvironmentVariable("DFDB_REPLICATION_SECRET");
        if (c.NodeName == "node-1" && !string.IsNullOrEmpty(envName)) c.NodeName = envName;
        if (c.Port == DefaultPort && int.TryParse(envPort, out var p)) c.Port = p;
        if (c.DataDir.EndsWith("data") && !string.IsNullOrEmpty(envDir)) c.DataDir = envDir;
        if (!string.IsNullOrEmpty(envKey))
        {
            c.Security ??= new SecurityConfig();
            c.Security.ApiKey ??= envKey;
        }
        if (!string.IsNullOrEmpty(envRep))
        {
            c.Security ??= new SecurityConfig();
            c.Security.ReplicationSecret ??= envRep;
        }

        var envInsecure = Environment.GetEnvironmentVariable("DFDB_INSECURE_DEV_MODE");
        if (!string.IsNullOrEmpty(envInsecure) &&
            (envInsecure == "1" || envInsecure.Equals("true", StringComparison.OrdinalIgnoreCase)))
        {
            c.InsecureDevMode = true;
        }

        // Replication env vars
        var envRole     = Environment.GetEnvironmentVariable("DFDB_REPLICATION_ROLE");
        var envRepPort  = Environment.GetEnvironmentVariable("DFDB_REPLICATION_PORT");
        var envLdrHost  = Environment.GetEnvironmentVariable("DFDB_LEADER_HOST");
        var envLdrPort  = Environment.GetEnvironmentVariable("DFDB_LEADER_PORT");
        var envFoSec    = Environment.GetEnvironmentVariable("DFDB_AUTO_FAILOVER_SECONDS");
        if (!string.IsNullOrEmpty(envRole))
        {
            c.Replication ??= new ReplicationConfig();
            c.Replication.Role ??= envRole;
        }
        if (int.TryParse(envRepPort, out var rp))
        {
            c.Replication ??= new ReplicationConfig();
            if (c.Replication.Port is null) c.Replication.Port = rp;
        }
        if (!string.IsNullOrEmpty(envLdrHost))
        {
            c.Replication ??= new ReplicationConfig();
            c.Replication.LeaderHost ??= envLdrHost;
        }
        if (int.TryParse(envLdrPort, out var lp))
        {
            c.Replication ??= new ReplicationConfig();
            if (c.Replication.LeaderPort is null) c.Replication.LeaderPort = lp;
        }
        if (int.TryParse(envFoSec, out var fs))
        {
            c.Replication ??= new ReplicationConfig();
            c.Replication.AutoFailover ??= new AutoFailoverConfig();
            if (c.Replication.AutoFailover.SilenceSeconds is null) c.Replication.AutoFailover.SilenceSeconds = fs;
        }

        var envBaseUrl = Environment.GetEnvironmentVariable("DFDB_PUBLIC_BASE_URL");
        if (!string.IsNullOrEmpty(envBaseUrl))
        {
            c.Network ??= new NetworkConfig();
            c.Network.PublicBaseUrl ??= envBaseUrl;
        }

        return c;
    }
}

/// <summary>
/// Network-level config. Currently just <see cref="PublicBaseUrl"/>, the
/// HTTP base URL this node advertises to peers during the replication
/// handshake (issue #51). Override when the node sits behind a reverse
/// proxy (e.g. <c>https://dfdb.example.com</c>) — otherwise we derive it
/// from the bind address + port.
/// </summary>
public sealed class NetworkConfig
{
    /// <summary>
    /// HTTP base URL this node is reachable at, e.g. <c>http://10.0.0.5:4300</c>
    /// or <c>https://dfdb.example.com</c>. Trailing slash is normalized away.
    /// Sent on the replication handshake so peers + the admin-UI can
    /// auto-discover the topology without guessing port/scheme.
    /// </summary>
    public string? PublicBaseUrl { get; set; }
}

public sealed class SecurityConfig
{
    /// <summary>Admin (god) key — full access to every database + admin
    /// operations (attach/detach DBs, spawn services, etc.). Kept as the
    /// dev-mode default; production deployments should prefer scoped
    /// keys (see <see cref="ScopedKeys"/>) and reserve this for an
    /// emergency-access path.</summary>
    public string? ApiKey { get; set; }
    public string? ReplicationSecret { get; set; }
    public TlsConfig? Tls { get; set; }

    /// <summary>Issue #72 — scoped API keys. Each entry's key grants
    /// access only to the databases its scopes name. Coexists with
    /// <see cref="ApiKey"/>: the admin key always works; scoped keys
    /// fill in the multi-tenant gap.</summary>
    public List<ScopedApiKey>? ScopedKeys { get; set; }
}

/// <summary>
/// A bearer token that's restricted to one or more databases + an
/// access level. Scope grammar (each entry in <see cref="Scopes"/>):
///   <list type="bullet">
///     <item><c>*</c>  — admin (everything, equivalent to ApiKey)</item>
///     <item><c>db:*</c> — full data-plane access to every DB (no admin)</item>
///     <item><c>db:foo</c> — read+write on database "foo"</item>
///     <item><c>db:foo:read</c> — read-only on "foo"</item>
///     <item><c>db:foo:write</c> — same as db:foo (explicit form)</item>
///   </list>
/// </summary>
public sealed class ScopedApiKey
{
    public string Key { get; set; } = "";
    /// <summary>Scopes this key carries. AT LEAST ONE required.</summary>
    public List<string> Scopes { get; set; } = new();
    /// <summary>Free-form note for operators ("staging tenant-a",
    /// "metrics reader") — never used by enforcement.</summary>
    public string? Description { get; set; }
}

public sealed class TlsConfig
{
    public string CertPath { get; set; } = "";
    public string? CertPassword { get; set; }

    public string? ResolveCertPassword()
    {
        if (string.IsNullOrEmpty(CertPassword)) return null;
        if (CertPassword.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
            return Environment.GetEnvironmentVariable(CertPassword.Substring(4));
        return CertPassword;
    }
}

/// <summary>
/// Replication wiring for a node. Omit the block entirely for a single-node setup.
/// Set <see cref="Role"/> to "leader" or "follower" to stand up replication automatically
/// when <c>dfdb serve</c> starts.
/// </summary>
public sealed class ReplicationConfig
{
    /// <summary>"leader" or "follower" (case-insensitive). null = replication disabled.</summary>
    public string? Role { get; set; }

    /// <summary>
    /// For role=leader: the TCP port the replication listener binds to.
    /// Must differ from the HTTP <see cref="NodeConfig.Port"/>. Defaults to 5500 if unset.
    /// </summary>
    public int? Port { get; set; }

    /// <summary>For role=follower: hostname of the leader.</summary>
    public string? LeaderHost { get; set; }

    /// <summary>For role=follower: replication port on the leader.</summary>
    public int? LeaderPort { get; set; }

    /// <summary>
    /// Issue #95 — semi-sync replication. For role=leader: how many followers
    /// must ACK a write before the leader acknowledges it to the client.
    /// 0 (default) = asynchronous fire-and-forget; 1 = wait for one replica
    /// (bounded RPO). A write that can't reach the quorum within
    /// <see cref="SyncTimeoutSeconds"/> fails with a replication-timeout error.
    /// </summary>
    public int MinSyncReplicas { get; set; }

    /// <summary>Timeout (seconds) for the semi-sync ack quorum. Default 5.</summary>
    public double SyncTimeoutSeconds { get; set; } = 5;

    /// <summary>Optional auto-failover (only honored when role=follower).</summary>
    public AutoFailoverConfig? AutoFailover { get; set; }

    public string NormalizedRole => (Role ?? "").Trim().ToLowerInvariant();
    public bool IsLeader   => NormalizedRole == "leader";
    public bool IsFollower => NormalizedRole == "follower";
}

public sealed class AutoFailoverConfig
{
    /// <summary>Silence threshold. If no heartbeat arrives for this many seconds, the follower promotes.</summary>
    public int? SilenceSeconds { get; set; }

    /// <summary>
    /// Which port this node should bind as a leader once promoted.
    /// Defaults to <see cref="ReplicationConfig.LeaderPort"/> (i.e. take over the old leader's port).
    /// </summary>
    public int? NewLeaderPort { get; set; }
}
