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
    /// <paramref name="field"/>. 404 if the document doesn't exist.
    ///
    /// <para>Issue #71: if the request carries a <c>Content-Range</c> header the
    /// upload is treated as one chunk of a resumable session (see
    /// <see cref="UploadChunk"/>); otherwise it's a single-shot streamed
    /// upload.</para></summary>
    public static async Task<IResult> Upload(
        DocumentForgeDb db, BlobStore store, string collection, string id, string field, HttpRequest request)
    {
        if (request.Headers.ContainsKey("Content-Range"))
            return await UploadChunk(db, store, collection, id, field, request);

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

        return RecordDescriptor(db, collection, docId, id, field, blobId, length, mime);
    }

    /// <summary>
    /// Issue #71 — one chunk of a resumable upload. The request carries
    /// <c>Content-Range: bytes {start}-{end}/{total}</c>; chunks are appended in
    /// order to a per-attachment session file until it reaches <c>total</c>
    /// bytes, at which point the assembled file is finalised into the
    /// content-addressed store exactly like a single-shot upload. A client that
    /// lost its connection sends <c>Content-Range: bytes * /{total}</c> to learn
    /// how many bytes are already stored and resumes from there.
    /// <list type="bullet">
    ///   <item>202 + <c>{ received, total, complete:false }</c> — chunk stored, more expected.</item>
    ///   <item>200 + descriptor — final chunk stored, blob recorded on the document.</item>
    ///   <item>409 + <c>{ expectedOffset }</c> — the chunk didn't start at the current offset; resume from there.</item>
    /// </list>
    /// </summary>
    public static async Task<IResult> UploadChunk(
        DocumentForgeDb db, BlobStore store, string collection, string id, string field, HttpRequest request)
    {
        if (!Guid.TryParse(id, out var guid))
            return Results.BadRequest(new { error = "Expected DocumentForge's internal _id (a GUID)." });
        if (string.IsNullOrWhiteSpace(field) || field.Contains('.'))
            return Results.BadRequest(new { error = "Blob field must be a non-empty top-level field name." });

        if (!TryParseContentRange(request.Headers["Content-Range"].ToString(), out bool statusProbe, out long start, out long total))
            return Results.BadRequest(new { error = "Malformed Content-Range. Use 'bytes {start}-{end}/{total}' to send a chunk, or 'bytes */{total}' to query resume offset." });

        var coll = db.GetCollection(collection);
        var docId = new DocumentId(guid);
        if (coll?.FindById(docId) is null) return Results.NotFound(new { error = "Document not found." });

        var uploads = new ResumableUploadStore(store.DataPath);
        var sessionKey = ResumableUploadStore.SessionKey(collection, id, field);

        // Status probe: report how many bytes are already durably received.
        if (statusProbe)
        {
            long have = uploads.ReceivedBytes(sessionKey);
            SetResumeHeaders(request, have);
            return Results.Json(new { received = have, total, complete = false }, statusCode: StatusCodes.Status200OK);
        }

        var result = await uploads.AppendChunkAsync(sessionKey, start, total, request.Body, request.HttpContext.RequestAborted);
        switch (result.Status)
        {
            case ResumableUploadStore.ChunkStatus.OffsetMismatch:
                SetResumeHeaders(request, result.Received);
                return Results.Json(new
                {
                    error = "Chunk offset mismatch — resume from expectedOffset.",
                    code = "offsetMismatch",
                    expectedOffset = result.Received,
                    received = result.Received,
                    total,
                }, statusCode: StatusCodes.Status409Conflict);

            case ResumableUploadStore.ChunkStatus.Overflow:
                return Results.BadRequest(new { error = "Uploaded more bytes than the declared total; the session was discarded — restart the upload." });

            case ResumableUploadStore.ChunkStatus.Partial:
                SetResumeHeaders(request, result.Received);
                return Results.Json(new { received = result.Received, total, complete = false }, statusCode: StatusCodes.Status202Accepted);

            default: // Complete — finalise the assembled file into the store.
            {
                string blobId; long length;
                using (var assembled = new FileStream(result.PartPath!, FileMode.Open, FileAccess.Read, FileShare.None, 1 << 20, FileOptions.SequentialScan))
                    (blobId, length) = await store.PutAsync(assembled, request.HttpContext.RequestAborted);
                uploads.Discard(sessionKey); // bytes are now in the content-addressed store
                return RecordDescriptor(db, collection, docId, id, field, blobId, length, request.ContentType);
            }
        }
    }

    /// <summary>Re-read the document under current state and record the small
    /// <c>$blob</c> descriptor via the normal write path — so schema, <c>_etag</c>
    /// and replication of the DESCRIPTOR all apply and the engine never sees the
    /// bytes. Shared by single-shot and resumable uploads.</summary>
    private static IResult RecordDescriptor(
        DocumentForgeDb db, string collection, DocumentId docId, string id, string field,
        string blobId, long length, string? mime)
    {
        var coll = db.GetCollection(collection);
        var doc = coll?.FindById(docId);
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

    private static void SetResumeHeaders(HttpRequest request, long received)
    {
        // Tell the client how much is stored so it can resume. Range echoes the
        // stored span; a 0-byte session omits it (an empty range is invalid).
        var resp = request.HttpContext.Response;
        if (received > 0) resp.Headers["Range"] = $"bytes=0-{received - 1}";
    }

    /// <summary>Parse a <c>Content-Range</c> upload header. Accepts
    /// <c>bytes {start}-{end}/{total}</c> (a chunk) and <c>bytes */{total}</c>
    /// (a status probe). A <c>*</c> total is rejected — the total must be known
    /// so the server can tell when the upload is complete.</summary>
    internal static bool TryParseContentRange(string header, out bool statusProbe, out long start, out long total)
    {
        statusProbe = false; start = 0; total = 0;
        const string prefix = "bytes ";
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var spec = header.Substring(prefix.Length).Trim();
        int slash = spec.LastIndexOf('/');
        if (slash < 0) return false;
        var rangePart = spec[..slash].Trim();
        var totalPart = spec[(slash + 1)..].Trim();
        if (totalPart == "*") return false; // unknown total unsupported
        if (!long.TryParse(totalPart, out total) || total <= 0) return false;

        if (rangePart == "*") { statusProbe = true; return true; }

        int dash = rangePart.IndexOf('-');
        if (dash < 0) return false;
        if (!long.TryParse(rangePart[..dash], out start) || start < 0 || start >= total) return false;
        if (!long.TryParse(rangePart[(dash + 1)..], out long end) || end < start || end >= total) return false;
        return true;
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
