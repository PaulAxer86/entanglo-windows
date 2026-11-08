using System.Text.Json.Serialization;

namespace Entanglo.Core.Protocol.Payloads;

public record PairRequestPayload(
    [property: JsonPropertyName("requesterDeviceId")]   string RequesterDeviceId,
    [property: JsonPropertyName("requesterDeviceName")] string RequesterDeviceName,
    [property: JsonPropertyName("pinHash")]             string PinHash = ""
);
