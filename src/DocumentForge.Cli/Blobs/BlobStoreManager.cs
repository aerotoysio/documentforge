using System.Collections.Concurrent;
using System.Text.Json;
using DocumentForge.Engine;

namespace DocumentForge.Cli.Blobs;

/// <summary>
/// Owns one <see cref="BlobStore"/> per database (keyed by the DB's data-file
/// path), lazily opened. The store lives beside the data file as
/// <c>&lt;db&gt;.dfdb.blobs</c>, so it travels with the database but never mixes
/// into it. Also builds/reads the small blob DESCRIPTOR that goes into a
/// document field — the only thing the transactional engine ever sees.
/// </summary>
public sealed class BlobStoreManager : IDisposable
{
    private readonly ConcurrentDictionary<string, BlobStore> _stores = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Marker field inside a blob descriptor. A JSON object with this
    /// key IS a blob reference; anything else is ordinary document data.</summary>
    public const string BlobIdKey = "$blob";

    public BlobStore For(DocumentForgeDb db) => For(db.FilePath);

    public BlobStore For(string dataFilePath) =>
        _stores.GetOrAdd(dataFilePath, p => new BlobStore(p + ".blobs"));

    /// <summary>Render the small, fixed-shape descriptor JSON written into the
    /// document field. Uses the reserved <c>$blob</c> key so reads recognise it.
    /// Staying tiny is what keeps page density, index scans and the replication
    /// op stream untouched (issue #109).</summary>
    public static string DescriptorJson(string id, long length, string? mime)
    {
        var obj = new Dictionary<string, object?>
        {
            [BlobIdKey] = id,
            ["len"] = length,
            ["mime"] = string.IsNullOrWhiteSpace(mime) ? null : mime,
        };
        return JsonSerializer.Serialize(obj);
    }

    /// <summary>If <paramref name="fieldValue"/> is a blob descriptor, pull out
    /// its id/len/mime; else return null. Accepts a JsonElement (from a parsed
    /// document field).</summary>
    public static (string Id, long Length, string? Mime)? TryReadDescriptor(JsonElement fieldValue)
    {
        if (fieldValue.ValueKind != JsonValueKind.Object) return null;
        if (!fieldValue.TryGetProperty(BlobIdKey, out var idEl) || idEl.ValueKind != JsonValueKind.String)
            return null;
        var id = idEl.GetString()!;
        long len = fieldValue.TryGetProperty("len", out var lenEl) && lenEl.ValueKind == JsonValueKind.Number
            ? lenEl.GetInt64() : 0;
        string? mime = fieldValue.TryGetProperty("mime", out var mimeEl) && mimeEl.ValueKind == JsonValueKind.String
            ? mimeEl.GetString() : null;
        return (id, len, mime);
    }

    /// <summary>Collect every blob id referenced by any document in the DB —
    /// the "mark" set for a mark-sweep GC. Walks documents once, out of band.</summary>
    public static HashSet<string> CollectReferencedBlobIds(DocumentForgeDb db)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var collName in db.GetCollectionNames())
        {
            var coll = db.GetCollection(collName);
            if (coll is null) continue;
            foreach (var doc in coll.FindAll())
            {
                using var parsed = JsonDocument.Parse(doc.ToJson());
                ScanForBlobIds(parsed.RootElement, referenced);
            }
        }
        return referenced;
    }

    private static void ScanForBlobIds(JsonElement el, HashSet<string> into)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                var desc = TryReadDescriptor(el);
                if (desc is not null) { into.Add(desc.Value.Id); return; }
                foreach (var prop in el.EnumerateObject()) ScanForBlobIds(prop.Value, into);
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) ScanForBlobIds(item, into);
                break;
        }
    }

    public void Dispose()
    {
        foreach (var s in _stores.Values) { try { s.Dispose(); } catch { } }
        _stores.Clear();
    }
}
