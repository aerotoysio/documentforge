using System.Text.Json;

namespace DocumentForge.Cli;

/// <summary>
/// Per-node configuration. Loaded in this priority order:
///   1. --config node.json
///   2. CLI flags (--port, --data-dir, --node-name, --api-key, --replication-secret, --bind-all)
///   3. Env vars (DFDB_PORT, DFDB_DATA_DIR, DFDB_NODE_NAME, DFDB_API_KEY, DFDB_REPLICATION_SECRET)
///   4. Defaults (single-node localhost:5000 with CWD/data)
/// </summary>
public sealed class NodeConfig
{
    public string NodeName { get; set; } = "node-1";
    public int Port { get; set; } = 5000;
    public string DataDir { get; set; } = Path.Combine(Directory.GetCurrentDirectory(), "data");
    public bool BindAllInterfaces { get; set; } = false;
    public SecurityConfig? Security { get; set; }

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
