using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocumentForge.Studio.Core.Connections;
using DocumentForge.Studio.Core.Models;

namespace DocumentForge.Studio.ViewModels;

/// <summary>One read-only setting row. Note carries the "restart-required"
/// label so an operator never mistakes a display for something editable here.</summary>
public sealed record ConfigRow(string Section, string Setting, string Value, string Note);

/// <summary>Service settings (a document tab): the redacted effective node
/// configuration (engine #111), with the live-editable semi-sync knobs
/// editable in place. Fields the server can only pick up on a restart are
/// labelled "restart to change" — a property of the field, not a pending
/// state (the server rejects editing them over the API; they're a node.json
/// edit + restart, and the Restart button here drives that restart). Server
/// connections only (issue #115).</summary>
public sealed partial class ServiceSettingsDocumentViewModel : DocumentViewModel
{
    private readonly IDfConnection _connection;
    private readonly Services.IDialogService _dialogs;

    public ServiceSettingsDocumentViewModel(IDfConnection connection, Services.IDialogService dialogs)
    {
        _connection = connection;
        _dialogs = dialogs;
        Title = $"Service settings — {connection.Descriptor.Name}";
        ContentId = $"svcconfig:{connection.Descriptor.Id}";
    }

    public ObservableCollection<ConfigRow> Rows { get; } = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "";

