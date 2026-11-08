using System.Text.Json.Serialization;

namespace Entanglo.Core.Protocol.Payloads;

public record HeartbeatMessage(
    [property: JsonPropertyName("sequence")]      long Sequence,
    [property: JsonPropertyName("lastRttMs")]     double? LastRttMs,
    [property: JsonPropertyName("sentAtMs")]      double? SentAtMs,
    [property: JsonPropertyName("echoSentAtMs")] double? EchoSentAtMs
);
