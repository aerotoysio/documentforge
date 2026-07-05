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

    protected override async Task<IReadOnlyList<TreeNodeViewModel>> LoadChildrenAsync()
    {
        var databases = await Connection.GetDatabasesAsync();
        return databases.Select(db => (TreeNodeViewModel)new DatabaseNodeViewModel(this, _main, db)).ToList();
    }

    [RelayCommand]
    private Task DisconnectAsync() => _main.DisconnectAsync(this);

    [RelayCommand]
    private void NewQuery() => _main.ComingSoon("The query workbench arrives in Phase 2.");
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

    protected override async Task<IReadOnlyList<TreeNodeViewModel>> LoadChildrenAsync()
    {
        var collections = await Server.Connection.GetCollectionNamesAsync(Info.Name);
        var nodes = collections
            .Select(c => (TreeNodeViewModel)new CollectionNodeViewModel(this, _main, c))
            .ToList();
        return [new FolderNodeViewModel($"Collections ({nodes.Count})", nodes)];
    }

    [RelayCommand]
    private Task PropertiesAsync() => _main.ShowDatabasePropertiesAsync(this);

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
        var nodes = indexes.Select(i => (TreeNodeViewModel)new IndexNodeViewModel(i)).ToList();
        return [new FolderNodeViewModel($"Indexes ({nodes.Count})", nodes)];
    }

    [RelayCommand]
    private void BrowseDocuments() => _main.ComingSoon("The collection browser arrives in Phase 3.");
}

public sealed class IndexNodeViewModel : TreeNodeViewModel
{
    public IndexNodeViewModel(IndexInfo info)
    {
        Info = info;
        Name = $"{info.Name}  ({info.JsonPath}){(info.IsUnique ? "  [unique]" : "")}";
        Glyph = "🔑";
    }

    public IndexInfo Info { get; }
}
