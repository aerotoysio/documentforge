using System.Text.Json;

namespace DocumentForge.Studio.Core.Query;

public enum SqlCompletionKind { Keyword, Function, Collection, Field }

public sealed record SqlCompletionItem(string Text, SqlCompletionKind Kind);

/// <summary>Pure completion logic for the query workbench (issue #114):
/// what word is being typed, what the context expects (collection names after
/// FROM/INTO/UPDATE/JOIN), and field paths flattened out of a sampled
/// document. The view owns the AvalonEdit CompletionWindow; everything here
/// is UI-free and unit-tested.</summary>
public static class SqlAutocomplete
{
    /// <summary>Keywords of the DocumentForge SQL dialect — mirrors the
    /// workbench highlighter. BETWEEN/HAVING are deliberately absent: the
    /// engine rejects them (see SqlReference "Gotchas"), so offering them
    /// would type errors for the user.</summary>
    public static readonly IReadOnlyList<string> Keywords = new[]
    {
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "INSERT", "INTO", "VALUES",
        "UPDATE", "SET", "DELETE", "CREATE", "UNIQUE", "INDEX", "ON", "DROP",
        "GROUP BY", "ORDER BY", "ASC", "DESC", "LIMIT", "OFFSET", "DISTINCT",
        "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "CROSS JOIN", "JOIN", "AS",
        "IN", "IS", "NULL", "LIKE",
    };

    public static readonly IReadOnlyList<string> Functions = new[]
    {
        "COUNT", "SUM", "AVG", "MIN", "MAX",
        "NEWID", "GETDATE", "NOW", "LOWER", "UPPER", "LEN", "SUBSTRING",
    };

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '.';

    /// <summary>The identifier being typed at the caret: its start offset and
    /// text. Dots count as word characters so nested field paths
    /// ("passenger.lastName") complete as one unit.</summary>
    public static (int Start, string Prefix) CurrentWord(string text, int caret)
    {
        if (caret > text.Length) caret = text.Length;
        var start = caret;
        while (start > 0 && IsWordChar(text[start - 1])) start--;
        return (start, text[start..caret]);
    }

    /// <summary>True when the token immediately before the word being typed is
    /// FROM / INTO / UPDATE / JOIN — the spots where a collection name goes.</summary>
    public static bool ExpectsCollection(string text, int wordStart)
    {
        var i = wordStart - 1;
        while (i >= 0 && char.IsWhiteSpace(text[i])) i--;
        if (i < 0) return false;
        var end = i + 1;
        while (i >= 0 && char.IsLetter(text[i])) i--;
        var token = text[(i + 1)..end];
        return token.ToUpperInvariant() is "FROM" or "INTO" or "UPDATE" or "JOIN";
    }

    /// <summary>The collection the statement reads/writes (first identifier
    /// after FROM/INTO/UPDATE) — the source for field-name completion. Null
    /// when the statement doesn't name one yet.</summary>
    public static string? TargetCollection(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            text, @"\b(?:FROM|INTO|UPDATE)\s+([A-Za-z_][A-Za-z0-9_]*)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Flattens a sampled document into completable field paths:
    /// dot-notation for nested objects (capped at depth 3), the array path
    /// itself for arrays, plus "arr[0].field" for arrays of objects. Returns
    /// an empty list for null/unparsable input.</summary>
    public static IReadOnlyList<string> FlattenFields(string? sampleJson, int maxDepth = 3)
    {
        if (string.IsNullOrWhiteSpace(sampleJson)) return Array.Empty<string>();
        try
        {
            using var doc = JsonDocument.Parse(sampleJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Array.Empty<string>();
            var paths = new List<string>();
            Walk(doc.RootElement, "", 0);
            paths.Sort(StringComparer.OrdinalIgnoreCase);
            return paths;

            void Walk(JsonElement obj, string prefix, int depth)
            {
                foreach (var prop in obj.EnumerateObject())
                {
                    var path = prefix.Length == 0 ? prop.Name : $"{prefix}.{prop.Name}";
                    paths.Add(path);
                    if (depth + 1 >= maxDepth) continue;
                    switch (prop.Value.ValueKind)
                    {
                        case JsonValueKind.Object:
                            Walk(prop.Value, path, depth + 1);
                            break;
                        case JsonValueKind.Array when prop.Value.GetArrayLength() > 0
                                                      && prop.Value[0].ValueKind == JsonValueKind.Object:
                            Walk(prop.Value[0], $"{path}[0]", depth + 1);
                            break;
                    }
                }
            }
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>The completion set for the caret position. After
    /// FROM/INTO/UPDATE/JOIN only collections are offered; elsewhere keywords,
    /// functions, fields (of the statement's target collection) and
    /// collections all compete, filtered by the typed prefix.</summary>
    public static IReadOnlyList<SqlCompletionItem> GetCompletions(
        string text, int caret,
        IReadOnlyList<string> collections,
        IReadOnlyList<string> fields)
    {
        var (start, prefix) = CurrentWord(text, caret);
        var items = new List<SqlCompletionItem>();

        if (ExpectsCollection(text, start))
        {
            items.AddRange(collections.Select(c => new SqlCompletionItem(c, SqlCompletionKind.Collection)));
        }
        else
        {
            items.AddRange(Keywords.Select(k => new SqlCompletionItem(k, SqlCompletionKind.Keyword)));
            items.AddRange(Functions.Select(f => new SqlCompletionItem(f, SqlCompletionKind.Function)));
            items.AddRange(fields.Select(f => new SqlCompletionItem(f, SqlCompletionKind.Field)));
            items.AddRange(collections.Select(c => new SqlCompletionItem(c, SqlCompletionKind.Collection)));
        }

        if (prefix.Length > 0)
            items = items.Where(i => i.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();

        return items;
    }
}
