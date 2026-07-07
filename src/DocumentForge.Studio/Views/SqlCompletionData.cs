using System.Windows.Media;
using DocumentForge.Studio.Core.Query;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace DocumentForge.Studio.Views;

/// <summary>One entry in the query workbench's completion window: a keyword,
/// function, collection, or sampled field path (issue #114).</summary>
public sealed class SqlCompletionData : ICompletionData
{
    private readonly SqlCompletionItem _item;

    public SqlCompletionData(SqlCompletionItem item) => _item = item;

    public string Text => _item.Text;

    public object Content => $"{Glyph}  {_item.Text}";

    public object Description => _item.Kind switch
    {
        SqlCompletionKind.Keyword => "Keyword",
        SqlCompletionKind.Function => $"Function — {_item.Text}(…)",
        SqlCompletionKind.Collection => "Collection",
        SqlCompletionKind.Field => "Field (from a sampled document)",
        _ => "",
    };

    public ImageSource? Image => null;

    // Fields first, then collections, so data outranks keywords when both match.
    public double Priority => _item.Kind switch
    {
        SqlCompletionKind.Field => 3,
        SqlCompletionKind.Collection => 2,
        SqlCompletionKind.Function => 1,
        _ => 0,
    };

    private string Glyph => _item.Kind switch
    {
        SqlCompletionKind.Keyword => "🔷",
        SqlCompletionKind.Function => "ƒ",
        SqlCompletionKind.Collection => "📄",
        SqlCompletionKind.Field => "▫",
        _ => "",
    };

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
        textArea.Document.Replace(completionSegment, Text);
}
