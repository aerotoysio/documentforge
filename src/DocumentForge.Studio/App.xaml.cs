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
    // installer bundles, so the Object Explorer isn't empty on first launch.
    private static void SeedFirstRunConnection(StudioWorkspace workspace)
    {
        if (workspace.Connections.Count > 0) return;
        workspace.UpsertConnection(new ConnectionDescriptor
        {
            Name = "Local DocumentForge (localhost:5001)",
            Kind = ConnectionKind.Http,
            Url = "http://localhost:5001",
        });
    }
}
