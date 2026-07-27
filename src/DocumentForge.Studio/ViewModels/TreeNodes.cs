using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocumentForge.Studio.Core.Connections;
using DocumentForge.Studio.Core.Models;

namespace DocumentForge.Studio.ViewModels;

/// <summary>Base node for the Object Explorer. Children load lazily on first
/// expand; a load failure becomes an inline ⚠ child and is retried on the
/// next Refresh.</summary>
public abstract partial class TreeNodeViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    public string Glyph { get; protected init; } = "";

    public ObservableCollection<TreeNodeViewModel> Children { get; } = new();

    protected bool HasLazyChildren { get; init; }
    private bool _childrenLoaded;

    /// <summary>Call at the end of a lazy node's constructor so the expander
    /// arrow shows before the real children exist.</summary>
    protected void ShowPlaceholder()
    {
        Children.Clear();
        Children.Add(new LoadingNodeViewModel());
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value) _ = EnsureChildrenAsync();
    }

    public async Task EnsureChildrenAsync()
    {
        if (_childrenLoaded || !HasLazyChildren) return;
        _childrenLoaded = true;
        try
        {
            var children = await LoadChildrenAsync();
            Children.Clear();
            foreach (var child in children) Children.Add(child);
        }
        catch (Exception ex)
        {
            _childrenLoaded = false;
            Children.Clear();
            Children.Add(new MessageNodeViewModel(ex.Message));
        }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (!HasLazyChildren) return;
        _childrenLoaded = false;
        ShowPlaceholder();
        await EnsureChildrenAsync();
        IsExpanded = true;
    }

    protected virtual Task<IReadOnlyList<TreeNodeViewModel>> LoadChildrenAsync() =>
        Task.FromResult<IReadOnlyList<TreeNodeViewModel>>([]);
}

public sealed class LoadingNodeViewModel : TreeNodeViewModel
{
    public LoadingNodeViewModel() { Name = "Loading…"; }
}

public sealed class MessageNodeViewModel : TreeNodeViewModel
{
    public MessageNodeViewModel(string message)
    {
        Name = message;
        Glyph = "⚠";
    }
}

public sealed class FolderNodeViewModel : TreeNodeViewModel
{
    public FolderNodeViewModel(string name, IEnumerable<TreeNodeViewModel> children)
    {
        Name = name;
        Glyph = "📁";
        foreach (var child in children) Children.Add(child);
    }
}

public sealed partial class ServerNodeViewModel : TreeNodeViewModel
{
    private readonly MainViewModel _main;

    public ServerNodeViewModel(MainViewModel main, IDfConnection connection)
    {
        _main = main;
        Connection = connection;
        Name = $"{connection.Descriptor.Name}  ({connection.Descriptor.Target})";
        Glyph = connection.Descriptor.Kind == ConnectionKind.File ? "🗎" : "🖥";
        HasLazyChildren = true;
        ShowPlaceholder();
    }

    public IDfConnection Connection { get; }

    public bool CanManageServer => Connection.Capabilities.HasFlag(ConnectionCapabilities.ServerAdmin);

    public bool CanAttachDatabase => Connection.Capabilities.HasFlag(ConnectionCapabilities.CreateDatabase);

    /// <summary>Appends the service's engine version (from /health) to the node
    /// label so the running service version is always visible. Best-effort —
    /// direct-file connections report no version and keep the plain label.</summary>
    public async Task LoadVersionAsync()
    {
        try
        {
            var health = await Connection.GetHealthAsync();
            if (!string.IsNullOrWhiteSpace(health.Version))
                Name = $"{Connection.Descriptor.Name}  ({Connection.Descriptor.Target})  ·  v{health.Version}";
        }
        catch { /* label stays without a version */ }
    }

    protected override async Task<IReadOnlyList<TreeNodeViewModel>> LoadChildrenAsync()
    {
        var databases = await Connection.GetDatabasesAsync();
        return databases.Select(db => (TreeNodeViewModel)new DatabaseNodeViewModel(this, _main, db)).ToList();
    }

    [RelayCommand]
    private Task DisconnectAsync() => _main.DisconnectAsync(this);

    [RelayCommand]
    private Task AttachDatabase() => _main.AttachDatabaseFileAsync(this);

    [RelayCommand]
    private Task NewQuery() => _main.OpenQueryOnServerAsync(this);

    [RelayCommand]
    private void Topology() => _main.OpenTopology(this);

    [RelayCommand]
    private void ApiKeys() => _main.OpenApiKeys(this);

    [RelayCommand]
    private void Backups() => _main.OpenBackups(this);

