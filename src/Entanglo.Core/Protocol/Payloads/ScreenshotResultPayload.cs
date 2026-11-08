using System.Text.Json.Serialization;

namespace Entanglo.Core.Protocol.Payloads;

public record ScreenshotResultPayload(
    [property: JsonPropertyName("requestId")] string RequestId,
    [property: JsonPropertyName("pngData")]   string? PngData,
    [property: JsonPropertyName("error")]     string? Error
);
