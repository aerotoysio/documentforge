using System.IO;
using System.Windows;
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

        MainWindow = new MainWindow(workspace);
        MainWindow.Show();
    }
}
