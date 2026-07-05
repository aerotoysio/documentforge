using System.Text.Json;
using DocumentForge.Core;
using DocumentForge.Document;
using DocumentForge.Engine;
using Microsoft.AspNetCore.Http;

namespace DocumentForge.Cli.Commands;

/// <summary>
/// Issue #103 — HTTP surface for the atomic conditional-update / compare-and-set
/// primitive. Shared by the flat <c>/collections/{name}/{id}/conditional</c>
/// route and the scoped <c>/db/{name}/collections/{collection}/{id}/conditional</c>
/// route so the request grammar and status-code mapping live in one place.
///
/// <para>Body grammar:
/// <code>
/// {
///   "conditions": [ { "field": "seats", "op": "&gt;=", "value": 1 } ],
///   "operations": [ { "field": "seats", "op": "dec", "value": 1 } ]
/// }
/// </code>
/// All conditions must hold (evaluated against the live document under the write
/// lock) or nothing is written. 200 on success (with the new <c>_etag</c>),
/// 409 when a condition fails, 404 when the document doesn't exist, 400 on a
/// malformed request.</para>
/// </summary>
public static class ConditionalUpdateHandler
{
    public static async Task<IResult> Handle(DocumentForgeDb db, string collection, string id, HttpRequest request)
    {
        if (!Guid.TryParse(id, out var guid))
            return Results.BadRequest(new { error = "This endpoint expects DocumentForge's internal _id (a Guid)." });

        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(body))
            return Results.BadRequest(new { error = "Empty body. Expected { conditions: [...], operations: [...] }." });

        List<UpdateCondition> conditions;
        List<UpdateOperation> operations;
        try
        {
            using var json = JsonDocument.Parse(body);
            conditions = ParseConditions(json.RootElement);
            operations = ParseOperations(json.RootElement);
        }
        catch (ConditionalUpdateParseException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (JsonException ex) { return Results.BadRequest(new { error = $"Invalid JSON: {ex.Message}" }); }

        if (operations.Count == 0)
            return Results.BadRequest(new { error = "At least one operation is required." });

        ConditionalUpdateResult result;
        try
        {
            result = db.TryConditionalUpdate(collection, new DocumentId(guid), conditions, operations);
        }
        catch (DocumentForgeException ex) { return Results.BadRequest(new { error = ex.Message }); }

        return result.Status switch
        {
            ConditionalUpdateStatus.Success => Results.Ok(new
            {
                success = true,
                id,
                collection,
                etag = result.Etag,
                document = JsonDocument.Parse(result.Document!.ToJson()).RootElement,
            }),
            ConditionalUpdateStatus.ConditionFailed => Results.Json(new
            {
                success = false,
                code = "conditionFailed",
                failedCondition = result.FailedCondition,
                error = $"Condition not met: {result.FailedCondition}. The document changed or the guard did not hold.",
            }, statusCode: StatusCodes.Status409Conflict),
            _ => Results.NotFound(new { success = false, error = "Document not found." }),
        };
    }

    private static List<UpdateCondition> ParseConditions(JsonElement root)
    {
        var list = new List<UpdateCondition>();
        if (!root.TryGetProperty("conditions", out var arr)) return list;
        if (arr.ValueKind != JsonValueKind.Array)
            throw new ConditionalUpdateParseException("'conditions' must be an array.");
        foreach (var el in arr.EnumerateArray())
        {
            var field = RequireField(el, "condition");
            var op = ParseConditionOp(RequireString(el, "op", "condition"));
            BsonValue? value = null;
            if (op is not (ConditionOp.Exists or ConditionOp.NotExists))
            {
                if (!el.TryGetProperty("value", out var v))
                    throw new ConditionalUpdateParseException($"Condition on '{field}' with op '{op}' requires a 'value'.");
                value = BsonDocument.ValueFromJson(v);
            }
            list.Add(new UpdateCondition(field, op, value));
        }
        return list;
    }

    private static List<UpdateOperation> ParseOperations(JsonElement root)
    {
        var list = new List<UpdateOperation>();
        if (!root.TryGetProperty("operations", out var arr)) return list;
        if (arr.ValueKind != JsonValueKind.Array)
            throw new ConditionalUpdateParseException("'operations' must be an array.");
        foreach (var el in arr.EnumerateArray())
        {
            var field = RequireField(el, "operation");
            var op = ParseMutationOp(RequireString(el, "op", "operation"));
            if (!el.TryGetProperty("value", out var v))
                throw new ConditionalUpdateParseException($"Operation '{op}' on '{field}' requires a 'value'.");
            list.Add(new UpdateOperation(field, op, BsonDocument.ValueFromJson(v)));
        }
        return list;
    }

    private static string RequireField(JsonElement el, string kind)
    {
        var field = RequireString(el, "field", kind);
        // Top-level fields only for now — nested-path atomic mutation is a
        // follow-up. Reject dots explicitly so callers aren't surprised.
        if (field.Contains('.'))
            throw new ConditionalUpdateParseException(
                $"Nested field paths ('{field}') are not yet supported by conditional update; use a top-level field.");
        return field;
    }

    private static string RequireString(JsonElement el, string prop, string kind)
    {
        if (!el.TryGetProperty(prop, out var p) || p.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(p.GetString()))
            throw new ConditionalUpdateParseException($"Each {kind} needs a non-empty string '{prop}'.");
        return p.GetString()!;
    }

    private static ConditionOp ParseConditionOp(string op) => op.Trim().ToLowerInvariant() switch
    {
        "==" or "=" or "eq" => ConditionOp.Eq,
        "!=" or "ne" => ConditionOp.Ne,
        "<" or "lt" => ConditionOp.Lt,
        "<=" or "lte" => ConditionOp.Lte,
        ">" or "gt" => ConditionOp.Gt,
        ">=" or "gte" => ConditionOp.Gte,
        "exists" => ConditionOp.Exists,
        "notexists" or "!exists" => ConditionOp.NotExists,
        _ => throw new ConditionalUpdateParseException(
            $"Unknown condition op '{op}'. Use ==, !=, <, <=, >, >=, exists, notExists."),
    };

    private static MutationOp ParseMutationOp(string op) => op.Trim().ToLowerInvariant() switch
    {
        "set" => MutationOp.Set,
        "inc" or "+" => MutationOp.Inc,
        "dec" or "-" => MutationOp.Dec,
        _ => throw new ConditionalUpdateParseException($"Unknown operation op '{op}'. Use set, inc, dec."),
    };

    private sealed class ConditionalUpdateParseException(string message) : Exception(message);
}
