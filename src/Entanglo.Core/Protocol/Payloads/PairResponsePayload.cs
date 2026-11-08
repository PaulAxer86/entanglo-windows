using System.Text.Json.Serialization;

namespace Entanglo.Core.Protocol.Payloads;

public record PairResponsePayload(
    [property: JsonPropertyName("accepted")]        bool Accepted,
    [property: JsonPropertyName("trustedDeviceId")] string? TrustedDeviceId,
    [property: JsonPropertyName("rejectionReason")] string? RejectionReason
);
