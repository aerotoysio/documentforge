using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocumentForge.Studio.Core.Cluster;
using DocumentForge.Studio.Services;

namespace DocumentForge.Studio.ViewModels;

public sealed partial class ClusterShardRow : ObservableObject
{
    public ClusterShardRow(ShardDescriptor shard) => Shard = shard;

    public ShardDescriptor Shard { get; }
    public string Name => Shard.Name;
    public string LeaderEndpoint => Shard.LeaderEndpoint;
    public int FollowerCount => Shard.Followers.Count;

    [ObservableProperty] private string _health = "not checked";
}

public sealed class ClusterCollectionRow
{
    public ClusterCollectionRow(string name, CollectionPolicyDescriptor policy)
    {
        Name = name;
        Strategy = policy.Strategy.ToString();
        ShardKeyPath = policy.Strategy == ShardingStrategy.Hash ? policy.ShardKeyPath ?? "(missing shard key)" : "—";
    }

    public string Name { get; }
    public string Strategy { get; }
    public string ShardKeyPath { get; }
}

/// <summary>Edits a DocumentForge cluster config (shards + per-collection sharding
/// strategy), health-checks each shard, and can pull the live config from a
/// running router. Client-only: it manipulates the config and pings endpoints;
/// the engine still owns cluster behaviour.</summary>
public sealed partial class ClusterDocumentViewModel : DocumentViewModel
{
    private ClusterConfig _config;
    private readonly IDialogService _dialogs;

    public ClusterDocumentViewModel(ClusterConfig config, string? filePath, IDialogService dialogs,
        IReadOnlyList<string>? knownEndpoints = null)
    {
        _config = config;
        _dialogs = dialogs;
        FilePath = filePath;
        ContentId = filePath is null ? $"cluster:new:{Guid.NewGuid():N}" : $"cluster:{filePath}";
        UpdateTitle();
        ReloadRows();
        NewCollectionStrategy = ShardingStrategy.Hash;

        // Suggest shard endpoints from the user's saved connections + any endpoints
        // already in this config, so they can pick instead of typing a URL.
        AddEndpointSuggestions((knownEndpoints ?? Array.Empty<string>())
            .Concat(_config.Shards.Select(s => s.LeaderEndpoint)));
    }

    private void AddEndpointSuggestions(IEnumerable<string> endpoints)
    {
        foreach (var e in endpoints.Where(e => !string.IsNullOrWhiteSpace(e)))
            if (!ShardEndpointSuggestions.Contains(e, StringComparer.OrdinalIgnoreCase))
                ShardEndpointSuggestions.Add(e);
    }

    public ObservableCollection<ClusterShardRow> Shards { get; } = new();
    public ObservableCollection<ClusterCollectionRow> Collections { get; } = new();
    public IReadOnlyList<ShardingStrategy> Strategies { get; } = new[] { ShardingStrategy.Hash, ShardingStrategy.Replicated };

    /// <summary>Endpoint URLs offered in the "add shard" box (saved connections + existing shards).</summary>
    public ObservableCollection<string> ShardEndpointSuggestions { get; } = new();

    /// <summary>Collection names discovered on healthy shards during a health check.</summary>
    public ObservableCollection<string> CollectionSuggestions { get; } = new();

    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isBusy;

    /// <summary>True when the config has edits not yet written to disk. Drives the
    /// "*" marker in the tab title so unsaved changes are visible.</summary>
    [ObservableProperty] private bool _isDirty;

    partial void OnIsDirtyChanged(bool value) => UpdateTitle();

    // Add-shard inputs
    [ObservableProperty] private string _newShardName = "";
    [ObservableProperty] private string _newShardEndpoint = "";

    // Add-collection inputs
    [ObservableProperty] private string _newCollectionName = "";
    [ObservableProperty] private ShardingStrategy _newCollectionStrategy;
    [ObservableProperty] private string _newCollectionShardKey = "";

    // "Load from live cluster" input — a running `dfdb router` endpoint.
    [ObservableProperty] private string _routerEndpoint = "";

    /// <summary>Only a hash-sharded collection needs a shard key.</summary>
    public bool NeedsShardKey => NewCollectionStrategy == ShardingStrategy.Hash;

    partial void OnNewCollectionStrategyChanged(ShardingStrategy value) => OnPropertyChanged(nameof(NeedsShardKey));

    public string SummaryText =>
        $"{Shards.Count} shard(s) · {Collections.Count} collection policy(ies) · {_config.VirtualNodesPerShard} vnodes/shard";

    private void ReloadRows()
    {
        Shards.Clear();
        foreach (var s in _config.Shards) Shards.Add(new ClusterShardRow(s));
        Collections.Clear();
        foreach (var (name, policy) in _config.Collections) Collections.Add(new ClusterCollectionRow(name, policy));
        OnPropertyChanged(nameof(SummaryText));
    }

    private void UpdateTitle()
    {
        var baseTitle = FilePath is null ? "Cluster (unsaved)" : $"Cluster — {Path.GetFileNameWithoutExtension(FilePath)}";
        Title = IsDirty ? $"{baseTitle} *" : baseTitle;
    }

    private void MarkDirty() => IsDirty = true;

