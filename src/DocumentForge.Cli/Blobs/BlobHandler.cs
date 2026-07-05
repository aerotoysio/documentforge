using System.Text.Json;
using DocumentForge.Core;
using DocumentForge.Document;
using DocumentForge.Engine;
using Microsoft.AspNetCore.Http;

namespace DocumentForge.Cli.Blobs;

/// <summary>
/// Issues #109 / #71 — HTTP surface for out-of-line blobs, shared by the flat
/// and scoped routes. A blob is uploaded onto a document FIELD (the field name
/// doubles as #71's "attachment key"); the field then holds a small
/// <c>$blob</c> descriptor and <c>SELECT *</c> returns that, never the bytes.
/// Uploads and downloads stream (with HTTP Range on read), so a hundred-MB
/// video never sits in memory.
/// </summary>
public static class BlobHandler
{
    /// <summary>PUT — stream the body into the blob store, then record the
    /// descriptor on <paramref name="collection"/>/<paramref name="id"/>'s
    /// <paramref name="field"/>. 404 if the document doesn't exist.</summary>
    public static async Task<IResult> Upload(
        DocumentForgeDb db, BlobStore store, string collection, string id, string field, HttpRequest request)
    {
        if (!Guid.TryParse(id, out var guid))
            return Results.BadRequest(new { error = "Expected DocumentForge's internal _id (a GUID)." });
        if (string.IsNullOrWhiteSpace(field) || field.Contains('.'))
            return Results.BadRequest(new { error = "Blob field must be a non-empty top-level field name." });

        var coll = db.GetCollection(collection);
        var docId = new DocumentId(guid);
        if (coll?.FindById(docId) is null) return Results.NotFound(new { error = "Document not found." });

        // Stage the bytes first (idempotent + fsync'd). If the doc write below
        // fails, the blob is simply orphaned and reclaimed by GC.
        var mime = request.ContentType;
        var (blobId, length) = await store.PutAsync(request.Body, request.HttpContext.RequestAborted);

        // Re-read under the current state (it may have changed) and record the
        // small descriptor via the normal document write path — so schema,
        // _etag and replication of the DESCRIPTOR all apply, and the engine
        // never sees the bytes.
        var doc = coll!.FindById(docId);
        if (doc is null) return Results.NotFound(new { error = "Document not found." });
        var descriptor = BsonDocument.FromJson(BlobStoreManager.DescriptorJson(blobId, length, mime));
        doc[field] = BsonValue.FromDocument(descriptor);
        try
        {
            if (!db.Replace(collection, docId, doc)) return Results.NotFound(new { error = "Document not found." });
        }
        catch (DocumentForgeException ex) { return Results.BadRequest(new { error = ex.Message }); }

        return Results.Ok(new
        {
            success = true,
            collection,
            id,
            field,
            blob = new { id = blobId, len = length, mime },
        });
    }

    /// <summary>GET — stream a blob's bytes back, honouring an HTTP Range
    /// header (206 Partial Content). 404 if the doc/field/blob is missing.</summary>
    public static IResult Download(
        DocumentForgeDb db, BlobStore store, string collection, string id, string field, HttpRequest request, HttpResponse response)
    {
        if (!Guid.TryParse(id, out var guid))
            return Results.BadRequest(new { error = "Expected DocumentForge's internal _id (a GUID)." });

        var coll = db.GetCollection(collection);
        var doc = coll?.FindById(new DocumentId(guid));
        if (doc is null) return Results.NotFound(new { error = "Document not found." });

        using var parsed = JsonDocument.Parse(doc.ToJson());
        if (!parsed.RootElement.TryGetProperty(field, out var fieldEl))
            return Results.NotFound(new { error = $"Field '{field}' is not set on this document." });
        var desc = BlobStoreManager.TryReadDescriptor(fieldEl);
        if (desc is null)
            return Results.NotFound(new { error = $"Field '{field}' is not a blob reference." });

        var total = store.LengthOf(desc.Value.Id);
        if (total is null) return Results.NotFound(new { error = "Blob bytes are not present (collected or not replicated)." });

        var contentType = string.IsNullOrWhiteSpace(desc.Value.Mime) ? "application/octet-stream" : desc.Value.Mime!;
        response.Headers["Accept-Ranges"] = "bytes";

        // Range: bytes=start-end / start- / -suffix
        var rangeHeader = request.Headers["Range"].ToString();
        if (!string.IsNullOrEmpty(rangeHeader) && TryParseRange(rangeHeader, total.Value, out long start, out long count))
        {
            var partial = store.OpenRead(desc.Value.Id, start, count);
            if (partial is null) return Results.NotFound(new { error = "Blob bytes are not present." });
            response.StatusCode = StatusCodes.Status206PartialContent;
            response.Headers["Content-Range"] = $"bytes {start}-{start + count - 1}/{total.Value}";
            return Results.Stream(partial, contentType);
        }

        var stream = store.OpenRead(desc.Value.Id);
        if (stream is null) return Results.NotFound(new { error = "Blob bytes are not present." });
        return Results.Stream(stream, contentType);
    }

    /// <summary>DELETE — remove the blob reference from the document (guarded by
    /// X-Confirm: true). The bytes are reclaimed later by GC.</summary>
    public static IResult Remove(
        DocumentForgeDb db, string collection, string id, string field, HttpRequest request)
    {
        if (request.Headers["X-Confirm"].ToString() != "true")
            return Results.BadRequest(new { error = "Destructive op. Include header 'X-Confirm: true' to proceed." });
        if (!Guid.TryParse(id, out var guid))
            return Results.BadRequest(new { error = "Expected DocumentForge's internal _id (a GUID)." });

        var coll = db.GetCollection(collection);
        var docId = new DocumentId(guid);
        var doc = coll?.FindById(docId);
        if (doc is null) return Results.NotFound(new { error = "Document not found." });
        if (!doc.ContainsKey(field)) return Results.NotFound(new { error = $"Field '{field}' is not set." });

        doc.Remove(field);
        try
        {
            if (!db.Replace(collection, docId, doc)) return Results.NotFound(new { error = "Document not found." });
        }
        catch (DocumentForgeException ex) { return Results.BadRequest(new { error = ex.Message }); }
        return Results.Ok(new { success = true, collection, id, field, removed = true });
    }

    private static bool TryParseRange(string header, long total, out long start, out long count)
    {
        start = 0; count = 0;
        // Only a single range is supported: "bytes=start-end", "bytes=start-", "bytes=-suffix".
        const string prefix = "bytes=";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var spec = header.Substring(prefix.Length).Split(',')[0].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0) return false;
        var left = spec[..dash];
        var right = spec[(dash + 1)..];

        if (left.Length == 0)
        {
            // suffix: last N bytes
            if (!long.TryParse(right, out var suffix) || suffix <= 0) return false;
            suffix = Math.Min(suffix, total);
            start = total - suffix;
            count = suffix;
            return count > 0;
        }
        if (!long.TryParse(left, out start) || start < 0 || start >= total) return false;
        long end = total - 1;
        if (right.Length > 0 && (!long.TryParse(right, out end) || end < start)) return false;
        end = Math.Min(end, total - 1);
        count = end - start + 1;
        return count > 0;
    }
}
