using System.Text.Json.Serialization;

namespace Entanglo.Core.Protocol.Payloads;

public record UrlPushPayload(
    [property: JsonPropertyName("url")] string Url
);
