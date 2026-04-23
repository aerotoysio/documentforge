using System.Text.Json;

namespace DocumentForge.Cli;

/// <summary>
/// Per-node configuration. Loaded in this priority order:
///   1. --config node.json
///   2. CLI flags (--port, --data-dir, --node-name, --api-key, --replication-secret, --bind-all,
///                 --replication-role, --replication-port, --leader-host, --leader-port,
///                 --auto-failover-seconds)
///   3. Env vars (DFDB_PORT, DFDB_DATA_DIR, DFDB_NODE_NAME, DFDB_API_KEY, DFDB_REPLICATION_SECRET,
///                DFDB_REPLICATION_ROLE, DFDB_REPLICATION_PORT, DFDB_LEADER_HOST, DFDB_LEADER_PORT,
///                DFDB_AUTO_FAILOVER_SECONDS)
///   4. Defaults (single-node localhost:5000 with CWD/data, no replication)
/// </summary>
public sealed class NodeConfig
{
    public string NodeName { get; set; } = "node-1";
    public int Port { get; set; } = 5000;
    public string DataDir { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), "data");
    public bool BindAllInterfaces { get; set; } = false;
    public SecurityConfig? Security { get; set; }
    public ReplicationConfig? Replication { get; set; }

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
            }
        }
        if (args.Contains("--bind-all")) c.BindAllInterfaces = true;

        var envName = Environment.GetEnvironmentVariable("DFDB_NODE_NAME");
        var envPort = Environment.GetEnvironmentVariable("DFDB_PORT");
        var envDir  = Environment.GetEnvironmentVariable("DFDB_DATA_DIR");
        var envKey  = Environment.GetEnvironmentVariable("DFDB_API_KEY");
        var envRep  = Environment.GetEnvironmentVariable("DFDB_REPLICATION_SECRET");
        if (c.NodeName == "node-1" && !string.IsNullOrEmpty(envName)) c.NodeName = envName;
        if (c.Port == 5000 && int.TryParse(envPort, out var p)) c.Port = p;
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

        return c;
    }
}

public sealed class SecurityConfig
{
    public string? ApiKey { get; set; }
    public string? ReplicationSecret { get; set; }
    public TlsConfig? Tls { get; set; }
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
