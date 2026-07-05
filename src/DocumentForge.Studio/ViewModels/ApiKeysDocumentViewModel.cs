using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocumentForge.Studio.Core.Connections;
using DocumentForge.Studio.Core.Models;

namespace DocumentForge.Studio.ViewModels;

public sealed record ApiKeyRow(string Id, string Scopes, string? Description, string? CreatedAt);

/// <summary>Manage a server's scoped API keys (a document tab): list, create
/// (secret shown once), revoke.</summary>
public sealed partial class ApiKeysDocumentViewModel : DocumentViewModel
{
    private readonly IDfConnection _connection;

    public ApiKeysDocumentViewModel(IDfConnection connection)
    {
        _connection = connection;
        Title = $"API Keys — {connection.Descriptor.Name}";
        ContentId = $"keys:{connection.Descriptor.Id}";
    }

    public ObservableCollection<ApiKeyRow> Keys { get; } = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _newDescription = "";
    [ObservableProperty] private string _newScopes = "";

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
        var scopes = NewScopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (scopes.Length == 0)
        {
            StatusMessage = "Enter at least one scope (e.g. admin, or db:orders, or db:orders:read).";
            return;
        }
        IsBusy = true;
        try
        {
            var created = await _connection.CreateApiKeyAsync(
                string.IsNullOrWhiteSpace(NewDescription) ? null : NewDescription.Trim(), scopes);
            CreatedSecret = created.Secret;
            NewDescription = "";
            NewScopes = "";
            StatusMessage = $"Created key {created.Id}. Copy the secret now — it can't be retrieved again.";
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
