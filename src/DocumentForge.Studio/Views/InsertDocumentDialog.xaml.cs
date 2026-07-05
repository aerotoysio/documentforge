using System.Windows;
using DocumentForge.Studio.Core.Query;

namespace DocumentForge.Studio.Views;

public partial class InsertDocumentDialog : Window
{
    /// <summary>The validated JSON to insert (set only when the user confirms).</summary>
    public string? Result { get; private set; }

    public InsertDocumentDialog(string collection, string template)
    {
        InitializeComponent();
        CollectionText.Text = collection;
        JsonBox.Text = template;
        Loaded += (_, _) => { JsonBox.Focus(); JsonBox.CaretIndex = JsonBox.Text.Length; };
    }

    private void OnFormat(object sender, RoutedEventArgs e)
    {
        if (JsonDocumentTools.TryPrettyPrint(JsonBox.Text, out var pretty, out var error))
        {
            JsonBox.Text = pretty;
            StatusText.Text = "";
        }
        else
        {
            StatusText.Text = $"Can't format: {error}";
        }
    }

    private void OnInsert(object sender, RoutedEventArgs e)
    {
        if (!JsonDocumentTools.IsJsonObject(JsonBox.Text, out var error))
        {
            StatusText.Text = error;
            return;
        }
        Result = JsonBox.Text;
        DialogResult = true;
    }
}
