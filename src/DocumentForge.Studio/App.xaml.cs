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
        SeedFirstRunConnection(workspace);
        try
        {
            Directory.CreateDirectory(workspace.Settings.DefaultDataDirectory);
        }
        catch (Exception)
        {
            // A locked-down profile without C:\data is not fatal — the user
            // can point connections anywhere.
        }

        // A .dfdb passed on the command line (file association / "Open with").
        var startupFile = e.Args.FirstOrDefault(a =>
            a.EndsWith(".dfdb", StringComparison.OrdinalIgnoreCase) && File.Exists(a));

        MainWindow = new MainWindow(workspace, startupFile);
        MainWindow.Show();
    }

    // First run: give the user a ready-made connection to the local service the
    // installer bundles, so the Object Explorer isn't empty on first launch. The
    // installer records its chosen data folder in datadir.txt next to the exe;
    // port.txt and service-key.txt live inside that folder. The API key rides
    // into the secret store so the seeded connection just works against a
    // deny-by-default node, and Studio's default data directory follows the
    // folder the installer configured.
    private static void SeedFirstRunConnection(StudioWorkspace workspace)
    {
        var dataDir = Services.InstalledService.DataDir;
        var port = Services.InstalledService.Port(dataDir);
        var url = $"http://localhost:{port}";
        var apiKey = Services.InstalledService.Key(dataDir);

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

        workspace.Settings.DefaultDataDirectory = dataDir;
        workspace.SaveSettings();
        workspace.UpsertConnection(new ConnectionDescriptor
        {
            Name = $"Local DocumentForge (localhost:{port})",
            Kind = ConnectionKind.Http,
            Url = url,
        }, apiKey);
    }

}
