using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocumentForge.Studio.Core.Connections;
using DocumentForge.Studio.Core.Models;

namespace DocumentForge.Studio.ViewModels;

public sealed record ApiKeyRow(string Id, string Scopes, string? Description, string? CreatedAt);

/// <summary>A friendly scope option. <see cref="Kind"/> is "*", "db:*",
/// "db-rw" or "db-read"; the last two combine with a chosen database.</summary>
public sealed record ScopeChoice(string Label, string Kind, bool NeedsDb)
{
    public override string ToString() => Label;
}

/// <summary>Manage a server's scoped API keys (a document tab): list, create
/// (secret shown once), revoke.</summary>
public sealed partial class ApiKeysDocumentViewModel : DocumentViewModel
{
    private readonly IDfConnection _connection;
    private readonly Func<Task> _onSecure;

    public ApiKeysDocumentViewModel(IDfConnection connection, Func<Task> onSecure)
    {
        _connection = connection;
        _onSecure = onSecure;
        Title = $"API Keys — {connection.Descriptor.Name}";
        ContentId = $"keys:{connection.Descriptor.Id}";
        _selectedScope = ScopeChoices[0];
    }

    /// <summary>True when this connection has no API key — i.e. the server is
    /// (probably) in open dev-mode. Drives the "secure this server" banner.</summary>
    public bool IsUnsecured => _connection.Descriptor.ApiKeySecretId is null;

    [RelayCommand]
    private Task Secure() => _onSecure();

    public ObservableCollection<ApiKeyRow> Keys { get; } = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _newDescription = "";

    // Guided scope selection instead of a raw scope string.
    public IReadOnlyList<ScopeChoice> ScopeChoices { get; } = new[]
    {
        new ScopeChoice("Full admin — all access", "*", NeedsDb: false),
        new ScopeChoice("One database — read & write", "db-rw", NeedsDb: true),
        new ScopeChoice("One database — read only", "db-read", NeedsDb: true),
        new ScopeChoice("All databases — read & write", "db:*", NeedsDb: false),
    };

    [ObservableProperty] private ScopeChoice _selectedScope;
    [ObservableProperty] private string? _selectedDatabase;

    public ObservableCollection<string> Databases { get; } = new();

    /// <summary>Whether the current scope choice needs a database picked.</summary>
    public bool NeedsDatabase => SelectedScope?.NeedsDb == true;

    partial void OnSelectedScopeChanged(ScopeChoice value) => OnPropertyChanged(nameof(NeedsDatabase));

    // The one-time secret, shown after a successful create until dismissed.
    [ObservableProperty] private string _createdSecret = "";

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var keys = await _connection.GetApiKeysAsync();
            Keys.Clear();
            foreach (var k in keys)
                Keys.Add(new ApiKeyRow(k.Id, string.Join(", ", k.Scopes), k.Description, k.CreatedAt));

            // Populate the database picker for db-scoped keys.
            try
            {
                var dbs = await _connection.GetDatabasesAsync();
                Databases.Clear();
                foreach (var d in dbs) Databases.Add(d.Name);
                SelectedDatabase ??= Databases.FirstOrDefault();
            }
            catch { /* keep any existing list */ }

            StatusMessage = $"{Keys.Count} key(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        var choice = SelectedScope ?? ScopeChoices[0];
        string scope;
        if (choice.Kind == "*") scope = "*";
        else if (choice.Kind == "db:*") scope = "db:*";
        else
        {
            if (string.IsNullOrWhiteSpace(SelectedDatabase))
            {
                StatusMessage = "Pick a database for this scope.";
                return;
            }
            scope = choice.Kind == "db-read" ? $"db:{SelectedDatabase}:read" : $"db:{SelectedDatabase}";
        }

        IsBusy = true;
        try
        {
            var created = await _connection.CreateApiKeyAsync(
                string.IsNullOrWhiteSpace(NewDescription) ? null : NewDescription.Trim(), new[] { scope });
            CreatedSecret = created.Secret;
            NewDescription = "";
            StatusMessage = $"Created key {created.Id} (scope {scope}). Copy the secret now — it can't be retrieved again.";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RevokeAsync(ApiKeyRow? row)
    {
        if (row is null) return;
        IsBusy = true;
        try
        {
            await _connection.RevokeApiKeyAsync(row.Id);
            StatusMessage = $"Revoked key {row.Id}.";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CopySecret()
    {
        if (CreatedSecret.Length == 0) return;
        try { Clipboard.SetText(CreatedSecret); StatusMessage = "Secret copied to clipboard."; }
        catch { /* clipboard busy */ }
    }

    [RelayCommand]
    private void DismissSecret() => CreatedSecret = "";
}
