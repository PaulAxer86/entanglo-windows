using System.Text.Json.Serialization;

namespace Entanglo.Core.Protocol.Payloads;

public record FileAckPayload(
    [property: JsonPropertyName("transferId")]   string TransferId,
    [property: JsonPropertyName("ok")]           bool Ok,
    [property: JsonPropertyName("failureReason")] string? FailureReason
);
