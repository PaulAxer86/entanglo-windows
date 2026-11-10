using System.Text.Json.Serialization;

namespace Entanglo.Core.Protocol.Payloads;

public record HelloPayload(
    [property: JsonPropertyName("deviceName")]   string DeviceName,
    [property: JsonPropertyName("deviceModel")]  string DeviceModel,
    [property: JsonPropertyName("appVersion")]   string AppVersion,
    [property: JsonPropertyName("roles")]        string[] Roles,
    [property: JsonPropertyName("platform")]     string? Platform    = null,
    [property: JsonPropertyName("screenWidth")]  int?    ScreenWidth  = null,
    [property: JsonPropertyName("screenHeight")] int?    ScreenHeight = null
);
