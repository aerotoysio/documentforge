using System.Text.Json;
using DocumentForge.Document;
using DocumentForge.Engine;

namespace DocumentForge.Cli.Commands;

/// <summary>
/// Issue #106 — parses the JSON schema/constraint body into a
/// <see cref="CollectionSchema"/> for the PUT-schema endpoints. Grammar:
/// <code>
/// {
///   "required": ["pnr", "passenger"],
///   "types":    { "pnr": "string", "seatCount": "int" },
///   "checks":   [ { "field": "seatCount", "op": ">=", "value": 0 } ]
/// }
/// </code>
/// All three sections are optional. Type names: string, int, number, bool,
/// datetime, object, array. Check ops match the conditional-update grammar.
/// </summary>
public static class SchemaHandler
{
    public sealed class SchemaParseException(string message) : Exception(message);

    public static CollectionSchema Parse(string collection, JsonElement root)
    {
        var required = new List<string>();
        if (root.TryGetProperty("required", out var req))
        {
            if (req.ValueKind != JsonValueKind.Array)
                throw new SchemaParseException("'required' must be an array of field names.");
            foreach (var el in req.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(el.GetString()))
                    throw new SchemaParseException("'required' entries must be non-empty strings.");
                required.Add(el.GetString()!);
            }
        }

        var types = new Dictionary<string, FieldTypeConstraint>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("types", out var t))
        {
            if (t.ValueKind != JsonValueKind.Object)
                throw new SchemaParseException("'types' must be an object of field -> type.");
            foreach (var prop in t.EnumerateObject())
                types[prop.Name] = ParseType(prop.Value.GetString() ?? "");
        }

        var checks = new List<UpdateCondition>();
        if (root.TryGetProperty("checks", out var c))
        {
            if (c.ValueKind != JsonValueKind.Array)
                throw new SchemaParseException("'checks' must be an array.");
            foreach (var el in c.EnumerateArray())
            {
                if (!el.TryGetProperty("field", out var f) || f.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(f.GetString()))
                    throw new SchemaParseException("Each check needs a non-empty string 'field'.");
                if (!el.TryGetProperty("op", out var o) || o.ValueKind != JsonValueKind.String)
                    throw new SchemaParseException("Each check needs a string 'op'.");
                var op = ParseCheckOp(o.GetString()!);
                BsonValue? value = null;
                if (op is not (ConditionOp.Exists or ConditionOp.NotExists))
                {
                    if (!el.TryGetProperty("value", out var v))
                        throw new SchemaParseException($"Check on '{f.GetString()}' with op '{op}' needs a 'value'.");
                    value = BsonDocument.ValueFromJson(v);
                }
                checks.Add(new UpdateCondition(f.GetString()!, op, value));
            }
        }

        if (required.Count == 0 && types.Count == 0 && checks.Count == 0)
            throw new SchemaParseException("Schema is empty — supply at least one of required, types, checks.");

        return new CollectionSchema(collection, required, types, checks);
    }

    private static FieldTypeConstraint ParseType(string name) => name.Trim().ToLowerInvariant() switch
    {
        "string" => FieldTypeConstraint.String,
        "int" or "integer" => FieldTypeConstraint.Int,
        "number" or "double" or "float" => FieldTypeConstraint.Number,
        "bool" or "boolean" => FieldTypeConstraint.Bool,
        "datetime" or "date" => FieldTypeConstraint.DateTime,
        "object" or "document" => FieldTypeConstraint.Object,
        "array" => FieldTypeConstraint.Array,
        _ => throw new SchemaParseException($"Unknown type '{name}'. Use string, int, number, bool, datetime, object, array."),
    };

    private static ConditionOp ParseCheckOp(string op) => op.Trim().ToLowerInvariant() switch
    {
        "==" or "=" or "eq" => ConditionOp.Eq,
        "!=" or "ne" => ConditionOp.Ne,
        "<" or "lt" => ConditionOp.Lt,
        "<=" or "lte" => ConditionOp.Lte,
        ">" or "gt" => ConditionOp.Gt,
        ">=" or "gte" => ConditionOp.Gte,
        "exists" => ConditionOp.Exists,
        "notexists" or "!exists" => ConditionOp.NotExists,
        _ => throw new SchemaParseException($"Unknown check op '{op}'. Use ==, !=, <, <=, >, >=, exists, notExists."),
    };

    /// <summary>Render a schema back to the wire shape for GET.</summary>
    public static object ToWire(CollectionSchema s) => new
    {
        collection = s.Collection,
        required = s.Required,
        types = s.Types.ToDictionary(kv => kv.Key, kv => kv.Value.ToString().ToLowerInvariant()),
        checks = s.Checks.Select(c => new
        {
            field = c.Field,
            op = c.Op.ToString(),
            value = c.Value is null || c.Value.IsNull ? null : c.Value.ToString(),
        }),
    };
}
