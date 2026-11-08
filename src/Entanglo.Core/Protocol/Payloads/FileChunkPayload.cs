using System.Text.Json.Serialization;

namespace Entanglo.Core.Protocol.Payloads;

public record FileChunkPayload(
    [property: JsonPropertyName("transferId")] string TransferId,
    [property: JsonPropertyName("sequence")]   int Sequence,
    [property: JsonPropertyName("data")]       string Data  // base64-encoded bytes
);
