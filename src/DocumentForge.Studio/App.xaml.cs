using System.IO;
using System.Windows;
using DocumentForge.Studio.Core.Connections;
using DocumentForge.Studio.Core.Settings;

namespace DocumentForge.Studio;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "DocumentForge Studio",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var workspace = new StudioWorkspace();
        try
        {
            Directory.CreateDirectory(workspace.Settings.DefaultDataDirectory);
        }
        catch (Exception)
        {
            // A locked-down profile without C:\data is not fatal — the user
            // can point connections anywhere.
        }

        SeedFirstRunConnection(workspace);

        // A .dfdb passed on the command line (file association / "Open with").
        var startupFile = e.Args.FirstOrDefault(a =>
            a.EndsWith(".dfdb", StringComparison.OrdinalIgnoreCase) && File.Exists(a));

        MainWindow = new MainWindow(workspace, startupFile);
        MainWindow.Show();
    }

    // First run: give the user a ready-made connection to the local service the
    // installer bundles, so the Object Explorer isn't empty on first launch. The
    // port matches what the installer configured (port.txt), defaulting to the
    // DocumentForge standard port 4300; the API key the installer provisioned
    // for the service (service-key.txt) rides along into the secret store so
    // the seeded connection just works against a deny-by-default node.
    private static void SeedFirstRunConnection(StudioWorkspace workspace)
    {
        var port = ReadInstalledPort();
        var url = $"http://localhost:{port}";
        var apiKey = ReadInstalledServiceKey();

        // Upgrade path: an earlier install seeded this connection without a
        // key and the installer has since provisioned one — attach it rather
        // than leaving the user to dig the key out of service-key.txt.
        var existing = workspace.Connections.Find(c =>
            c.Kind == ConnectionKind.Http &&
            string.Equals(c.Url, url, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (existing.ApiKeySecretId is null && apiKey is not null)
                workspace.UpsertConnection(existing, apiKey);
            return;
        }

        if (workspace.Connections.Count > 0) return;

        workspace.UpsertConnection(new ConnectionDescriptor
        {
            Name = $"Local DocumentForge (localhost:{port})",
            Kind = ConnectionKind.Http,
            Url = url,
        }, apiKey);
    }

    /// <summary>The API key the installer provisioned for the bundled service,
    /// read from service-key.txt next to the exe. Null when absent (older
    /// installs, or the service component wasn't selected).</summary>
    private static string? ReadInstalledServiceKey()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "service-key.txt");
            if (File.Exists(path))
            {
                var key = File.ReadAllText(path).Trim();
                if (key.Length > 0) return key;
            }
        }
        catch { /* fall back to no key */ }
        return null;
    }

    /// <summary>The port the installer configured for the local service, read
    /// from port.txt next to the exe. Defaults to 4300 (DocumentForge standard).</summary>
    private static int ReadInstalledPort()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "port.txt");
            if (File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var p) && p is > 0 and <= 65535)
                return p;
        }
        catch { /* fall back to default */ }
        return 4300;
    }
}
