using DocumentForge.Core;

namespace DocumentForge.Index;

public sealed class IndexDefinition
{
    public string Name { get; init; } = "";
    public CollectionName CollectionName { get; init; }
    // For composite indexes this is "status,createdAt". For single-field it's just "pnr".
    // Access via Paths for the parsed list.
    public string JsonPath { get; init; } = "";
    public bool IsUnique { get; init; }
    public PageId RootPage { get; set; } = PageId.Invalid;

    /// <summary>Paths parsed from JsonPath (split on ','). Single-field indexes return a one-element list.</summary>
    public IReadOnlyList<string> Paths => _paths ??= ParsePaths(JsonPath);
    private IReadOnlyList<string>? _paths;

    public bool IsComposite => Paths.Count > 1;

    private static IReadOnlyList<string> ParsePaths(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }
}
