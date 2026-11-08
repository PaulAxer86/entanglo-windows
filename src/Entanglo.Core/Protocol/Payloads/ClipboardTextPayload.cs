using System.Text.Json.Serialization;

namespace Entanglo.Core.Protocol.Payloads;

public record ClipboardTextPayload(
    [property: JsonPropertyName("text")] string Text
);
