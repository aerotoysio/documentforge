using System.Text.Json;

namespace DocumentForge.Studio.Core.Query;

/// <summary>Small JSON helpers for the document editor — validation, pretty
/// printing, and building an insert template from a sample document. Pure so
/// they're unit-testable.</summary>
public static class JsonDocumentTools
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Pretty-prints JSON. On a parse error, returns the input unchanged
    /// and reports the message.</summary>
    public static bool TryPrettyPrint(string json, out string pretty, out string? error)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            pretty = JsonSerializer.Serialize(doc.RootElement, Indented);
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            pretty = json;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>True when the text parses as a JSON object (the only thing DF will
    /// accept as a document). Reports why not on failure.</summary>
    public static bool IsJsonObject(string json, out string? error)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "A document must be a JSON object (start with '{').";
                return false;
            }
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Builds an editable insert template from a sample document by
    /// dropping the engine-managed <c>_id</c>/<c>_etag</c> fields and
    /// pretty-printing what's left. Falls back to an empty object when there's
    /// no sample.</summary>
    public static string TemplateFromSample(string? sampleJson)
    {
        if (string.IsNullOrWhiteSpace(sampleJson))
            return "{\n  \n}";
        try
        {
            using var doc = JsonDocument.Parse(sampleJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "{\n  \n}";

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Name is "_id" or "_etag") continue;
                    prop.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return "{\n  \n}";
        }
    }
}