    // Live-editable knobs (PUT /admin/config). Kept as text so partial input
    // doesn't fight the binding; validated on Apply.
    [ObservableProperty] private string _minSyncReplicasText = "";
    [ObservableProperty] private string _syncTimeoutSecondsText = "";

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var config = await _connection.GetServiceConfigAsync();
            Populate(config);
            StatusMessage = "Loaded. The semi-sync knobs apply live; fields marked \"restart to change\" take a node.json edit + a restart (button above).";
        }
        catch (NotSupportedException ex) { StatusMessage = ex.Message; }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>Restart the node via POST /admin/restart, then poll /health until
    /// it's back (or give up after a minute) and re-load the settings.</summary>
    [RelayCommand]
    private async Task RestartServiceAsync()
    {
        if (!_dialogs.Confirm("Restart service",
            $"Restart the DocumentForge node behind \"{_connection.Descriptor.Name}\"?\n\n" +
            "The node flushes to disk first, then exits and is restarted by its Windows service " +
            "(or IIS). It will be unavailable for a few seconds. A node run from a plain console " +
            "has no supervisor and will just stop."))
            return;

        IsBusy = true;
        try
        {
            var ack = await _connection.RestartServerAsync();
            StatusMessage = $"Restarting… ({ack})";

            var back = false;
            var deadline = DateTime.UtcNow.AddSeconds(60);
            await Task.Delay(TimeSpan.FromSeconds(2)); // let the old process exit
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var health = await _connection.GetHealthAsync();
                    if (health.Healthy) { back = true; break; }
                }
                catch { /* still down — keep polling */ }
                await Task.Delay(TimeSpan.FromSeconds(2));
            }

            if (back)
            {
                var config = await _connection.GetServiceConfigAsync();
                Populate(config);
                StatusMessage = "Restarted — the node is back and settings have been re-loaded.";
            }
            else
            {
                StatusMessage = "Restart requested, but the node did not come back within 60s. " +
                                "If it runs as a plain console process (no Windows service / IIS), start it again manually.";
            }
        }
        catch (NotSupportedException ex) { StatusMessage = ex.Message; }
        catch (Exception ex) { StatusMessage = $"Restart failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        int? minSync = null;
        double? timeout = null;

        var msrText = MinSyncReplicasText.Trim();
        if (msrText.Length > 0)
        {
            if (!int.TryParse(msrText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var msr) || msr < 0)
            { StatusMessage = "Min sync replicas must be a non-negative integer."; return; }
            minSync = msr;
        }

        var stsText = SyncTimeoutSecondsText.Trim();
        if (stsText.Length > 0)
        {
            if (!double.TryParse(stsText, NumberStyles.Float, CultureInfo.InvariantCulture, out var sts) || sts <= 0)
            { StatusMessage = "Sync timeout must be a positive number of seconds."; return; }
            timeout = sts;
        }

        if (minSync is null && timeout is null)
        { StatusMessage = "Nothing to apply — both live-editable fields are empty."; return; }

        IsBusy = true;
        try
        {
            var config = await _connection.UpdateServiceConfigAsync(minSync, timeout);
            Populate(config);
            StatusMessage = "Applied. The change is live (no restart) and persisted to the node's configuration.";
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    private void Populate(ServiceConfigInfo c)
    {
        MinSyncReplicasText = c.MinSyncReplicas.ToString(CultureInfo.InvariantCulture);
        SyncTimeoutSecondsText = c.SyncTimeoutSeconds.ToString(CultureInfo.InvariantCulture);

        // restartRequired on the wire is a STATIC descriptor — "changing this field
        // needs a restart" — never a live pending-restart state. Label it so it
        // can't be read as "the service needs a restart right now".
        var restart = new HashSet<string>(c.RestartRequired, StringComparer.OrdinalIgnoreCase);
        string NoteFor(string field) => restart.Contains(field) ? "restart to change" : "";

        Rows.Clear();
        Rows.Add(new ConfigRow("Node", "Node name", c.NodeName ?? "—", NoteFor("nodeName")));
        Rows.Add(new ConfigRow("Node", "Port", c.Port.ToString(), NoteFor("port")));
        Rows.Add(new ConfigRow("Node", "Data directory", c.DataDir ?? "—", NoteFor("dataDir")));
        Rows.Add(new ConfigRow("Node", "Bind all interfaces", c.BindAllInterfaces ? "yes" : "no", NoteFor("bindAllInterfaces")));
        Rows.Add(new ConfigRow("Node", "Insecure dev mode", c.InsecureDevMode ? "ON (no auth!)" : "off", NoteFor("insecureDevMode")));
        Rows.Add(new ConfigRow("Node", "HTTP endpoint", c.HttpEndpoint ?? "—", ""));

        Rows.Add(new ConfigRow("Security", "Admin API key",
            c.AdminKeyConfigured ? $"configured ({c.AdminKeyFingerprint})" : "not configured", NoteFor("apiKey")));
        Rows.Add(new ConfigRow("Security", "Replication secret",
            c.ReplicationSecretConfigured ? $"configured ({c.ReplicationSecretFingerprint})" : "not configured", NoteFor("replicationSecret")));
        Rows.Add(new ConfigRow("Security", "TLS",
            c.TlsConfigured
                ? $"{c.TlsCertPath}{(c.TlsCertPasswordConfigured ? " (password set)" : "")}"
                : "not configured", NoteFor("tls")));
        foreach (var k in c.ScopedKeys)
            Rows.Add(new ConfigRow("Security", $"Scoped key: {k.Description ?? "(no description)"}",
                $"{string.Join(", ", k.Scopes)} ({k.KeyFingerprint})", NoteFor("scopedKeys")));

        Rows.Add(new ConfigRow("Replication", "Role", c.ReplicationRole ?? "none", NoteFor("role")));
        if (c.ReplicationPort is { } rp)
            Rows.Add(new ConfigRow("Replication", "Replication port", rp.ToString(), NoteFor("replicationPort")));
        if (c.LeaderHost is not null)
            Rows.Add(new ConfigRow("Replication", "Leader", $"{c.LeaderHost}:{c.LeaderPort}", NoteFor("leaderHost")));
        Rows.Add(new ConfigRow("Replication", "Min sync replicas", c.MinSyncReplicas.ToString(), "live-editable"));
        Rows.Add(new ConfigRow("Replication", "Sync timeout (s)",
            c.SyncTimeoutSeconds.ToString(CultureInfo.InvariantCulture), "live-editable"));

        Rows.Add(new ConfigRow("Network", "Public base URL", c.PublicBaseUrl ?? "—", NoteFor("publicBaseUrl")));
    }
}
