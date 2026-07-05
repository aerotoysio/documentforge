using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocumentForge.Studio.Core.Connections;
using DocumentForge.Studio.Core.Models;
using DocumentForge.Studio.Services;

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
    private readonly IDialogService _dialogs;

    public ApiKeysDocumentViewModel(IDfConnection connection, Func<Task> onSecure, IDialogService dialogs)
    {
        _connection = connection;
        _onSecure = onSecure;
        _dialogs = dialogs;
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

        // Lock-out protection: on an open (dev-mode) server, the first key ends
        // dev-mode immediately. A non-admin first key locks you out of admin.
        if (IsUnsecured)
        {
            var isAdmin = scope == "*";
            var message = isAdmin
                ? "This server is currently open (no auth). Creating this admin key will make it require a key for every request.\n\n" +
                  "This connection isn't using a key yet, so you'd need to reconnect with the new key afterwards. The " +
                  "\"Secure this server\" button does that for you automatically — prefer it unless you have a reason not to.\n\n" +
                  "Create this admin key anyway?"
                : "⚠  LOCK-OUT RISK\n\n" +
                  "This server is open (no auth). Creating the first key locks it down immediately — and because this key " +
                  "is NOT full admin, you will lose the ability to manage this server from here (and there's no undo without " +
                  "server-side access).\n\n" +
                  "Strongly recommended: Cancel, then use \"Secure this server\" at the top to create an admin key that keeps " +
                  "you connected. You can add scoped keys like this one afterwards.\n\n" +
                  "Create this non-admin key anyway?";
            if (!_dialogs.Confirm("Create key — please read", message))
                return;
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
