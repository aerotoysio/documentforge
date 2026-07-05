using DocumentForge.Studio.Core.Connections;
using DocumentForge.Studio.Core.Settings;

namespace DocumentForge.Studio.Core.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dfstudio-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void SecretStore_RoundTrips_And_Deletes()
    {
        var store = new SecretStore(_dir);
        var id = store.Set(null, "super-secret-key");

        Assert.Equal("super-secret-key", store.TryGet(id));

        // Values must be encrypted at rest, not plain text.
        var onDisk = File.ReadAllText(Path.Combine(_dir, "secrets.json"));
        Assert.DoesNotContain("super-secret-key", onDisk);

        // Survives a reload from disk.
        var reloaded = new SecretStore(_dir);
        Assert.Equal("super-secret-key", reloaded.TryGet(id));

        Assert.True(reloaded.Delete(id));
        Assert.Null(reloaded.TryGet(id));
    }

    [Fact]
    public void Workspace_Persists_Settings_And_Connections()
    {
        var workspace = new StudioWorkspace(_dir);
        workspace.Settings.DefaultDataDirectory = @"D:\elsewhere";
        workspace.SaveSettings();
        workspace.UpsertConnection(new ConnectionDescriptor
        {
            Name = "prod",
            Kind = ConnectionKind.Http,
            Url = "https://dfdb.example.com:5001",
        }, apiKey: "key-123");

        var reloaded = new StudioWorkspace(_dir);
        Assert.Equal(@"D:\elsewhere", reloaded.Settings.DefaultDataDirectory);
        var connection = Assert.Single(reloaded.Connections);
        Assert.Equal("prod", connection.Name);
        Assert.Equal(ConnectionKind.Http, connection.Kind);
        Assert.NotNull(connection.ApiKeySecretId);
        Assert.Equal("key-123", reloaded.Secrets.TryGet(connection.ApiKeySecretId!));

        // The key must never be readable in connections.json.
        var onDisk = File.ReadAllText(Path.Combine(_dir, "connections.json"));
        Assert.DoesNotContain("key-123", onDisk);
    }

    [Fact]
    public void RemoveConnection_Deletes_Its_Secret()
    {
        var workspace = new StudioWorkspace(_dir);
        var descriptor = new ConnectionDescriptor { Name = "x", Kind = ConnectionKind.Http, Url = "http://h:1" };
        workspace.UpsertConnection(descriptor, apiKey: "k");
        var secretId = descriptor.ApiKeySecretId!;

        workspace.RemoveConnection(descriptor.Id);

        Assert.Empty(workspace.Connections);
        Assert.Null(workspace.Secrets.TryGet(secretId));
    }

    [Fact]
    public void ExportImport_RoundTrips_Including_Secrets()
    {
        var source = new StudioWorkspace(Path.Combine(_dir, "source"));
        source.Settings.DefaultDataDirectory = @"C:\data\documentForge";
        source.UpsertConnection(new ConnectionDescriptor
        {
            Name = "local service",
            Kind = ConnectionKind.Http,
            Url = "http://localhost:5001",
        }, apiKey: "bundle-key");
        source.UpsertConnection(new ConnectionDescriptor
        {
            Name = "orders file",
            Kind = ConnectionKind.File,
            FilePath = @"C:\data\documentForge\orders.dfdb",
        });

        var bundlePath = Path.Combine(_dir, "bundle.dfstudio.json");
        source.ExportBundle(bundlePath);

        // The bundle is intentionally plain text — portable across machines.
        Assert.Contains("bundle-key", File.ReadAllText(bundlePath));

        var target = new StudioWorkspace(Path.Combine(_dir, "target"));
        target.ImportBundle(bundlePath, replace: true);

        Assert.Equal(2, target.Connections.Count);
        var http = target.Connections.Single(c => c.Kind == ConnectionKind.Http);
        Assert.Equal("bundle-key", target.Secrets.TryGet(http.ApiKeySecretId!));
    }

    [Fact]
    public void ImportBundle_Merge_Overwrites_On_Id_Match_And_Keeps_Others()
    {
        var workspace = new StudioWorkspace(_dir);
        var keep = new ConnectionDescriptor { Name = "keep", Kind = ConnectionKind.Http, Url = "http://a:1" };
        var overwrite = new ConnectionDescriptor { Name = "old-name", Kind = ConnectionKind.Http, Url = "http://b:2" };
        workspace.UpsertConnection(keep);
        workspace.UpsertConnection(overwrite);

        var other = new StudioWorkspace(Path.Combine(_dir, "other"));
        other.UpsertConnection(new ConnectionDescriptor
        {
            Id = overwrite.Id,
            Name = "new-name",
            Kind = ConnectionKind.Http,
            Url = "http://b:2",
        });
        var bundlePath = Path.Combine(_dir, "merge.json");
        other.ExportBundle(bundlePath);

        workspace.ImportBundle(bundlePath, replace: false);

        Assert.Equal(2, workspace.Connections.Count);
        Assert.Contains(workspace.Connections, c => c.Name == "keep");
        Assert.Contains(workspace.Connections, c => c.Name == "new-name");
        Assert.DoesNotContain(workspace.Connections, c => c.Name == "old-name");
    }

    [Fact]
    public void Corrupt_Settings_File_Falls_Back_To_Defaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ not json !!");

        var workspace = new StudioWorkspace(_dir);

        Assert.Equal(@"C:\data\documentForge", workspace.Settings.DefaultDataDirectory);
        Assert.True(File.Exists(Path.Combine(_dir, "settings.json.corrupt")));
    }
}