    [RelayCommand]
    private void AddShard()
    {
        var name = NewShardName.Trim();
        var endpoint = NewShardEndpoint.Trim();
        if (name.Length == 0 || endpoint.Length == 0)
        {
            StatusMessage = "Enter a shard name and endpoint.";
            return;
        }
        if (_config.Shards.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = $"A shard named '{name}' already exists.";
            return;
        }
        if (_config.Shards.Any(s => s.LeaderEndpoint.Equals(endpoint, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = $"Shard '{_config.Shards.First(s => s.LeaderEndpoint.Equals(endpoint, StringComparison.OrdinalIgnoreCase)).Name}' " +
                            $"already points at {endpoint}. Each shard needs a distinct endpoint.";
            return;
        }
        _config.Shards.Add(new ShardDescriptor { Name = name, Endpoint = endpoint });
        if (!ShardEndpointSuggestions.Contains(endpoint, StringComparer.OrdinalIgnoreCase))
            ShardEndpointSuggestions.Add(endpoint);
        NewShardName = "";
        NewShardEndpoint = "";
        ReloadRows();
        MarkDirty();
        StatusMessage = $"Added shard '{name}'. Save to write the cluster config.";
    }

    [RelayCommand]
    private void RemoveShard(ClusterShardRow? row)
    {
        if (row is null) return;
        _config.Shards.RemoveAll(s => ReferenceEquals(s, row.Shard));
        ReloadRows();
        MarkDirty();
        StatusMessage = $"Removed shard '{row.Name}'.";
    }

    [RelayCommand]
    private void AddCollection()
    {
        var name = NewCollectionName.Trim();
        if (name.Length == 0) { StatusMessage = "Enter a collection name."; return; }
        var key = NewCollectionShardKey.Trim();
        if (NewCollectionStrategy == ShardingStrategy.Hash && key.Length == 0)
        {
            StatusMessage = "A hash-sharded collection needs a shard key path.";
            return;
        }
        var isUpdate = _config.Collections.ContainsKey(name);
        _config.Collections[name] = new CollectionPolicyDescriptor
        {
            Strategy = NewCollectionStrategy,
            ShardKeyPath = NewCollectionStrategy == ShardingStrategy.Hash ? key : null,
        };
        NewCollectionName = "";
        NewCollectionShardKey = "";
        ReloadRows();
        MarkDirty();
        StatusMessage = $"{(isUpdate ? "Updated" : "Added")} '{name}' → {NewCollectionStrategy}. Save to write the cluster config.";
    }

    [RelayCommand]
    private void RemoveCollection(ClusterCollectionRow? row)
    {
        if (row is null) return;
        _config.Collections.Remove(row.Name);
        ReloadRows();
        MarkDirty();
        StatusMessage = $"Removed policy for '{row.Name}'.";
    }

    [RelayCommand]
    private void Save()
    {
        var path = FilePath ?? _dialogs.PickSaveFile("Cluster config (*.json)|*.json|All files (*.*)|*.*", "cluster.json");
        if (path is null) return;
        try
        {
            _config.Save(path);
            FilePath = path;
            IsDirty = false;
            UpdateTitle();
            StatusMessage = $"Saved to {path}";
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Save failed", ex.Message);
        }
    }

    /// <summary>Write a copy to a chosen location without adopting it as this
    /// tab's file (a plain "export a copy").</summary>
    [RelayCommand]
    private void Export()
    {
        var suggested = FilePath is null ? "cluster.json" : Path.GetFileName(FilePath);
        var path = _dialogs.PickSaveFile("DocumentForge cluster (*.json)|*.json|All files (*.*)|*.*", suggested);
        if (path is null) return;
        try
        {
            _config.Save(path);
            StatusMessage = $"Exported a copy to {path}";
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Export failed", ex.Message);
        }
    }

    /// <summary>Pull the live config from a running <c>dfdb router</c> so you can
    /// edit what the cluster is actually running. It's read-only on the engine
    /// side: Studio loads a copy, and applying changes still means saving a file
    /// and restarting the router.</summary>
    [RelayCommand]
    private async Task LoadFromRouterAsync()
    {
        var endpoint = RouterEndpoint.Trim();
        if (endpoint.Length == 0)
        {
            StatusMessage = "Enter the endpoint of a running `dfdb router` to load its live config.";
            return;
        }
        IsBusy = true;
        try
        {
            _config = await ClusterConfig.LoadFromRouterAsync(endpoint);
            ReloadRows();
            AddEndpointSuggestions(_config.Shards.Select(s => s.LeaderEndpoint));
            MarkDirty();
            StatusMessage = $"Loaded live config from {endpoint} (v{_config.Version}, {_config.Shards.Count} shard(s)). " +
                            "This is a working copy — Save it to a file and restart the router to apply edits.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't load from {endpoint}: {ex.Message}. " +
                            "It must be a running `dfdb router` exposing GET /cluster/config.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CheckHealthAsync()
    {
        IsBusy = true;
        var up = 0;
        try
        {
            var discovered = new SortedSet<string>(CollectionSuggestions, StringComparer.OrdinalIgnoreCase);
            foreach (var row in Shards)
            {
                row.Health = "checking…";
                var h = await ClusterHealth.PingAsync(row.LeaderEndpoint, TimeSpan.FromSeconds(5));
                if (h.Reachable && h.Healthy)
                {
                    up++;
                    row.Health = $"🟢 up{(h.Version is null ? "" : $" · v{h.Version}")}";
                    // Best-effort: learn the collection names living on this shard
                    // so the "add collection" box can suggest them.
                    foreach (var c in await ClusterHealth.TryGetCollectionsAsync(row.LeaderEndpoint, TimeSpan.FromSeconds(5)))
                        discovered.Add(c);
                }
                else if (h.Reachable)
                {
                    row.Health = $"🟡 degraded · {h.Status}";
                }
                else
                {
                    row.Health = $"🔴 unreachable · {h.Error}";
                }
            }
            if (discovered.Count != CollectionSuggestions.Count)
            {
                CollectionSuggestions.Clear();
                foreach (var c in discovered) CollectionSuggestions.Add(c);
            }
            StatusMessage = Shards.Count == 0
                ? "No shards to check — add some first."
                : $"{up}/{Shards.Count} shard(s) healthy.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
