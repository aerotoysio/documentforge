using System.Text.Json;
using System.Text.Json.Serialization;
using DocumentForge.Studio.Core.Connections;

namespace DocumentForge.Studio.Core.Settings;

/// <summary>Everything a settings export contains, in plain text — including
/// decrypted secrets, so the file is portable across machines/profiles.
/// The UI must warn before writing one of these to disk.</summary>
public sealed class SettingsBundle
{
    public int FormatVersion { get; set; } = 1;
    public DateTime ExportedAtUtc { get; set; }
    public StudioSettings Settings { get; set; } = new();
    public List<ConnectionDescriptor> Connections { get; set; } = new();
    public Dictionary<string, string> Secrets { get; set; } = new();
}

/// <summary>
/// The on-disk home of DocumentForge Studio: settings.json, connections.json
/// and the DPAPI secret store, rooted at %AppData%\DocumentForge Studio by
/// default (injectable for tests). Also the factory for live
/// <see cref="IDfConnection"/> instances, since opening one needs the secret
/// store to resolve API keys.
/// </summary>
public sealed class StudioWorkspace
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _settingsPath;
    private readonly string _connectionsPath;

    public StudioWorkspace(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DocumentForge Studio");
        Directory.CreateDirectory(RootDirectory);

        _settingsPath = Path.Combine(RootDirectory, "settings.json");
        _connectionsPath = Path.Combine(RootDirectory, "connections.json");

        Settings = Load<StudioSettings>(_settingsPath) ?? new StudioSettings();
        Connections = Load<List<ConnectionDescriptor>>(_connectionsPath) ?? new List<ConnectionDescriptor>();
        Secrets = new SecretStore(RootDirectory);
    }

    public string RootDirectory { get; }
    public StudioSettings Settings { get; }
    public List<ConnectionDescriptor> Connections { get; }
    public SecretStore Secrets { get; }

    public void SaveSettings() => AtomicFile.Write(_settingsPath, JsonSerializer.Serialize(Settings, Json));
    public void SaveConnections() => AtomicFile.Write(_connectionsPath, JsonSerializer.Serialize(Connections, Json));

    /// <summary>Adds or updates a saved connection. A non-null apiKey is
    /// pushed into the secret store and referenced by id; null leaves any
    /// existing key untouched.</summary>
    public void UpsertConnection(ConnectionDescriptor descriptor, string? apiKey = null)
    {
        if (apiKey is not null)
            descriptor.ApiKeySecretId = Secrets.Set(descriptor.ApiKeySecretId, apiKey);

        var existing = Connections.FindIndex(c => c.Id == descriptor.Id);
        if (existing >= 0) Connections[existing] = descriptor;
        else Connections.Add(descriptor);
        SaveConnections();
    }

    public void RemoveConnection(string id)
    {
        var descriptor = Connections.Find(c => c.Id == id);
        if (descriptor is null) return;
        if (descriptor.ApiKeySecretId is { } secretId)
            Secrets.Delete(secretId);
        Connections.Remove(descriptor);
        SaveConnections();
    }

    /// <summary>Builds a live (not yet opened) connection for a descriptor,
    /// resolving its API key from the secret store.</summary>
    public IDfConnection CreateConnection(ConnectionDescriptor descriptor) =>
        ConnectionFactory.Create(
            descriptor,
            descriptor.ApiKeySecretId is { } id ? Secrets.TryGet(id) : null);

    public void ExportBundle(string filePath)
    {
        var bundle = new SettingsBundle
        {
            ExportedAtUtc = DateTime.UtcNow,
            Settings = Settings,
            Connections = Connections,
            Secrets = Secrets.ExportPlaintext(),
        };
        AtomicFile.Write(filePath, JsonSerializer.Serialize(bundle, Json));
    }

    /// <summary>Merge keeps existing connections and overwrites on id match;
    /// replace wipes connections and secrets first. Settings always come
    /// from the bundle.</summary>
    public void ImportBundle(string filePath, bool replace)
    {
        var bundle = JsonSerializer.Deserialize<SettingsBundle>(File.ReadAllText(filePath), Json)
                     ?? throw new InvalidDataException($"'{filePath}' is not a Studio settings bundle.");
        if (bundle.FormatVersion > 1)
            throw new InvalidDataException(
                $"Bundle format v{bundle.FormatVersion} is newer than this Studio understands (v1). Update Studio first.");

        if (replace) Connections.Clear();
        foreach (var incoming in bundle.Connections)
        {
            var existing = Connections.FindIndex(c => c.Id == incoming.Id);
            if (existing >= 0) Connections[existing] = incoming;
            else Connections.Add(incoming);
        }

        Secrets.ImportPlaintext(bundle.Secrets, replaceAll: replace);

        Settings.DefaultDataDirectory = bundle.Settings.DefaultDataDirectory;
        Settings.DefaultQueryLimit = bundle.Settings.DefaultQueryLimit;

        SaveSettings();
        SaveConnections();
    }

    private static T? Load<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json);
        }
        catch (JsonException)
        {
            // A corrupt settings file shouldn't brick the app; keep the bad
            // file for inspection and start fresh.
            File.Copy(path, path + ".corrupt", overwrite: true);
            return null;
        }
    }
}
