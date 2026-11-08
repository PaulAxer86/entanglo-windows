using System.Text.Json.Serialization;

namespace Entanglo.Core.Protocol.Payloads;

public record ScreenshotRequestPayload(
    [property: JsonPropertyName("requestId")] string RequestId
);
