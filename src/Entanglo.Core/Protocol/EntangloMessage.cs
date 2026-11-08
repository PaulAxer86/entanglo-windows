using System.Text.Json.Serialization;

namespace Entanglo.Core.Protocol;

public record EntangloMessage(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("messageType")]     MessageType MessageType,
    [property: JsonPropertyName("senderDeviceId")] string SenderDeviceId,
    [property: JsonPropertyName("sessionId")]      string SessionId,
    [property: JsonPropertyName("timestamp")]      double Timestamp,
    [property: JsonPropertyName("payload")]        string Payload
);
