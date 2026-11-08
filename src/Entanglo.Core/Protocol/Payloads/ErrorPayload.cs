using System.Text.Json.Serialization;

namespace Entanglo.Core.Protocol.Payloads;

public record ErrorPayload(
    [property: JsonPropertyName("code")]    string Code,
    [property: JsonPropertyName("message")] string Message
);
