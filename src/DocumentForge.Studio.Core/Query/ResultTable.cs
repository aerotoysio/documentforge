using System.Data;
using System.Text;
using System.Text.Json;

namespace DocumentForge.Studio.Core.Query;

/// <summary>Turns a set of JSON documents (as returned by
/// <c>IDfConnection.ExecuteAsync</c>) into shapes the query workbench renders:
/// a flat <see cref="DataTable"/> for the grid, and pretty-printed text for the
/// JSON view. Pure so it can be unit-tested without WPF.</summary>
public static class ResultTable
{
    /// <summary>Flattens documents into a grid. Columns are the union of every
    /// document's top-level keys, in first-seen order. Scalar values render as
    /// text; nested objects/arrays render as their compact JSON so a cell always
    /// holds something readable.</summary>
    public static DataTable Build(IReadOnlyList<string> documents)
    {
        var table = new DataTable();
        var columns = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<Dictionary<string, string?>>(documents.Count);

        foreach (var json in documents)
        {
            var row = new Dictionary<string, string?>(StringComparer.Ordinal);
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        row[prop.Name] = RenderCell(prop.Value);
                        if (columns.Add(prop.Name)) table.Columns.Add(prop.Name, typeof(string));
                    }
                }
                else
                {
                    EnsureValueColumn(table, columns);
                    row["value"] = RenderCell(doc.RootElement);
                }
            }
            catch (JsonException)
            {
                EnsureValueColumn(table, columns);
                row["value"] = json;
            }
            rows.Add(row);
        }

        foreach (var row in rows)
        {
            var dataRow = table.NewRow();
            foreach (DataColumn column in table.Columns)
                dataRow[column] = row.TryGetValue(column.ColumnName, out var v) && v is not null
                    ? v
                    : DBNull.Value;
            table.Rows.Add(dataRow);
        }

        return table;
    }

    /// <summary>Pretty-prints up to <paramref name="cap"/> documents for the JSON
    /// view, appending a truncation note if there are more.</summary>
    public static string PrettyJson(IReadOnlyList<string> documents, int cap = 200)
    {
        if (documents.Count == 0) return "";
        var sb = new StringBuilder();
        var shown = Math.Min(cap, documents.Count);
        for (var i = 0; i < shown; i++)
        {
            try
            {
                using var doc = JsonDocument.Parse(documents[i]);
                sb.Append(JsonSerializer.Serialize(doc.RootElement, IndentedJson));
            }
            catch (JsonException)
            {
                sb.Append(documents[i]);
            }
            sb.AppendLine();
            if (i < shown - 1) sb.AppendLine();
        }
        if (documents.Count > cap)
            sb.AppendLine().Append($"… {documents.Count - cap:N0} more document(s) not shown.");
        return sb.ToString();
    }

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private static void EnsureValueColumn(DataTable table, HashSet<string> columns)
    {
        if (columns.Add("value")) table.Columns.Add("value", typeof(string));
    }

    private static string? RenderCell(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => null,
        _ => value.GetRawText(), // object / array → compact JSON
    };
}
