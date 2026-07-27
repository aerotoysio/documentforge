using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocumentForge.Studio.Core.Connections;
using DocumentForge.Studio.Core.Models;
using DocumentForge.Studio.Core.Settings;
using DocumentForge.Studio.Services;

namespace DocumentForge.Studio.ViewModels;

/// <summary>One field row inside a diagram box.</summary>
public sealed record DiagramField(string Name, string Detail, string Glyph);

/// <summary>A collection box on the relationship diagram (#152). Position is
/// live-draggable; everything else is rebuilt on refresh.</summary>
public sealed partial class DiagramNode : ObservableObject
{
    public const double BoxWidth = 200;
    public const double HeaderHeight = 30;
    public const double RowHeight = 18;
    public const double FooterPadding = 8;
    private const int MaxVisibleFields = 12;

    public DiagramNode(string collection, IReadOnlyList<DiagramField> fields, bool hasSchema, bool isGhost)
    {
        CollectionName = collection;
        HasSchema = hasSchema;
        IsGhost = isGhost;
        var visible = fields.Take(MaxVisibleFields).ToList();
        if (fields.Count > MaxVisibleFields)
            visible.Add(new DiagramField($"… {fields.Count - MaxVisibleFields} more", "", ""));
        Fields = visible;
        Height = HeaderHeight + Fields.Count * RowHeight + FooterPadding;
    }

    public string CollectionName { get; }
    public IReadOnlyList<DiagramField> Fields { get; }
    /// <summary>True when the collection carries a schema (badge on the header).</summary>
    public bool HasSchema { get; }
    /// <summary>A collection that exists only as a ref target (no documents,
    /// no schema of its own yet) — rendered dashed.</summary>
    public bool IsGhost { get; }
    public double Width => BoxWidth;
    public double Height { get; }

    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;

    /// <summary>Vertical anchor for an edge that starts at a specific field row
    /// (falls back to the header's centre when the field isn't visible).</summary>
    public double FieldAnchorY(string field)
    {
        for (int i = 0; i < Fields.Count; i++)
            if (string.Equals(Fields[i].Name, field, StringComparison.OrdinalIgnoreCase))
                return Y + HeaderHeight + i * RowHeight + RowHeight / 2;
        return Y + HeaderHeight / 2;
    }

    public double HeaderAnchorY => Y + HeaderHeight / 2;
}

/// <summary>A ref connector between two boxes. Endpoints track both boxes as
/// they're dragged (PropertyChanged subscriptions), MSSQL-diagram style.</summary>
public sealed class DiagramEdge : ObservableObject
{
    public DiagramEdge(DiagramNode child, DiagramNode parent, SchemaRefInfo @ref, string? warning)
    {
        Child = child;
        Parent = parent;
        Ref = @ref;
        Warning = warning;
        child.PropertyChanged += OnNodeMoved;
        parent.PropertyChanged += OnNodeMoved;
    }

    public DiagramNode Child { get; }
    public DiagramNode Parent { get; }
    public SchemaRefInfo Ref { get; }
    /// <summary>Non-null when the ref has a performance/consistency caveat
    /// (e.g. unindexed ref field). Shown as ⚠ on the badge + tooltip.</summary>
    public string? Warning { get; }

    private void OnNodeMoved(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(DiagramNode.X) or nameof(DiagramNode.Y))) return;
        OnPropertyChanged(nameof(X1)); OnPropertyChanged(nameof(Y1));
        OnPropertyChanged(nameof(X2)); OnPropertyChanged(nameof(Y2));
        OnPropertyChanged(nameof(BadgeX)); OnPropertyChanged(nameof(BadgeY));
    }

    private bool ParentIsRight => Parent.X >= Child.X + Child.Width / 2;

    // Child end anchors at the ref field's row; parent end at the header.
    public double X1 => ParentIsRight ? Child.X + Child.Width : Child.X;
    public double Y1 => Child.FieldAnchorY(Ref.Field);
    public double X2 => ParentIsRight ? Parent.X : Parent.X + Parent.Width;
    public double Y2 => Parent.HeaderAnchorY;

    public double BadgeX => (X1 + X2) / 2 - 26;
    public double BadgeY => (Y1 + Y2) / 2 - 11;

    /// <summary>"∞→1" plus the onDelete glyph: ⊘ restrict, ∅ setNull, ⇒ cascade.</summary>
    public string Badge => (Warning is null ? "" : "⚠ ") + "∞→1 " + Ref.OnDelete switch
    {
        "cascade" => "⇒",
        "setNull" => "∅",
        _ => "⊘",
    };

    public string Tooltip =>
        $"{Child.CollectionName}.{Ref.Field} → {Parent.CollectionName}.{Ref.TargetField}  (onDelete: {Ref.OnDelete})"
        + (Warning is null ? "" : $"\n⚠ {Warning}")
        + "\nClick to edit or remove this relationship.";
}

