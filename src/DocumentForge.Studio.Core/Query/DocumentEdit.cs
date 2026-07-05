using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocumentForge.Studio.Core.Query;

/// <summary>Reconstructs a document from a single edited grid cell. Pure so the
/// editable-grid logic is unit-tested without a UI. The new cell value arrives
/// as a string (that's all a grid gives us), so the type is inferred from the
/// field's existing value — a number field stays a number, a bool stays a bool,
/// an object/array cell is re-parsed as JSON.</summary>
public static class DocumentEdit
{
    /// <summary>Returns the document JSON with a single top-level field replaced.
    /// Throws <see cref="FormatException"/> if the field was an object/array and
    /// the new text isn't valid JSON.</summary>
    public static string ApplyCellEdit(string originalJson, string field, string? newCellValue)
    {
        var obj = JsonNode.Parse(originalJson)?.AsObject()
                  ?? throw new InvalidOperationException("Document is not a JSON object.");
        obj[field] = Coerce(obj[field], newCellValue);
        return obj.ToJsonString();
    }

    private static JsonNode? Coerce(JsonNode? existing, string? raw)
    {
        if (raw is null) return null;

        var kind = existing switch
        {
            JsonObject => JsonValueKind.Object,
            JsonArray => JsonValueKind.Array,
            JsonValue v when v.TryGetValue<JsonElement>(out var el) => el.ValueKind,
            _ => JsonValueKind.Undefined,
        };

        switch (kind)
        {
            case JsonValueKind.Object:
            case JsonValueKind.Array:
                try { return JsonNode.Parse(raw); }
                catch (JsonException) { throw new FormatException($"'{raw}' is not valid JSON for this field."); }

            case JsonValueKind.Number:
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l);
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return JsonValue.Create(d);
                return JsonValue.Create(raw);

            case JsonValueKind.True:
            case JsonValueKind.False:
                if (bool.TryParse(raw, out var b)) return JsonValue.Create(b);
                return JsonValue.Create(raw);

            case JsonValueKind.String:
                return JsonValue.Create(raw);

            default: // null / missing field → best-effort inference
                if (bool.TryParse(raw, out var bb)) return JsonValue.Create(bb);
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ll)) return JsonValue.Create(ll);
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var dd)) return JsonValue.Create(dd);
                return JsonValue.Create(raw);
        }
    }

    /// <summary>Reads a top-level string field (e.g. <c>_id</c>, <c>_etag</c>)
    /// from a document, or null if absent/non-string.</summary>
    public static string? ReadField(string json, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty(field, out var el)
                   && el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
