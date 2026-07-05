using System.IO;
using System.Windows;
using DocumentForge.Studio.Services;
using DocumentForge.Studio.ViewModels;

namespace DocumentForge.Studio.Views;

public partial class NewDatabaseDialog : Window
{
    private const string LocalFileTarget = "Local file";
    private readonly IReadOnlyList<ServerNodeViewModel> _servers;
    private readonly string _defaultDataDir;

    public NewDatabaseRequest? Result { get; private set; }

    public NewDatabaseDialog(IReadOnlyList<ServerNodeViewModel> servers, string defaultDataDir)
    {
        _servers = servers;
        _defaultDataDir = defaultDataDir;
        InitializeComponent();

        var targets = new List<string> { LocalFileTarget };
        targets.AddRange(servers.Select(s => s.Connection.Descriptor.Name));
        TargetCombo.ItemsSource = targets;
        TargetCombo.SelectedIndex = 0;
        HintText.Text = $"A new .dfdb file will be created in {_defaultDataDir} and opened as a direct-file connection.";
        TargetCombo.SelectionChanged += (_, _) => HintText.Text = TargetCombo.SelectedIndex == 0
            ? $"A new .dfdb file will be created in {_defaultDataDir} and opened as a direct-file connection."
            : "The database is created (or attached) in the server's data directory via the DocumentForge service.";
        NameBox.Focus();
    }

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            HintText.Text = "✘ Enter a database name.";
            return;
        }
        if (name.StartsWith('_'))
        {
            HintText.Text = "✘ Names starting with '_' are reserved for DocumentForge system databases.";
            return;
        }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            HintText.Text = "✘ The name contains characters that aren't valid in a file name.";
            return;
        }

        var server = TargetCombo.SelectedIndex <= 0 ? null : _servers[TargetCombo.SelectedIndex - 1];
        Result = new NewDatabaseRequest(name, server);
        DialogResult = true;
    }
}
