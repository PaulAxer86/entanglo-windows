using System.Text.Json.Serialization;

namespace Entanglo.Core.Protocol.Payloads;

public record FileOfferPayload(
    [property: JsonPropertyName("transferId")]   string TransferId,
    [property: JsonPropertyName("name")]         string Name,
    [property: JsonPropertyName("sizeBytes")]    long SizeBytes,
    [property: JsonPropertyName("totalChunks")]  int TotalChunks,
    [property: JsonPropertyName("chunkSize")]    int ChunkSize,
    [property: JsonPropertyName("kind")]         string? Kind
);
