using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocumentForge.Studio.Core.Connections;
using DocumentForge.Studio.Core.Query;
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
        Documents.Add(new WelcomeDocumentViewModel());
        _activeDocument = Documents[0];
    }

    public ObservableCollection<ServerNodeViewModel> Servers { get; } = new();

    /// <summary>Tabs in the main document area (Welcome + query tabs).</summary>
    public ObservableCollection<DocumentViewModel> Documents { get; } = new();

    [ObservableProperty]
    private DocumentViewModel? _activeDocument;

    // Remembers the last query tab the user worked in, so the SQL Reference can
    // insert into it even while the reference tab itself is focused.
    private QueryDocumentViewModel? _lastActiveQuery;

    partial void OnActiveDocumentChanged(DocumentViewModel? value)
    {
        if (value is QueryDocumentViewModel q) _lastActiveQuery = q;
    }

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

    /// <summary>Opens a direct-file connection to a .dfdb path (used by the
    /// file association / "Open with"). Not saved — it's an ad-hoc open.</summary>
    public async Task OpenFileConnectionAsync(string filePath)
    {
        var descriptor = new ConnectionDescriptor
        {
            Name = System.IO.Path.GetFileNameWithoutExtension(filePath),
            Kind = ConnectionKind.File,
            FilePath = filePath,
        };
        await OpenConnectionAsync(descriptor, apiKey: null, save: false);
    }

    public async Task DisconnectAsync(ServerNodeViewModel server)
    {
        Servers.Remove(server);
        await server.Connection.DisposeAsync();
        StatusText = $"Disconnected from {server.Connection.Descriptor.Name}";
    }

    /// <summary>On startup, quietly reconnect saved connections so the Object
    /// Explorer isn't empty after a relaunch. Failures are non-fatal (a down
    /// service is skipped with a status note, no error dialog).</summary>
    public async Task ReconnectSavedAsync()
    {
        if (!_workspace.Settings.ReconnectOnStartup || _workspace.Connections.Count == 0) return;

        var ordered = _workspace.Connections.OrderByDescending(c => c.LastConnectedUtc).ToList();
        var reconnected = 0;
        var failed = 0;
        StatusText = "Reconnecting saved connections…";

        foreach (var descriptor in ordered)
        {
            if (Servers.Any(s => s.Connection.Descriptor.Id == descriptor.Id)) continue;
            IDfConnection? connection = null;
            try
            {
                connection = _workspace.CreateConnection(descriptor);
                await connection.ConnectAsync();
                var node = new ServerNodeViewModel(this, connection);
                Servers.Add(node);
                node.IsExpanded = true;
                reconnected++;
            }
            catch
            {
                if (connection is not null) await connection.DisposeAsync();
                failed++;
            }
        }

        StatusText = reconnected == 0 && failed == 0
            ? "Ready"
            : $"Reconnected {reconnected} saved connection(s)" +
              (failed > 0 ? $"; {failed} unavailable (use Connect to retry)" : "");
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

    /// <summary>Prompts for and creates an index on a collection, then refreshes
    /// the collection node so the new index appears in the tree. Index DDL runs
    /// as SQL, so it works identically over HTTP and direct-file connections.</summary>
    public async Task CreateIndexAsync(CollectionNodeViewModel collection)
    {
        var request = _dialogs.ShowNewIndexDialog(collection.Name);
        if (request is null) return;

        var sql = SqlText.BuildCreateIndex(request.Collection, request.Name, request.Paths, request.Unique);
        try
        {
            var result = await collection.Database.Server.Connection.ExecuteAsync(collection.Database.Info.Name, sql);
            if (!result.Success)
            {
                _dialogs.ShowError("Create index failed", result.Message ?? "Unknown error.");
                return;
            }
            await collection.RefreshAsync();
            StatusText = $"Index '{request.Name}' created on {request.Collection}";
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Create index failed", ex.Message);
        }
    }

    /// <summary>Inserts a new document into a collection. Seeds the editor with a
    /// template derived from an existing document (minus _id/_etag) so the user
    /// starts from the collection's real shape.</summary>
    public async Task InsertDocumentAsync(CollectionNodeViewModel collection)
    {
        var connection = collection.Database.Server.Connection;
        var database = collection.Database.Info.Name;

        string template;
        try
        {
            var sample = await connection.ExecuteAsync(database, $"SELECT * FROM {collection.Name} LIMIT 1");
            template = JsonDocumentTools.TemplateFromSample(sample.Documents.FirstOrDefault());
        }
        catch
        {
            template = JsonDocumentTools.TemplateFromSample(null);
        }

        var json = _dialogs.ShowInsertDocumentDialog(collection.Name, template);
        if (json is null) return;

        try
        {
            var id = await connection.InsertDocumentAsync(database, collection.Name, json);
            StatusText = $"Inserted document {id} into {collection.Name}";
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Insert failed", ex.Message);
        }
    }

    public async Task DropCollectionAsync(CollectionNodeViewModel collection)
    {
        if (!_dialogs.Confirm("Drop collection",
                $"Drop collection '{collection.Name}' and all its documents and indexes?\n\nThis cannot be undone."))
            return;
        try
        {
            var dropped = await collection.Database.Server.Connection.DropCollectionAsync(
                collection.Database.Info.Name, collection.Name);
            await collection.Database.RefreshAsync();
            StatusText = dropped ? $"Collection '{collection.Name}' dropped" : $"Collection '{collection.Name}' not found";
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Drop collection failed", ex.Message);
        }
    }

    public async Task CompactCollectionAsync(CollectionNodeViewModel collection)
    {
        try
        {
            var result = await collection.Database.Server.Connection.CompactCollectionAsync(
                collection.Database.Info.Name, collection.Name);
            StatusText = $"Compacted '{collection.Name}': {result.BytesReclaimed:N0} bytes reclaimed " +
                         $"({result.PagesCompacted:N0} pages, {result.TimeMs:F0} ms)";
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Compact failed", ex.Message);
        }
    }

    public async Task DropIndexAsync(IndexNodeViewModel index, CollectionNodeViewModel owner)
    {
        if (!_dialogs.Confirm("Drop index", $"Drop index '{index.Info.Name}' on {owner.Name}?"))
            return;

        try
        {
            var result = await owner.Database.Server.Connection.ExecuteAsync(
                owner.Database.Info.Name, SqlText.BuildDropIndex(index.Info.Name));
            if (!result.Success)
            {
                _dialogs.ShowError("Drop index failed", result.Message ?? "Unknown error.");
                return;
            }
            await owner.RefreshAsync();
            StatusText = $"Index '{index.Info.Name}' dropped";
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Drop index failed", ex.Message);
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
    private void OpenClusterFile()
    {
        var path = _dialogs.PickOpenFile("Cluster config (*.json)|*.json|All files (*.*)|*.*");
        if (path is null) return;
        try
        {
            var config = Core.Cluster.ClusterConfig.Load(path);
            OpenCluster(config, path);
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Open cluster config failed", ex.Message);
        }
    }

    [RelayCommand]
    private void NewClusterFile() => OpenCluster(new Core.Cluster.ClusterConfig(), null);

    private void OpenCluster(Core.Cluster.ClusterConfig config, string? path)
    {
        var contentId = path is null ? null : $"cluster:{path}";
        if (contentId is not null)
        {
            var existing = Documents.FirstOrDefault(d => d.ContentId == contentId);
            if (existing is not null) { ActiveDocument = existing; return; }
        }
        var knownEndpoints = _workspace.Connections
            .Where(c => c.Kind == Core.Connections.ConnectionKind.Http && !string.IsNullOrWhiteSpace(c.Url))
            .Select(c => c.Url!)
            .ToList();
        var document = new ClusterDocumentViewModel(config, path, _dialogs, knownEndpoints);
        Documents.Add(document);
        ActiveDocument = document;
        StatusText = path is null ? "New cluster config" : $"Cluster — {System.IO.Path.GetFileName(path)}";
    }

    [RelayCommand]
    private void OpenSqlReference()
    {
        var existing = Documents.FirstOrDefault(d => d.ContentId == "sqlref");
        if (existing is not null) { ActiveDocument = existing; return; }
        Documents.Add(new SqlReferenceDocumentViewModel(InsertSqlSnippet));
        ActiveDocument = Documents[^1];
    }

    /// <summary>Inserts a reference snippet into the most-recently-used query tab,
    /// or falls back to the clipboard when none is open.</summary>
    private void InsertSqlSnippet(string snippet)
    {
        if (_lastActiveQuery is not null && Documents.Contains(_lastActiveQuery))
        {
            _lastActiveQuery.RequestInsert(snippet);
            ActiveDocument = _lastActiveQuery;
            StatusText = "Inserted snippet into query.";
        }
        else
        {
            try { System.Windows.Clipboard.SetText(snippet); } catch { }
            _dialogs.ShowInfo("No query tab open",
                "There's no query tab to insert into, so the snippet was copied to the clipboard. Open a query (right-click a database → New Query) and paste it.");
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

    /// <summary>Opens a query tab against a specific database, optionally seeded
    /// with SQL (e.g. "SELECT * FROM orders LIMIT 100" from a collection node).</summary>
    public void OpenQuery(IDfConnection connection, string database, string? initialSql = null)
    {
        var document = new QueryDocumentViewModel(connection, database, _workspace, initialSql);
        Documents.Add(document);
        ActiveDocument = document;
        StatusText = $"New query on {database}";
    }

    /// <summary>Opens (or re-focuses) the backups panel for a server.</summary>
    public void OpenBackups(ServerNodeViewModel server)
    {
        var contentId = $"backups:{server.Connection.Descriptor.Id}";
        var existing = Documents.FirstOrDefault(d => d.ContentId == contentId);
        if (existing is not null) { ActiveDocument = existing; return; }

        var document = new BackupsDocumentViewModel(server.Connection);
        Documents.Add(document);
        ActiveDocument = document;
        StatusText = $"Backups — {server.Connection.Descriptor.Name}";
    }

    /// <summary>Opens (or re-focuses) the API key manager for a server.</summary>
    public void OpenApiKeys(ServerNodeViewModel server)
    {
        var contentId = $"keys:{server.Connection.Descriptor.Id}";
        var existing = Documents.FirstOrDefault(d => d.ContentId == contentId);
        if (existing is not null) { ActiveDocument = existing; return; }

        var document = new ApiKeysDocumentViewModel(server.Connection, () => SecureServerAsync(server), _dialogs);
        Documents.Add(document);
        ActiveDocument = document;
        StatusText = $"API Keys — {server.Connection.Descriptor.Name}";
    }

    /// <summary>Secures an open (dev-mode) server: mints an admin key, switches
    /// THIS connection to use it, and reconnects — so you don't lock yourself
    /// out (creating the first key ends dev-mode immediately).</summary>
    public async Task SecureServerAsync(ServerNodeViewModel server)
    {
        var descriptor = server.Connection.Descriptor;
        if (descriptor.ApiKeySecretId is not null)
        {
            _dialogs.ShowInfo("Already secured", "This connection already uses an API key.");
            return;
        }
        if (!_dialogs.Confirm("Secure server",
                "This creates an admin API key on the server and switches this connection to use it. " +
                "The server will then require a key for every request.\n\nContinue?"))
            return;

        Core.Models.CreatedApiKey created;
        // "*" is the admin scope in DocumentForge (see AuthContext.FromScopes).
        try { created = await server.Connection.CreateApiKeyAsync("Studio admin key", new[] { "*" }); }
        catch (Exception ex) { _dialogs.ShowError("Secure failed", ex.Message); return; }

        // Persist the key onto the connection and reconnect with it.
        var isSaved = _workspace.Connections.Any(c => c.Id == descriptor.Id);
        descriptor.ApiKeySecretId = _workspace.Secrets.Set(descriptor.ApiKeySecretId, created.Secret);
        if (isSaved) _workspace.SaveConnections();

        // Close the API Keys tab bound to the now-stale connection.
        if (Documents.FirstOrDefault(d => d.ContentId == $"keys:{descriptor.Id}") is { } keysDoc)
            Documents.Remove(keysDoc);

        await DisconnectAsync(server);
        var connection = ConnectionFactory.Create(descriptor, created.Secret);
        try
        {
            await connection.ConnectAsync();
            var node = new ServerNodeViewModel(this, connection);
            Servers.Add(node);
            node.IsExpanded = true;
            StatusText = $"Secured {descriptor.Name} and reconnected with the admin key.";
            _dialogs.ShowInfo("Server secured",
                "The server now requires an API key. This connection was updated to use the new admin key" +
                (isSaved ? " and saved." : ".") +
                "\n\nAdmin key (copy it now for other clients — it can't be shown again):\n\n" + created.Secret);
        }
        catch (Exception ex)
        {
            await connection.DisposeAsync();
            _dialogs.ShowError("Reconnect failed",
                $"The server was secured, but reconnecting with the new key failed: {ex.Message}\n\n" +
                "Use Connect to reconnect with this key:\n" + created.Secret);
        }
    }

    /// <summary>Opens (or re-focuses) the replication topology graph for a server.</summary>
    public void OpenTopology(ServerNodeViewModel server)
    {
        var contentId = $"topo:{server.Connection.Descriptor.Id}";
        var existing = Documents.FirstOrDefault(d => d.ContentId == contentId);
        if (existing is not null) { ActiveDocument = existing; return; }

        var document = new TopologyDocumentViewModel(server.Connection);
        Documents.Add(document);
        ActiveDocument = document;
        StatusText = $"Topology — {server.Connection.Descriptor.Name}";
    }

    /// <summary>Opens (or re-focuses) the dashboard for a database.</summary>
    public void OpenDashboard(DatabaseNodeViewModel database)
    {
        var contentId = $"dash:{database.Server.Connection.Descriptor.Id}:{database.Info.Name}";
        var existing = Documents.FirstOrDefault(d => d.ContentId == contentId);
        if (existing is not null) { ActiveDocument = existing; return; }

        var document = new DashboardDocumentViewModel(database.Server.Connection, database.Info.Name);
        Documents.Add(document);
        ActiveDocument = document;
        StatusText = $"Dashboard — {database.Info.Name}";
    }

    /// <summary>Opens (or re-focuses) the replication panel for a database.</summary>
    public void OpenReplication(DatabaseNodeViewModel database)
    {
        var contentId = $"repl:{database.Server.Connection.Descriptor.Id}:{database.Info.Name}";
        var existing = Documents.FirstOrDefault(d => d.ContentId == contentId);
        if (existing is not null) { ActiveDocument = existing; return; }

        var document = new ReplicationDocumentViewModel(database.Server.Connection, database.Info.Name);
        Documents.Add(document);
        ActiveDocument = document;
        StatusText = $"Replication — {database.Info.Name}";
    }

    /// <summary>Opens a query tab against a server's default database (or its
    /// first, for a single-file connection).</summary>
    public async Task OpenQueryOnServerAsync(ServerNodeViewModel server)
    {
        try
        {
            var databases = await server.Connection.GetDatabasesAsync();
            var target = databases.FirstOrDefault(d => d.IsDefault) ?? databases.FirstOrDefault();
            if (target is null)
            {
                _dialogs.ShowError("New query", "This connection has no databases to query.");
                return;
            }
            OpenQuery(server.Connection, target.Name);
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("New query", ex.Message);
        }
    }

    public void CloseDocument(object? content)
    {
        if (content is DocumentViewModel { CanClose: true } document)
            Documents.Remove(document);
    }

    public async Task ShutdownAsync()
    {
        foreach (var server in Servers.ToList())
            await server.Connection.DisposeAsync();
        Servers.Clear();
    }
}
