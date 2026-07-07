using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocumentForge.Studio.Core.Connections;

namespace DocumentForge.Studio.ViewModels;

public sealed record ServiceRow(int Port, string Kind, string Name, string BaseUrl, string DataDir, string Status, string? Started)
{
    public bool IsRunning => Status == "running";
}

/// <summary>Swarm panel (a document tab): spawn, list, kill, and tail the logs
/// of child dfdb processes managed by the connected service's ServiceManager —
/// a local multi-node cluster without a terminal (issue #118). Server
/// connections only.</summary>
public sealed partial class ServicesDocumentViewModel : DocumentViewModel
{
    private readonly IDfConnection _connection;
    private readonly Func<string, Task>? _connectToEndpoint;

    public ServicesDocumentViewModel(IDfConnection connection, Func<string, Task>? connectToEndpoint = null)
    {
        _connection = connection;
        _connectToEndpoint = connectToEndpoint;
        Title = $"Services — {connection.Descriptor.Name}";
        ContentId = $"services:{connection.Descriptor.Id}";
    }

    public ObservableCollection<ServiceRow> Services { get; } = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private ServiceRow? _selectedService;
    [ObservableProperty] private string _spawnPortText = "";
    [ObservableProperty] private string _spawnNodeName = "";
    [ObservableProperty] private string _logText = "";

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var entries = await _connection.GetManagedServicesAsync();
            var selectedPort = SelectedService?.Port;
            Services.Clear();
            foreach (var e in entries.OrderBy(e => e.Port))
                Services.Add(new ServiceRow(
                    e.Port,
                    e.Kind,
                    e.NodeName ?? "—",
                    e.BaseUrl,
                    e.DataDir,
                    e.Running ? "running" : $"exited ({e.ExitCode?.ToString() ?? "?"})",
                    e.StartedAtUtc));
            SelectedService = Services.FirstOrDefault(s => s.Port == selectedPort);
            var running = Services.Count(s => s.IsRunning);
            StatusMessage = Services.Count == 0
                ? "No managed children. Spawn one to stand up a local multi-node cluster."
                : $"{Services.Count} child(ren) · {running} running.";
        }
        catch (NotSupportedException ex) { StatusMessage = ex.Message; }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task SpawnAsync()
    {
        int? port = null;
        var portText = SpawnPortText.Trim();
        if (portText.Length > 0)
        {
            if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) || p is < 1 or > 65535)
            { StatusMessage = "Port must be 1–65535, or blank for a free port."; return; }
            port = p;
        }
        var nodeName = SpawnNodeName.Trim();

        IsBusy = true;
        try
        {
            // apiKey: null — the child inherits the parent's admin key, so the
            // same credentials Studio already holds for the parent work on it.
            var spawned = await _connection.SpawnServiceAsync(port, nodeName.Length > 0 ? nodeName : null, dataDir: null, apiKey: null);
            SpawnPortText = "";
            SpawnNodeName = "";
            StatusMessage = $"Spawned '{spawned.NodeName ?? "child"}' on {spawned.BaseUrl}{(spawned.Ready ? "" : " (still starting)")}.";
            await RefreshAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task StopAsync(ServiceRow? row)
    {
        row ??= SelectedService;
        if (row is null) { StatusMessage = "Select a child service to stop."; return; }
        IsBusy = true;
        try
        {
            await _connection.StopManagedServiceAsync(row.Port);
            StatusMessage = $"Stopped child on port {row.Port}.";
            await RefreshAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task StopAllAsync()
    {
        var running = Services.Where(s => s.IsRunning).ToList();
        if (running.Count == 0) { StatusMessage = "No running children to stop."; return; }
        IsBusy = true;
        var stopped = 0;
        var failed = 0;
        try
        {
            foreach (var s in running)
            {
                try { await _connection.StopManagedServiceAsync(s.Port); stopped++; }
                catch { failed++; }
            }
            StatusMessage = $"Stopped {stopped} child(ren)" + (failed > 0 ? $"; {failed} failed." : ".");
            await RefreshAsync();
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ViewLogAsync(ServiceRow? row)
    {
        row ??= SelectedService;
        if (row is null) { StatusMessage = "Select a child service to tail."; return; }
        IsBusy = true;
        try
        {
            var log = await _connection.GetManagedServiceLogAsync(row.Port);
            LogText = log.Length > 0 ? log : "(log is empty)";
            StatusMessage = $"Log tail for port {row.Port}.";
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ConnectAsync(ServiceRow? row)
    {
        row ??= SelectedService;
        if (row is null || _connectToEndpoint is null) return;
        if (!row.IsRunning) { StatusMessage = "That child has exited — spawn a new one instead."; return; }
        StatusMessage = $"Connecting to {row.BaseUrl}…";
        await _connectToEndpoint(row.BaseUrl);
    }
}
