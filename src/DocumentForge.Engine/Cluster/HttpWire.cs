using System.Text.Json;
using DocumentForge.Core;
using DocumentForge.Document;

namespace DocumentForge.Engine.Cluster;

/// <summary>
/// JSON DTOs and (de)serializers for the 2PC wire protocol carried by
/// <see cref="HttpShardTransport"/>. The same types are consumed by the
/// REST endpoints in <c>samples/DocumentForge.Api/Program.cs</c>.
///
/// <para>
/// Wire shape for a single op:
/// <code>
/// // Insert
/// { "kind":"Insert", "collection":"orders", "doc":{...} }
/// // Replace
/// { "kind":"Replace", "collection":"orders", "docId":"…", "doc":{...} }
/// // DeleteByField
/// { "kind":"DeleteByField", "collection":"orders", "field":"status", "value":"CANCELLED" }
/// </code>
/// </para>
/// </summary>
public static class HttpWire
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static ShardTxOpDto ToDto(ShardTxOp op) => op.Kind switch
    {
        ShardTxOpKind.Insert => new ShardTxOpDto
        {
            Kind = "Insert",
            Collection = op.Collection,
            Doc = JsonDocument.Parse(op.Doc!.ToJson()).RootElement
        },
        ShardTxOpKind.Replace => new ShardTxOpDto
        {
            Kind = "Replace",
            Collection = op.Collection,
            DocId = op.DocId.ToString(),
            Doc = JsonDocument.Parse(op.Doc!.ToJson()).RootElement
        },
        ShardTxOpKind.DeleteByField => new ShardTxOpDto
        {
            Kind = "DeleteByField",
            Collection = op.Collection,
            Field = op.Field,
            Value = op.Value
        },
        _ => throw new InvalidOperationException($"Unsupported ShardTxOpKind {op.Kind}")
    };

    public static ShardTxOp FromDto(ShardTxOpDto dto)
    {
        return dto.Kind switch
        {
            "Insert" => ShardTxOp.ForInsert(
                dto.Collection,
                BsonDocument.FromJson(dto.Doc!.Value.GetRawText())),
            "Replace" => ShardTxOp.ForReplace(
                dto.Collection,
                new DocumentId(Guid.Parse(dto.DocId!)),
                BsonDocument.FromJson(dto.Doc!.Value.GetRawText())),
            "DeleteByField" => ShardTxOp.ForDeleteByField(
                dto.Collection,
                dto.Field!,
                dto.Value!),
            _ => throw new InvalidDataException($"Unknown ShardTxOpKind '{dto.Kind}'")
        };
    }

    public static IReadOnlyList<ShardTxOp> FromDtos(IReadOnlyList<ShardTxOpDto> dtos) =>
        dtos.Select(FromDto).ToList();

    public static IReadOnlyList<ShardTxOpDto> ToDtos(IReadOnlyList<ShardTxOp> ops) =>
        ops.Select(ToDto).ToList();
}

public sealed class ShardTxOpDto
{
    public string Kind { get; set; } = "";
    public string Collection { get; set; } = "";
    public JsonElement? Doc { get; set; }
    public string? DocId { get; set; }
    public string? Field { get; set; }
    public string? Value { get; set; }
}

// --- Request / response shapes ---

public sealed class ExecuteTransactionRequest
{
    public List<ShardTxOpDto> Ops { get; set; } = new();
}

public sealed class PrepareRequest
{
    public string TxId { get; set; } = "";
    public string CoordinatorShardId { get; set; } = "";
    public List<ShardTxOpDto> Ops { get; set; } = new();
    public long TimeoutMs { get; set; } = 30000;
}

public sealed class PrepareResponse
{
    public string Vote { get; set; } = "";   // "Prepared" | "Aborted"
    public string? AbortReason { get; set; }
}

public sealed class CoordinatorDecisionRequest
{
    public bool Commit { get; set; }
}

public sealed class InFlightPreparedResponse
{
    public List<PreparedTxRecordDto> Records { get; set; } = new();
}

public sealed class PreparedTxRecordDto
{
    public string TxId { get; set; } = "";
    public string CoordinatorShardId { get; set; } = "";
    public List<ShardTxOpDto> Ops { get; set; } = new();
}

public sealed class CoordinatorTxStateDto
{
    public string TxId { get; set; } = "";
    public bool Decided { get; set; }
    public bool Done { get; set; }
}