/// <summary>
/// Issue #152 — the MSSQL-style database diagram: collections as draggable
/// boxes, schema refs (#151) as connectors. Edits write the whole schema back
/// through the connection, so the engine enforces immediately.
/// </summary>
public sealed partial class DiagramDocumentViewModel : DocumentViewModel
{
    private readonly IDfConnection _connection;
    private readonly string _database;
    private readonly StudioWorkspace _workspace;
    private readonly IDialogService _dialogs;
    private readonly string _layoutKey;
    private Dictionary<string, CollectionSchemaInfo> _schemasByCollection = new(StringComparer.OrdinalIgnoreCase);

    public DiagramDocumentViewModel(IDfConnection connection, string database, StudioWorkspace workspace, IDialogService dialogs)
    {
        _connection = connection;
        _database = database;
        _workspace = workspace;
        _dialogs = dialogs;
        _layoutKey = $"{connection.Descriptor.Id}:{database}";
        Title = $"Diagram — {database}";
        ContentId = $"diagram:{connection.Descriptor.Id}:{database}";
    }

    public ObservableCollection<DiagramNode> Nodes { get; } = new();
    public ObservableCollection<DiagramEdge> Edges { get; } = new();

    [ObservableProperty] private double _canvasWidth = 800;
    [ObservableProperty] private double _canvasHeight = 500;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var collections = await _connection.GetCollectionNamesAsync(_database);
            var schemas = await _connection.GetSchemasAsync(_database);
            _schemasByCollection = schemas.ToDictionary(s => s.Collection, StringComparer.OrdinalIgnoreCase);

            // Ref targets that aren't real collections yet still get a box, so
            // the relationship is visible instead of silently dropped.
            var allNames = collections.ToList();
            foreach (var s in schemas)
            {
                if (!allNames.Contains(s.Collection, StringComparer.OrdinalIgnoreCase)) allNames.Add(s.Collection);
                foreach (var r in s.Refs)
                    if (!allNames.Contains(r.Collection, StringComparer.OrdinalIgnoreCase)) allNames.Add(r.Collection);
            }

            // Preserve on-screen positions across refresh; fall back to the
            // saved layout, then to grid placement for brand-new boxes.
            var current = Nodes.ToDictionary(n => n.CollectionName, n => (n.X, n.Y), StringComparer.OrdinalIgnoreCase);
            var saved = _workspace.GetDiagramLayout(_layoutKey);

