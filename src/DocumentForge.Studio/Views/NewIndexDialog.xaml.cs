using System.Windows;
using DocumentForge.Studio.Core.Query;
using DocumentForge.Studio.Services;

namespace DocumentForge.Studio.Views;

public partial class NewIndexDialog : Window
{
    private readonly string _collection;
    private bool _nameEditedByUser;

    public NewIndexRequest? Result { get; private set; }

    public NewIndexDialog(string collection)
    {
        _collection = collection;
        InitializeComponent();
        CollectionText.Text = collection;
        NameBox.TextChanged += (_, _) => { if (NameBox.IsKeyboardFocusWithin) _nameEditedByUser = true; };
        UniqueCheck.Checked += (_, _) => UpdatePreview();
        UniqueCheck.Unchecked += (_, _) => UpdatePreview();
        PathBox.Focus();
    }

    private string[] Paths() => PathBox.Text
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void OnPathChanged(object sender, RoutedEventArgs e)
    {
        // Auto-suggest a name from the paths until the user types their own.
        if (!_nameEditedByUser)
            NameBox.Text = Paths().Length > 0 ? SqlText.SuggestIndexName(_collection, Paths()) : "";
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var paths = Paths();
        PreviewText.Text = paths.Length > 0 && NameBox.Text.Trim().Length > 0
            ? SqlText.BuildCreateIndex(_collection, NameBox.Text.Trim(), paths, UniqueCheck.IsChecked == true)
            : "";
    }

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        var paths = Paths();
        if (paths.Length == 0)
        {
            PreviewText.Foreground = System.Windows.Media.Brushes.DarkRed;
            PreviewText.Text = "Enter at least one JSON path.";
            return;
        }
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            PreviewText.Foreground = System.Windows.Media.Brushes.DarkRed;
            PreviewText.Text = "Enter an index name.";
            return;
        }
        Result = new NewIndexRequest(_collection, name, paths, UniqueCheck.IsChecked == true);
        DialogResult = true;
    }
}
