using System.Text.Json;
using DocumentForge.Core;
using DocumentForge.Document;
using DocumentForge.Engine;
using Microsoft.AspNetCore.Http;

namespace DocumentForge.Cli.Commands;

/// <summary>
/// Issue #70 — partial-document update (PATCH). A thin, elegant layer over the
/// atomic conditional-update primitive (issue #103): the whole merge is applied
/// under the engine write lock in a single commit, so a single-field write no
/// longer needs a client-side read-modify-write round trip (and its race).
///
/// <para>Two body grammars, auto-detected:</para>
/// <list type="number">
/// <item><b>JSON Merge Patch</b> (RFC 7396, shallow) — the default. Each
/// top-level key is applied to the document: a non-null value replaces the
/// field, and <c>null</c> removes it. Example:
/// <code>{ "status": "shipped", "tracking": "1Z...", "tempFlag": null }</code></item>
/// <item><b>Operator form</b> — a body of the shape <c>{ "$ops": [ ... ] }</c>
/// carrying explicit atomic operations (set / inc / dec / unset). This is what
/// makes counters race-free without a read:
/// <code>{ "$ops": [ { "field": "sold", "op": "inc", "value": 1 } ] }</code></item>
/// </list>
///
/// <para>Optimistic concurrency: an <c>If-Match</c> header pins the update to a
/// specific <c>_etag</c>. If the live document has moved on, the PATCH returns
/// 412 Precondition Failed and writes nothing — safe read-modify-write for
/// clients that still want whole-value semantics.</para>
///
/// <para>Status: 200 on success (returns the new <c>_etag</c> + document),
/// 404 when the document doesn't exist, 412 on an <c>If-Match</c> mismatch,
/// 400 on a malformed body.</para>
/// </summary>
public static class PatchHandler
{
    public static async Task<IResult> Handle(DocumentForgeDb db, string collection, string id, HttpRequest request)
    {
        if (!Guid.TryParse(id, out var guid))
            return Results.BadRequest(new { error = "This endpoint expects DocumentForge's internal _id (a Guid). To patch by a business key, use PATCH /collections/{name}/by/{field}/{value}." });

        using var reader = new StreamReader(request.Body);
        var body = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(body))
            return Results.BadRequest(new { error = "Empty body. Send a JSON merge document (fields to set; null to remove) or { \"$ops\": [...] }." });

        // Optional optimistic-concurrency guard: If-Match: "<etag>".
        var conditions = new List<UpdateCondition>();
        string? ifMatch = ParseIfMatch(request);
        if (ifMatch is not null)
            conditions.Add(new UpdateCondition("_etag", ConditionOp.Eq, BsonValue.FromString(ifMatch)));

        List<UpdateOperation> operations;
        try
        {
            using var json = JsonDocument.Parse(body);
            operations = ParseBody(json.RootElement);
        }
        catch (PatchParseException ex) { return Results.BadRequest(new { error = ex.Message }); }
        catch (JsonException ex) { return Results.BadRequest(new { error = $"Invalid JSON: {ex.Message}" }); }

        if (operations.Count == 0)
            return Results.BadRequest(new { error = "Patch is empty — no fields to change." });

        ConditionalUpdateResult result;
        try
        {
            result = db.TryConditionalUpdate(collection, new DocumentId(guid), conditions, operations);
        }
        catch (SchemaViolationException ex) { return Results.BadRequest(new { error = ex.Message, code = "schemaViolation" }); }
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
            // The only condition PATCH ever attaches is the If-Match ETag guard,
            // so a ConditionFailed here is always a precondition mismatch → 412.
            ConditionalUpdateStatus.ConditionFailed => Results.Json(new
            {
                success = false,
                code = "preconditionFailed",
                error = $"If-Match precondition failed: the document's current _etag is not '{ifMatch}'. It was modified concurrently.",
            }, statusCode: StatusCodes.Status412PreconditionFailed),
            _ => Results.NotFound(new { success = false, error = "Document not found." }),
        };
    }

    private static string? ParseIfMatch(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("If-Match", out var values)) return null;
        var raw = values.ToString();
        if (string.IsNullOrWhiteSpace(raw) || raw == "*") return null; // "*" = "any", no guard
        return raw.Trim().Trim('"'); // tolerate quoted ETag form
    }

    private static List<UpdateOperation> ParseBody(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new PatchParseException("PATCH body must be a JSON object.");

        // Operator form: { "$ops": [ { field, op, value? } ] }.
        if (root.TryGetProperty("$ops", out var ops))
        {
            if (ops.ValueKind != JsonValueKind.Array)
                throw new PatchParseException("'$ops' must be an array of { field, op, value } operations.");
            var list = new List<UpdateOperation>();
            foreach (var el in ops.EnumerateArray())
                list.Add(ParseOperation(el));
            return list;
        }

        // Merge-patch form: each top-level key → set, null → unset.
        var merged = new List<UpdateOperation>();
        foreach (var prop in root.EnumerateObject())
        {
            var field = prop.Name;
            if (string.IsNullOrWhiteSpace(field))
                throw new PatchParseException("Field names must be non-empty.");
            if (field.StartsWith('$'))
                throw new PatchParseException($"'{field}' looks like an operator; a merge body cannot mix in $-prefixed keys. Use the {{ \"$ops\": [...] }} form for explicit operations.");
            GuardField(field);
            merged.Add(prop.Value.ValueKind == JsonValueKind.Null
                ? new UpdateOperation(field, MutationOp.Unset, null)
                : new UpdateOperation(field, MutationOp.Set, BsonDocument.ValueFromJson(prop.Value)));
        }
        return merged;
    }

    private static UpdateOperation ParseOperation(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object)
            throw new PatchParseException("Each $ops entry must be an object { field, op, value }.");
        if (!el.TryGetProperty("field", out var f) || f.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(f.GetString()))
            throw new PatchParseException("Each operation needs a non-empty string 'field'.");
        var field = f.GetString()!;
        GuardField(field);
        if (!el.TryGetProperty("op", out var o) || o.ValueKind != JsonValueKind.String)
            throw new PatchParseException($"Operation on '{field}' needs a string 'op' (set, inc, dec, unset).");
        var op = ParseMutationOp(o.GetString()!);

        if (op == MutationOp.Unset)
            return new UpdateOperation(field, op, null); // value ignored for unset

        if (!el.TryGetProperty("value", out var v))
            throw new PatchParseException($"Operation '{o.GetString()}' on '{field}' requires a 'value'.");
        return new UpdateOperation(field, op, BsonDocument.ValueFromJson(v));
    }

    private static void GuardField(string field)
    {
        // Mirror the conditional-update constraint: top-level fields only.
        if (field.Contains('.'))
            throw new PatchParseException(
                $"Nested field paths ('{field}') are not yet supported by PATCH; patch a top-level field.");
        if (string.Equals(field, "_id", StringComparison.Ordinal))
            throw new PatchParseException("'_id' is immutable and cannot be patched.");
    }

    private static MutationOp ParseMutationOp(string op) => op.Trim().ToLowerInvariant() switch
    {
        "set" => MutationOp.Set,
        "inc" or "+" => MutationOp.Inc,
        "dec" or "-" => MutationOp.Dec,
        "unset" or "del" or "delete" => MutationOp.Unset,
        _ => throw new PatchParseException($"Unknown operation op '{op}'. Use set, inc, dec, unset."),
    };

    private sealed class PatchParseException(string message) : Exception(message);
}