    [RelayCommand]
    private void ServiceSettings() => _main.OpenServiceSettings(this);

    [RelayCommand]
    private void Services() => _main.OpenServices(this);
}

public sealed partial class DatabaseNodeViewModel : TreeNodeViewModel
{
    private readonly MainViewModel _main;

    public DatabaseNodeViewModel(ServerNodeViewModel server, MainViewModel main, DatabaseInfo info)
    {
        Server = server;
        _main = main;
        Info = info;
        Name = info.IsDefault && server.Connection.Capabilities.HasFlag(ConnectionCapabilities.MultiDatabase)
            ? $"{info.Name}  (default)"
            : info.Name;
        Glyph = "🗄";
        HasLazyChildren = true;
        ShowPlaceholder();
    }

    public ServerNodeViewModel Server { get; }
    public DatabaseInfo Info { get; }

    public bool CanDrop => Server.Connection.Capabilities.HasFlag(ConnectionCapabilities.DropDatabase);

    /// <summary>Server-only features (replication, admin) are hidden for direct-file connections.</summary>
    public bool CanManageServer => Server.Connection.Capabilities.HasFlag(ConnectionCapabilities.ServerAdmin);

    protected override async Task<IReadOnlyList<TreeNodeViewModel>> LoadChildrenAsync()
    {
        var collections = await Server.Connection.GetCollectionNamesAsync(Info.Name);
        var nodes = collections
            .Select(c => (TreeNodeViewModel)new CollectionNodeViewModel(this, _main, c))
            .ToList();
        return [new FolderNodeViewModel($"Collections ({nodes.Count})", nodes)];
    }

    [RelayCommand]
    private void NewQuery() => _main.OpenQuery(Server.Connection, Info.Name);

    [RelayCommand]
    private void Dashboard() => _main.OpenDashboard(this);

    [RelayCommand]
    private void Diagram() => _main.OpenDiagram(this);

    [RelayCommand]
    private void Replication() => _main.OpenReplication(this);

    [RelayCommand]
    private Task PropertiesAsync() => _main.ShowDatabasePropertiesAsync(this);

    [RelayCommand]
    private Task ExportJsonAsync() => _main.ExportDatabaseJsonAsync(this);

    [RelayCommand]
    private Task DropAsync() => _main.DropDatabaseAsync(this);
}

public sealed partial class CollectionNodeViewModel : TreeNodeViewModel
{
    private readonly MainViewModel _main;

    public CollectionNodeViewModel(DatabaseNodeViewModel database, MainViewModel main, string collectionName)
    {
        Database = database;
        _main = main;
        Name = collectionName;
        Glyph = "📄";
        HasLazyChildren = true;
        ShowPlaceholder();
    }

    public DatabaseNodeViewModel Database { get; }

    protected override async Task<IReadOnlyList<TreeNodeViewModel>> LoadChildrenAsync()
    {
        var indexes = await Database.Server.Connection.GetIndexesAsync(Database.Info.Name, Name);
        var nodes = indexes.Select(i => (TreeNodeViewModel)new IndexNodeViewModel(this, _main, i)).ToList();
        return [new FolderNodeViewModel($"Indexes ({nodes.Count})", nodes)];
    }

    // Until the Phase 3 document browser lands, "open" a collection by dropping
    // a SELECT into a query tab — immediately useful and a natural default.
    [RelayCommand]
    private void OpenCollection() =>
        _main.OpenQuery(Database.Server.Connection, Database.Info.Name, $"SELECT * FROM {Name} LIMIT 100");

    [RelayCommand]
    private void NewQuery() => _main.OpenQuery(Database.Server.Connection, Database.Info.Name);

    [RelayCommand]
    private Task InsertDocument() => _main.InsertDocumentAsync(this);

    [RelayCommand]
    private Task NewIndex() => _main.CreateIndexAsync(this);

    [RelayCommand]
    private Task Compact() => _main.CompactCollectionAsync(this);

    [RelayCommand]
    private Task DropCollection() => _main.DropCollectionAsync(this);
}

public sealed partial class IndexNodeViewModel : TreeNodeViewModel
{
    private readonly CollectionNodeViewModel _collection;
    private readonly MainViewModel _main;

    public IndexNodeViewModel(CollectionNodeViewModel collection, MainViewModel main, IndexInfo info)
    {
        _collection = collection;
        _main = main;
        Info = info;
        Name = $"{info.Name}  ({info.JsonPath}){(info.IsUnique ? "  [unique]" : "")}";
        Glyph = "🔑";
    }

    public IndexInfo Info { get; }

    [RelayCommand]
    private Task Drop() => _main.DropIndexAsync(this, _collection);
}
