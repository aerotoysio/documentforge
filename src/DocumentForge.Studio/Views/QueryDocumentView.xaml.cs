using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml;
using DocumentForge.Studio.ViewModels;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace DocumentForge.Studio.Views;

public partial class QueryDocumentView : UserControl
{
    private bool _editorSeeded;

    public QueryDocumentView()
    {
        InitializeComponent();
        Editor.SyntaxHighlighting = LoadSqlHighlighting();
        Editor.PreviewKeyDown += OnEditorKeyDown;
        Loaded += OnLoaded;
    }

    private QueryDocumentViewModel? ViewModel => DataContext as QueryDocumentViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Seed once; re-entrant Loaded (tab re-selection) must not clobber edits.
        if (_editorSeeded || ViewModel is null) return;
        _editorSeeded = true;
        Editor.Text = ViewModel.InitialSql;
        Editor.Focus();
        Editor.CaretOffset = Editor.Text.Length;
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5)
        {
            e.Handled = true;
            _ = RunAsync();
        }
    }

    private async void OnRun(object sender, RoutedEventArgs e) => await RunAsync();

    private async Task RunAsync()
    {
        if (ViewModel is { } vm) await vm.ExecuteAsync(Editor.Text);
    }

    private void OnHistorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryCombo.SelectedItem is string sql)
        {
            Editor.Text = sql;
            Editor.Focus();
            Editor.CaretOffset = Editor.Text.Length;
            // Leave the box unselected so re-picking the same entry re-fires.
            HistoryCombo.SelectedIndex = -1;
        }
    }

    private static IHighlightingDefinition LoadSqlHighlighting()
    {
        using var reader = XmlReader.Create(new StringReader(Xshd));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    // DocumentForge's SQL dialect (keywords + scalar functions), inlined so there's
    // no embedded-resource build wiring to get wrong.
    private const string Xshd = """
<SyntaxDefinition name="DFSQL" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
  <Color name="Comment" foreground="Green"/>
  <Color name="String" foreground="#A31515"/>
  <Color name="Keyword" foreground="Blue" fontWeight="bold"/>
  <Color name="Function" foreground="#267F99"/>
  <Color name="Number" foreground="#098658"/>
  <RuleSet ignoreCase="true">
    <Span color="Comment" begin="--"/>
    <Span color="Comment" multiline="true" begin="/\*" end="\*/"/>
    <Span color="String">
      <Begin>'</Begin>
      <End>'</End>
    </Span>
    <Keywords color="Keyword">
      <Word>SELECT</Word><Word>FROM</Word><Word>WHERE</Word><Word>AND</Word><Word>OR</Word>
      <Word>NOT</Word><Word>INSERT</Word><Word>INTO</Word><Word>VALUES</Word><Word>UPDATE</Word>
      <Word>SET</Word><Word>DELETE</Word><Word>CREATE</Word><Word>UNIQUE</Word><Word>INDEX</Word>
      <Word>ON</Word><Word>DROP</Word><Word>GROUP</Word><Word>BY</Word><Word>ORDER</Word>
      <Word>ASC</Word><Word>DESC</Word><Word>LIMIT</Word><Word>OFFSET</Word><Word>DISTINCT</Word>
      <Word>INNER</Word><Word>LEFT</Word><Word>RIGHT</Word><Word>OUTER</Word><Word>CROSS</Word>
      <Word>JOIN</Word><Word>AS</Word><Word>IN</Word><Word>IS</Word><Word>NULL</Word>
      <Word>LIKE</Word><Word>BETWEEN</Word><Word>HAVING</Word>
    </Keywords>
    <Keywords color="Function">
      <Word>COUNT</Word><Word>SUM</Word><Word>AVG</Word><Word>MIN</Word><Word>MAX</Word>
      <Word>NEWID</Word><Word>GETDATE</Word><Word>NOW</Word><Word>LOWER</Word><Word>UPPER</Word>
      <Word>LEN</Word><Word>SUBSTRING</Word>
    </Keywords>
    <Rule color="Number">\b0[xX][0-9a-fA-F]+|\b\d+(\.\d+)?</Rule>
  </RuleSet>
</SyntaxDefinition>
""";
}
