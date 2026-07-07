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
/// editable in place. Restart-required fields are labelled as such — changing
/// them is a node.json edit + restart, and the server rejects them over the
/// API. Server connections only (issue #115).</summary>
public sealed partial class ServiceSettingsDocumentViewModel : DocumentViewModel
{
    private readonly IDfConnection _connection;

    public ServiceSettingsDocumentViewModel(IDfConnection connection)
    {
        _connection = connection;
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
            StatusMessage = "Loaded. Only the semi-sync knobs are live-editable; restart-required fields need a node.json edit + service restart.";
        }
        catch (NotSupportedException ex) { StatusMessage = ex.Message; }
        catch (Exception ex) { StatusMessage = ex.Message; }
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

        var restart = new HashSet<string>(c.RestartRequired, StringComparer.OrdinalIgnoreCase);
        string NoteFor(string field) => restart.Contains(field) ? "restart-required" : "";

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
