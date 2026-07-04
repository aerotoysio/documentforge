using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocumentForge.Studio.Core.Connections;
using DocumentForge.Studio.Services;
using DocumentForge.Studio.Core.Settings;

namespace DocumentForge.Studio.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly StudioWorkspace _workspace;
    private readonly IDialogService _dialogs;

    public MainViewModel(StudioWorkspace workspace, IDialogService dialogs)
    {
        _workspace = workspace;
        _dialogs = dialogs;
    }

    public ObservableCollection<ServerNodeViewModel> Servers { get; } = new();

    [ObservableProperty]
    private string _statusText = "Ready";

    public string VersionText =>
        $"DocumentForge Studio {typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "dev"}";

    [RelayCommand]
    private async Task ConnectAsync()
    {
        var request = _dialogs.ShowConnectDialog();
        if (request is null) return;
        await OpenConnectionAsync(request.Descriptor, request.ApiKey, request.Save);
    }

    public async Task OpenConnectionAsync(ConnectionDescriptor descriptor, string? apiKey, bool save)
    {
        if (Servers.Any(s => s.Connection.Descriptor.Id == descriptor.Id))
        {
            _dialogs.ShowInfo("Already connected", $"'{descriptor.Name}' is already open in Object Explorer.");
            return;
        }

        IDfConnection? connection = null;
        try
        {
            StatusText = $"Connecting to {descriptor.Target}…";
            if (save)
            {
                _workspace.UpsertConnection(descriptor, apiKey);
                connection = _workspace.CreateConnection(descriptor);
            }
            else
            {
                connection = ConnectionFactory.Create(descriptor, apiKey);
            }

            await connection.ConnectAsync();

            descriptor.LastConnectedUtc = DateTime.UtcNow;
            if (save) _workspace.SaveConnections();

            var node = new ServerNodeViewModel(this, connection);
            Servers.Add(node);
            node.IsExpanded = true;
            StatusText = $"Connected to {descriptor.Name}";
        }
        catch (Exception ex)
        {
            if (connection is not null) await connection.DisposeAsync();
            StatusText = "Connect failed";
            _dialogs.ShowError("Connect failed", ex.Message);
        }
    }

    public async Task DisconnectAsync(ServerNodeViewModel server)
    {
        Servers.Remove(server);
        await server.Connection.DisposeAsync();
        StatusText = $"Disconnected from {server.Connection.Descriptor.Name}";
    }

    [RelayCommand]
    private async Task NewDatabaseAsync()
    {
        var eligibleServers = Servers
            .Where(s => s.Connection.Capabilities.HasFlag(ConnectionCapabilities.CreateDatabase))
            .ToList();
        var request = _dialogs.ShowNewDatabaseDialog(eligibleServers, _workspace.Settings.DefaultDataDirectory);
        if (request is null) return;

        try
        {
            if (request.Server is null)
            {
                var path = Path.Combine(_workspace.Settings.DefaultDataDirectory, request.Name + ".dfdb");
                if (File.Exists(path))
                    throw new InvalidOperationException($"'{path}' already exists.");
                DirectFileConnection.CreateDatabaseFile(path);
                var descriptor = new ConnectionDescriptor
                {
                    Name = request.Name,
                    Kind = ConnectionKind.File,
                    FilePath = path,
                };
                await OpenConnectionAsync(descriptor, apiKey: null, save: true);
            }
            else
            {
                await request.Server.Connection.CreateDatabaseAsync(request.Name);
                await request.Server.RefreshAsync();
            }
            StatusText = $"Database '{request.Name}' created";
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Create database failed", ex.Message);
        }
    }

    public async Task DropDatabaseAsync(DatabaseNodeViewModel database)
    {
        var choice = _dialogs.ConfirmDropDatabase(database.Info.Name);
        if (choice == DropChoice.Cancel) return;

        try
        {
            await database.Server.Connection.DropDatabaseAsync(database.Info.Name, deleteFiles: choice == DropChoice.Drop);
            await database.Server.RefreshAsync();
            StatusText = choice == DropChoice.Drop
                ? $"Database '{database.Info.Name}' dropped"
                : $"Database '{database.Info.Name}' detached";
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Drop database failed", ex.Message);
        }
    }

    public async Task ShowDatabasePropertiesAsync(DatabaseNodeViewModel database)
    {
        try
        {
            var stats = await database.Server.Connection.GetStatsAsync(database.Info.Name);
            var lines = new List<string>
            {
                $"Database: {database.Info.Name}",
                $"File: {database.Info.FilePath ?? "(server-side)"}",
                $"Size: {stats.FileSize / 1024.0 / 1024.0:F2} MB ({stats.FileSize:N0} bytes)",
                $"Pages: {stats.PageCount:N0}  (cached {stats.CachedPages:N0}, dirty {stats.DirtyPages:N0})",
                "",
                $"Collections ({stats.Collections.Count}):",
            };
            lines.AddRange(stats.Collections.Select(c =>
                $"  {c.Name}: {c.DocumentCount:N0} documents, {c.IndexCount} index(es)"));
            _dialogs.ShowInfo($"Properties — {database.Info.Name}", string.Join(Environment.NewLine, lines));
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Properties failed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task RefreshAllAsync()
    {
        foreach (var server in Servers.ToList())
            await server.RefreshAsync();
        StatusText = "Refreshed";
    }

    [RelayCommand]
    private void ExportSettings()
    {
        if (!_dialogs.Confirm(
                "Export settings",
                "The export bundle contains your connections AND their API keys in PLAIN TEXT so it can move to another machine.\n\n" +
                "Store it somewhere safe. Continue?"))
            return;

        var path = _dialogs.PickSaveFile(
            "Studio settings bundle (*.dfstudio.json)|*.dfstudio.json|All files (*.*)|*.*",
            "documentforge-studio-settings.dfstudio.json");
        if (path is null) return;

        try
        {
            _workspace.ExportBundle(path);
            StatusText = $"Settings exported to {path}";
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Export failed", ex.Message);
        }
    }

    [RelayCommand]
    private void ImportSettings()
    {
        var path = _dialogs.PickOpenFile(
            "Studio settings bundle (*.dfstudio.json)|*.dfstudio.json|All files (*.*)|*.*");
        if (path is null) return;

        var replace = _dialogs.Confirm(
            "Import settings",
            "Replace ALL existing connections and secrets with the bundle's contents?\n\n" +
            "Yes — replace everything.\nNo — merge (bundle entries win on conflict).");
        try
        {
            _workspace.ImportBundle(path, replace);
            StatusText = $"Settings imported from {path}";
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Import failed", ex.Message);
        }
    }

    [RelayCommand]
    private void About() => _dialogs.ShowInfo(
        "About DocumentForge Studio",
        $"{VersionText}\n\nSSMS-style management for DocumentForge — the SQL-queryable JSON document database.\n" +
        "https://github.com/aerotoysio/documentforge");

    [RelayCommand]
    private void Exit() => System.Windows.Application.Current.Shutdown();

    public void ComingSoon(string message) => _dialogs.ShowInfo("Coming soon", message);

    public async Task ShutdownAsync()
    {
        foreach (var server in Servers.ToList())
            await server.Connection.DisposeAsync();
        Servers.Clear();
    }
}
