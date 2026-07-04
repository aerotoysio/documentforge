using System.IO;
using System.Windows;
using System.Windows.Controls;
using DocumentForge.Studio.Core.Connections;
using DocumentForge.Studio.Core.Settings;
using DocumentForge.Studio.Services;
using Microsoft.Win32;

namespace DocumentForge.Studio.Views;

public partial class ConnectDialog : Window
{
    private readonly StudioWorkspace _workspace;
    private ConnectionDescriptor? _editing;

    public ConnectRequest? Result { get; private set; }

    public ConnectDialog(StudioWorkspace workspace)
    {
        _workspace = workspace;
        InitializeComponent();
        RefreshSavedList(select: null);
    }

    private void RefreshSavedList(ConnectionDescriptor? select)
    {
        SavedCombo.ItemsSource = null;
        SavedCombo.ItemsSource = _workspace.Connections.OrderByDescending(c => c.LastConnectedUtc).ToList();
        SavedCombo.SelectedItem = select;
    }

    private void OnSavedSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SavedCombo.SelectedItem is not ConnectionDescriptor saved)
        {
            ForgetButton.IsEnabled = false;
            return;
        }

        ForgetButton.IsEnabled = true;
        _editing = saved;
        NameBox.Text = saved.Name;
        if (saved.Kind == ConnectionKind.File)
        {
            FileRadio.IsChecked = true;
            FilePathBox.Text = saved.FilePath ?? "";
        }
        else
        {
            HttpRadio.IsChecked = true;
            UrlBox.Text = saved.Url ?? "";
            // The key stays in the DPAPI store; an empty box means "keep the
            // stored key". Typing a new one replaces it on save.
            ApiKeyBox.Clear();
        }
        TestStatus.Text = "";
    }

    private void OnForgetSaved(object sender, RoutedEventArgs e)
    {
        if (SavedCombo.SelectedItem is not ConnectionDescriptor saved) return;
        if (MessageBox.Show(this, $"Forget saved connection '{saved.Name}' (and its stored key)?",
                "Forget connection", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _workspace.RemoveConnection(saved.Id);
        if (_editing?.Id == saved.Id) _editing = null;
        RefreshSavedList(select: null);
    }

    private void OnKindChanged(object sender, RoutedEventArgs e)
    {
        if (FilePanel is null || HttpPanel is null) return; // fires during InitializeComponent
        var isFile = FileRadio.IsChecked == true;
        FilePanel.Visibility = isFile ? Visibility.Visible : Visibility.Collapsed;
        HttpPanel.Visibility = isFile ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnBrowseFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "DocumentForge database (*.dfdb)|*.dfdb|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(_workspace.Settings.DefaultDataDirectory)
                ? _workspace.Settings.DefaultDataDirectory
                : null,
        };
        if (dialog.ShowDialog(this) == true)
        {
            FilePathBox.Text = dialog.FileName;
            if (string.IsNullOrWhiteSpace(NameBox.Text))
                NameBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        }
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        if (BuildRequest() is not { } request) return;
        TestButton.IsEnabled = ConnectButton.IsEnabled = false;
        TestStatus.Text = "Testing…";
        try
        {
            await using var connection = ConnectionFactory.Create(request.Descriptor, ResolveKey(request));
            await connection.ConnectAsync();
            var health = await connection.GetHealthAsync();
            TestStatus.Text = health.Healthy
                ? $"✔ Connected. Status: {health.Status}" + (health.Version is null ? "" : $", version {health.Version}")
                : $"⚠ Reached, but degraded: {health.Detail ?? health.Status}";
        }
        catch (Exception ex)
        {
            TestStatus.Text = $"✘ {ex.Message}";
        }
        finally
        {
            TestButton.IsEnabled = ConnectButton.IsEnabled = true;
        }
    }

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        if (BuildRequest() is not { } request) return;
        Result = request;
        DialogResult = true;
    }

    /// <summary>Null when validation failed (the status line explains why).</summary>
    private ConnectRequest? BuildRequest()
    {
        var isFile = FileRadio.IsChecked == true;

        if (isFile && !File.Exists(FilePathBox.Text))
        {
            TestStatus.Text = "✘ Choose an existing .dfdb file (File > New Database creates one).";
            return null;
        }
        if (!isFile && !Uri.TryCreate(UrlBox.Text, UriKind.Absolute, out var uri))
        {
            TestStatus.Text = "✘ Enter a valid URL, e.g. http://localhost:5001";
            return null;
        }

        var name = string.IsNullOrWhiteSpace(NameBox.Text)
            ? (isFile ? Path.GetFileNameWithoutExtension(FilePathBox.Text) : new Uri(UrlBox.Text).Authority)
            : NameBox.Text.Trim();

        // Re-use the saved descriptor's identity when the target matches, so
        // connecting to a saved entry updates it instead of duplicating it.
        var descriptor = _editing is not null && MatchesEditing(isFile)
            ? _editing
            : new ConnectionDescriptor();

        descriptor.Name = name;
        descriptor.Kind = isFile ? ConnectionKind.File : ConnectionKind.Http;
        descriptor.FilePath = isFile ? FilePathBox.Text.Trim() : null;
        descriptor.Url = isFile ? null : UrlBox.Text.Trim().TrimEnd('/');

        var apiKey = !isFile && ApiKeyBox.Password.Length > 0 ? ApiKeyBox.Password : null;
        return new ConnectRequest(descriptor, apiKey, SaveCheck.IsChecked == true);
    }

    private bool MatchesEditing(bool isFile) =>
        _editing is not null &&
        (isFile
            ? _editing.Kind == ConnectionKind.File &&
              string.Equals(_editing.FilePath, FilePathBox.Text.Trim(), StringComparison.OrdinalIgnoreCase)
            : _editing.Kind == ConnectionKind.Http &&
              string.Equals(_editing.Url?.TrimEnd('/'), UrlBox.Text.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

    /// <summary>An empty key box on a saved HTTP connection means "use the
    /// stored key" — resolve it so Test behaves like the real connect.</summary>
    private string? ResolveKey(ConnectRequest request) =>
        request.ApiKey
        ?? (request.Descriptor.ApiKeySecretId is { } id ? _workspace.Secrets.TryGet(id) : null);
}
