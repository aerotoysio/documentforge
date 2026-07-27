using System.Text.Json;

namespace DocumentForge.Studio.Core.Models;

/// <summary>One referential-integrity constraint on a collection (#151):
/// <see cref="Field"/> must match <see cref="TargetField"/> of a document in
/// <see cref="Collection"/>. <see cref="OnDelete"/> is the wire spelling:
/// "restrict" | "setNull" | "cascade".</summary>
public sealed record SchemaRefInfo(string Field, string Collection, string TargetField, string OnDelete);

/// <summary>One CHECK constraint. <see cref="Value"/> is kept as the raw JSON
/// element so an edit→save round-trip through Studio never changes its type.</summary>
public sealed record SchemaCheckInfo(string Field, string Op, JsonElement? Value);

/// <summary>
/// A collection's schema as the server serves it (#106/#151). Studio's diagram
/// designer reads all four sections and writes back the whole schema when it
/// edits refs, so the non-ref sections must round-trip losslessly.
/// </summary>
public sealed record CollectionSchemaInfo(
    string Collection,
    IReadOnlyList<string> Required,
    IReadOnlyDictionary<string, string> Types,
    IReadOnlyList<SchemaCheckInfo> Checks,
    IReadOnlyList<SchemaRefInfo> Refs)
{
    public bool IsEmpty => Required.Count == 0 && Types.Count == 0 && Checks.Count == 0 && Refs.Count == 0;

    /// <summary>Serialize to the PUT-schema wire grammar (SchemaHandler's
    /// input shape). Check ops come back from GET as enum names ("Gte");
    /// the parser accepts their lowercase forms, so ops are lowercased here.</summary>
    public string ToSchemaJson()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            if (Required.Count > 0)
            {
                w.WriteStartArray("required");
                foreach (var r in Required) w.WriteStringValue(r);
                w.WriteEndArray();
            }
            if (Types.Count > 0)
            {
                w.WriteStartObject("types");
                foreach (var (field, type) in Types) w.WriteString(field, type.ToLowerInvariant());
                w.WriteEndObject();
            }
            if (Checks.Count > 0)
            {
                w.WriteStartArray("checks");
                foreach (var c in Checks)
                {
                    w.WriteStartObject();
                    w.WriteString("field", c.Field);
                    w.WriteString("op", c.Op.ToLowerInvariant());
                    if (c.Value is { } v) { w.WritePropertyName("value"); v.WriteTo(w); }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            if (Refs.Count > 0)
            {
                w.WriteStartArray("refs");
                foreach (var r in Refs)
                {
                    w.WriteStartObject();
                    w.WriteString("field", r.Field);
                    w.WriteString("collection", r.Collection);
                    w.WriteString("targetField", r.TargetField);
                    w.WriteString("onDelete", r.OnDelete);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Parse one schema object from the GET wire shape.</summary>
    public static CollectionSchemaInfo FromWire(JsonElement el)
    {
        var collection = el.TryGetProperty("collection", out var c) ? c.GetString() ?? "" : "";

        var required = new List<string>();
        if (el.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
            foreach (var r in req.EnumerateArray())
                if (r.GetString() is { Length: > 0 } s) required.Add(s);

        var types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (el.TryGetProperty("types", out var t) && t.ValueKind == JsonValueKind.Object)
            foreach (var p in t.EnumerateObject())
                types[p.Name] = p.Value.GetString() ?? "";

        var checks = new List<SchemaCheckInfo>();
        if (el.TryGetProperty("checks", out var ch) && ch.ValueKind == JsonValueKind.Array)
            foreach (var k in ch.EnumerateArray())
                checks.Add(new SchemaCheckInfo(
                    k.TryGetProperty("field", out var f) ? f.GetString() ?? "" : "",
                    k.TryGetProperty("op", out var o) ? o.GetString() ?? "" : "",
                    k.TryGetProperty("value", out var v) && v.ValueKind != JsonValueKind.Null
                        ? v.Clone() : null));

        var refs = new List<SchemaRefInfo>();
        if (el.TryGetProperty("refs", out var rf) && rf.ValueKind == JsonValueKind.Array)
            foreach (var r in rf.EnumerateArray())
                refs.Add(new SchemaRefInfo(
                    r.TryGetProperty("field", out var rfield) ? rfield.GetString() ?? "" : "",
                    r.TryGetProperty("collection", out var rcoll) ? rcoll.GetString() ?? "" : "",
                    r.TryGetProperty("targetField", out var rtf) ? rtf.GetString() ?? "_id" : "_id",
                    r.TryGetProperty("onDelete", out var rod) ? rod.GetString() ?? "restrict" : "restrict"));

        return new CollectionSchemaInfo(collection, required, types, checks, refs);
    }

    /// <summary>This schema with a different refs list (non-ref sections untouched).</summary>
    public CollectionSchemaInfo WithRefs(IReadOnlyList<SchemaRefInfo> refs) => this with { Refs = refs };
}
