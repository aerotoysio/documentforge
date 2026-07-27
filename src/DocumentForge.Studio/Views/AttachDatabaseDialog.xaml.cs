using System.IO;
using System.Windows;
using DocumentForge.Studio.Services;
using Microsoft.Win32;

namespace DocumentForge.Studio.Views;

public partial class AttachDatabaseDialog : Window
{
    private readonly bool _serverIsLocal;

    public AttachDatabaseRequest? Result { get; private set; }

    public AttachDatabaseDialog(string serverName, bool serverIsLocal)
    {
        _serverIsLocal = serverIsLocal;
        InitializeComponent();
        Title = $"Attach Database File — {serverName}";
        HintText.Text = serverIsLocal
            ? "The file is attached in place (not copied) — the server opens it from this path."
            : "This server is remote: enter the path AS SEEN BY THE SERVER'S MACHINE. " +
              "Browse only helps if the file is on a share both machines can reach.";
        PathBox.Focus();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "DocumentForge database (*.dfdb)|*.dfdb|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        PathBox.Text = dialog.FileName;
        if (NameBox.Text.Trim().Length == 0)
            NameBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
    }

    private void OnAttach(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text.Trim();
        if (path.Length == 0)
        {
            HintText.Text = "✘ Pick or enter the .dfdb file path.";
            return;
        }
        // Only meaningful when the server shares this filesystem; a remote
        // server resolves the path on its own machine.
        if (_serverIsLocal && !File.Exists(path))
        {
            HintText.Text = $"✘ No file at '{path}'.";
            return;
        }

        var name = NameBox.Text.Trim();
        if (name.Length == 0) name = Path.GetFileNameWithoutExtension(path);
        if (name.Length == 0)
        {
            HintText.Text = "✘ Enter a name to attach the database as.";
            return;
        }
        if (name.StartsWith('_'))
        {
            HintText.Text = "✘ Names starting with '_' are reserved for DocumentForge system databases.";
            return;
        }

        Result = new AttachDatabaseRequest(name, path);
        DialogResult = true;
    }
}
