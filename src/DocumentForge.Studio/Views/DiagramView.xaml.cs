using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DocumentForge.Studio.ViewModels;

namespace DocumentForge.Studio.Views;

/// <summary>Code-behind hosts only the box-drag interaction (#152) — pure view
/// plumbing; the boxes' positions live on the view model and the connectors
/// re-anchor themselves as positions change.</summary>
public partial class DiagramView : UserControl
{
    private DiagramNode? _dragNode;
    private Point _dragStart;         // cursor position at drag start (canvas coords)
    private Point _nodeStart;         // node position at drag start
    private bool _moved;

    public DiagramView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            // First open: populate without requiring a manual refresh click.
            if (DataContext is DiagramDocumentViewModel vm && vm.Nodes.Count == 0)
                await vm.RefreshCommand.ExecuteAsync(null);
        };
    }

    private void Node_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not DiagramNode node) return;
        _dragNode = node;
        _dragStart = e.GetPosition(this);
        _nodeStart = new Point(node.X, node.Y);
        _moved = false;
        border.CaptureMouse();
        e.Handled = true;
    }

    private void Node_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragNode is null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(this);
        var dx = pos.X - _dragStart.X;
        var dy = pos.Y - _dragStart.Y;
        if (!_moved && Math.Abs(dx) < 3 && Math.Abs(dy) < 3) return; // click jitter isn't a drag
        _moved = true;
        _dragNode.X = Math.Max(0, _nodeStart.X + dx);
        _dragNode.Y = Math.Max(0, _nodeStart.Y + dy);
    }

    private void Node_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border) border.ReleaseMouseCapture();
        if (_dragNode is not null && _moved && DataContext is DiagramDocumentViewModel vm)
            vm.OnNodeDragCompleted();
        _dragNode = null;
    }
}
