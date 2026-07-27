using System.Windows;
using System.Windows.Controls;
using DocumentForge.Studio.Services;

namespace DocumentForge.Studio.Views;

/// <summary>Add/edit one diagram relationship (#152). The dialog only shapes
/// the request — validation (dangling targets, setNull-vs-required, reserved
/// collections) is the engine's job and surfaces as a server error.</summary>
public partial class RelationshipDialog : Window
{
    public RelationshipDialogOutcome Outcome { get; private set; } =
        new(RelationshipDialogChoice.Cancel, null);

    public RelationshipDialog(RelationshipDialogArgs args)
    {
        InitializeComponent();
        foreach (var c in args.Collections)
        {
            ChildCollectionBox.Items.Add(c);
            ParentCollectionBox.Items.Add(c);
        }
        if (args.Existing is { } e)
        {
            Title = "Edit Relationship";
            ChildCollectionBox.Text = e.ChildCollection;
            ChildFieldBox.Text = e.ChildField;
            ParentCollectionBox.Text = e.ParentCollection;
            TargetFieldBox.Text = e.TargetField;
            foreach (ComboBoxItem item in OnDeleteBox.Items)
                if (string.Equals((string)item.Tag, e.OnDelete, StringComparison.OrdinalIgnoreCase))
                    item.IsSelected = true;
            RemoveButton.Visibility = Visibility.Visible;
        }
        else
        {
            Title = "Add Relationship";
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var child = ChildCollectionBox.Text.Trim();
        var field = ChildFieldBox.Text.Trim();
        var parent = ParentCollectionBox.Text.Trim();
        var target = TargetFieldBox.Text.Trim();
        if (child.Length == 0 || field.Length == 0 || parent.Length == 0)
        {
            MessageBox.Show(this, "Child collection, child field and parent collection are required.",
                "Relationship", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (target.Length == 0) target = "_id";
        var onDelete = (OnDeleteBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "restrict";
        Outcome = new(RelationshipDialogChoice.Save,
            new RelationshipRequest(child, field, parent, target, onDelete));
        DialogResult = true;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "Remove this relationship? The engine stops enforcing it immediately; no documents are changed.",
                "Remove relationship", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        Outcome = new(RelationshipDialogChoice.Remove, null);
        DialogResult = true;
    }
}
