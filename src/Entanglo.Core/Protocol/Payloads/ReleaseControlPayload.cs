using System.Text.Json.Serialization;

namespace Entanglo.Core.Protocol.Payloads;

public record ReleaseControlPayload(
    [property: JsonPropertyName("reason")] string Reason = "local_input"
);