            var indexesByCollection = new Dictionary<string, IReadOnlyList<IndexInfo>>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in collections)
            {
                try { indexesByCollection[c] = await _connection.GetIndexesAsync(_database, c); }
                catch { indexesByCollection[c] = Array.Empty<IndexInfo>(); }
            }

            Nodes.Clear();
            Edges.Clear();

            var nodeByName = new Dictionary<string, DiagramNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in allNames)
            {
                var isReal = collections.Contains(name, StringComparer.OrdinalIgnoreCase);
                _schemasByCollection.TryGetValue(name, out var schema);
                var fields = await BuildFieldListAsync(name, schema, isReal);
                var node = new DiagramNode(name, fields, schema is not null, isGhost: !isReal && schema is null);
                if (current.TryGetValue(name, out var pos)) { node.X = pos.X; node.Y = pos.Y; }
                else if (saved.TryGetValue(name, out var savedPos)) { node.X = savedPos.X; node.Y = savedPos.Y; }
                else { node.X = -1; node.Y = -1; } // grid-place below
                Nodes.Add(node);
                nodeByName[name] = node;
            }
            GridPlaceUnpositioned();

            var warnings = 0;
            foreach (var schema in schemas)
            {
                if (!nodeByName.TryGetValue(schema.Collection, out var child)) continue;
                foreach (var r in schema.Refs)
                {
                    if (!nodeByName.TryGetValue(r.Collection, out var parent)) continue;
                    var warning = BuildWarning(schema.Collection, r, indexesByCollection);
                    if (warning is not null) warnings++;
                    Edges.Add(new DiagramEdge(child, parent, r, warning));
                }
            }

            RecomputeCanvasExtent();
            StatusMessage = $"{Nodes.Count} collection(s), {Edges.Count} relationship(s)"
                            + (warnings > 0 ? $", ⚠ {warnings} unindexed ref field(s)" : "")
                            + ". Drag boxes to arrange; click a connector to edit.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally { IsBusy = false; }
    }

    private static string? BuildWarning(string childCollection, SchemaRefInfo r,
        IReadOnlyDictionary<string, IReadOnlyList<IndexInfo>> indexesByCollection)
    {
        var hasChildIndex = indexesByCollection.TryGetValue(childCollection, out var childIdx)
            && childIdx.Any(i => string.Equals(i.JsonPath, r.Field, StringComparison.OrdinalIgnoreCase));
        if (!hasChildIndex)
            return $"'{childCollection}.{r.Field}' has no index — delete checks on '{r.Collection}' scan the collection.";
        if (!string.Equals(r.TargetField, "_id", StringComparison.OrdinalIgnoreCase))
        {
            var hasParentIndex = indexesByCollection.TryGetValue(r.Collection, out var parentIdx)
                && parentIdx.Any(i => string.Equals(i.JsonPath, r.TargetField, StringComparison.OrdinalIgnoreCase));
            if (!hasParentIndex)
                return $"'{r.Collection}.{r.TargetField}' has no index — existence checks scan the collection.";
        }
        return null;
    }

    /// <summary>Field rows: _id first, then schema-declared fields (typed /
    /// required / ref-source), then fields sampled from one live document.</summary>
    private async Task<List<DiagramField>> BuildFieldListAsync(string collection, CollectionSchemaInfo? schema, bool isReal)
    {
        var rows = new List<DiagramField>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "_id", "_etag" };
        rows.Add(new DiagramField("_id", "key", "🔑"));

        if (schema is not null)
        {
            var declared = schema.Required
                .Concat(schema.Types.Keys)
                .Concat(schema.Refs.Select(r => r.Field))
                .Concat(schema.Checks.Select(c => c.Field));
            foreach (var f in declared)
            {
                if (!seen.Add(f)) continue;
                var parts = new List<string>();
                if (schema.Types.TryGetValue(f, out var t)) parts.Add(t);
                if (schema.Required.Contains(f, StringComparer.OrdinalIgnoreCase)) parts.Add("required");
                var isRef = schema.Refs.Any(r => string.Equals(r.Field, f, StringComparison.OrdinalIgnoreCase));
                rows.Add(new DiagramField(f, string.Join(", ", parts), isRef ? "🔗" : ""));
            }
        }

        if (isReal)
        {
            try
            {
                var sample = await _connection.ExecuteAsync(_database, $"SELECT * FROM {collection} LIMIT 1");
                if (sample.Success && sample.Documents.Count > 0)
                {
                    using var doc = JsonDocument.Parse(sample.Documents[0]);
                    foreach (var p in doc.RootElement.EnumerateObject())
                        if (seen.Add(p.Name))
                            rows.Add(new DiagramField(p.Name, p.Value.ValueKind.ToString().ToLowerInvariant(), ""));
                }
            }
            catch { /* sampling is best-effort — dotted names, empty collections */ }
        }
        return rows;
    }

    private void GridPlaceUnpositioned()
    {
        const double marginX = 30, marginY = 24, gapX = 60, gapY = 40;
        // Column heights seeded from already-positioned boxes so new ones
        // don't land on top of them.
        var cols = 3;
        var colX = Enumerable.Range(0, cols).Select(i => marginX + i * (DiagramNode.BoxWidth + gapX)).ToArray();
        var colBottom = new double[cols];
        for (int i = 0; i < cols; i++) colBottom[i] = marginY;
        foreach (var n in Nodes.Where(n => n.X >= 0))
        {
            for (int i = 0; i < cols; i++)
                if (Math.Abs(n.X - colX[i]) < DiagramNode.BoxWidth)
                    colBottom[i] = Math.Max(colBottom[i], n.Y + n.Height + gapY);
        }
        foreach (var n in Nodes.Where(n => n.X < 0))
        {
            var col = Array.IndexOf(colBottom, colBottom.Min());
            n.X = colX[col];
            n.Y = colBottom[col];
            colBottom[col] += n.Height + gapY;
        }
    }

    [RelayCommand]
    private void AutoLayout()
    {
        foreach (var n in Nodes) { n.X = -1; n.Y = -1; }
        GridPlaceUnpositioned();
        RecomputeCanvasExtent();
        SaveLayout();
    }

    /// <summary>Called by the view after a drag completes.</summary>
    public void OnNodeDragCompleted()
    {
        RecomputeCanvasExtent();
        SaveLayout();
    }

    private void SaveLayout() =>
        _workspace.SaveDiagramLayout(_layoutKey,
            Nodes.ToDictionary(n => n.CollectionName, n => new DiagramNodePosition(n.X, n.Y)));

    private void RecomputeCanvasExtent()
    {
        CanvasWidth = Math.Max(800, Nodes.Count == 0 ? 0 : Nodes.Max(n => n.X + n.Width) + 60);
        CanvasHeight = Math.Max(500, Nodes.Count == 0 ? 0 : Nodes.Max(n => n.Y + n.Height) + 60);
    }

    // ---- relationship editing ----

    [RelayCommand]
    private async Task AddRelationshipAsync()
    {
        var collections = Nodes.Where(n => !n.IsGhost).Select(n => n.CollectionName).OrderBy(n => n).ToList();
        if (collections.Count == 0)
        {
            _dialogs.ShowInfo("Database Diagram", "No collections yet — insert a document first.");
            return;
        }
        var outcome = _dialogs.ShowRelationshipDialog(new RelationshipDialogArgs(collections, Existing: null));
        if (outcome.Choice != RelationshipDialogChoice.Save || outcome.Request is null) return;
        await ApplyRelationshipChangeAsync(original: null, outcome.Request);
    }

    [RelayCommand]
    private async Task EditRelationshipAsync(DiagramEdge? edge)
    {
        if (edge is null) return;
        var collections = Nodes.Where(n => !n.IsGhost).Select(n => n.CollectionName).OrderBy(n => n).ToList();
        var original = new RelationshipRequest(
            edge.Child.CollectionName, edge.Ref.Field, edge.Parent.CollectionName, edge.Ref.TargetField, edge.Ref.OnDelete);
        var outcome = _dialogs.ShowRelationshipDialog(new RelationshipDialogArgs(collections, original));
        switch (outcome.Choice)
        {
            case RelationshipDialogChoice.Save when outcome.Request is not null:
                await ApplyRelationshipChangeAsync(original, outcome.Request);
                break;
            case RelationshipDialogChoice.Remove:
                await ApplyRelationshipChangeAsync(original, replacement: null);
                break;
        }
    }

    /// <summary>Removes <paramref name="original"/> (when editing) and adds
    /// <paramref name="replacement"/> (when saving), writing each touched
    /// child schema back whole so non-ref sections survive untouched.</summary>
    private async Task ApplyRelationshipChangeAsync(RelationshipRequest? original, RelationshipRequest? replacement)
    {
        try
        {
            IsBusy = true;
            if (original is not null)
            {
                var schema = GetOrEmptySchema(original.ChildCollection);
                var pruned = schema.Refs.Where(r =>
                    !(string.Equals(r.Field, original.ChildField, StringComparison.OrdinalIgnoreCase)
                      && string.Equals(r.Collection, original.ParentCollection, StringComparison.OrdinalIgnoreCase)
                      && string.Equals(r.TargetField, original.TargetField, StringComparison.OrdinalIgnoreCase))).ToList();
                var newSchema = schema.WithRefs(pruned);
                // Same child collection and still saving? Merge into one PUT below.
                var sameCollection = replacement is not null
                    && string.Equals(replacement.ChildCollection, original.ChildCollection, StringComparison.OrdinalIgnoreCase);
                if (!sameCollection)
                {
                    if (newSchema.IsEmpty) await _connection.DeleteSchemaAsync(_database, original.ChildCollection);
                    else await _connection.PutSchemaAsync(_database, newSchema);
                }
                _schemasByCollection[original.ChildCollection] = newSchema;
            }

            if (replacement is not null)
            {
                var schema = GetOrEmptySchema(replacement.ChildCollection);
                var refs = schema.Refs.ToList();
                refs.Add(new SchemaRefInfo(replacement.ChildField, replacement.ParentCollection,
                    replacement.TargetField, replacement.OnDelete));
                await _connection.PutSchemaAsync(_database, schema.WithRefs(refs));
            }

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Relationship", ex.Message);
            IsBusy = false;
            await RefreshAsync(); // resync — the first half of an edit may have applied
        }
    }

    private CollectionSchemaInfo GetOrEmptySchema(string collection) =>
        _schemasByCollection.TryGetValue(collection, out var s)
            ? s
            : new CollectionSchemaInfo(collection, Array.Empty<string>(),
                new Dictionary<string, string>(), Array.Empty<SchemaCheckInfo>(), Array.Empty<SchemaRefInfo>());
}
