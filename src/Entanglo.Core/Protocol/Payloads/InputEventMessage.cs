using System.Text.Json.Serialization;

namespace Entanglo.Core.Protocol.Payloads;

public record InputEventMessage(
    [property: JsonPropertyName("kind")]          string Kind,
    [property: JsonPropertyName("x")]             double? X,
    [property: JsonPropertyName("y")]             double? Y,
    [property: JsonPropertyName("deltaX")]        double? DeltaX,
    [property: JsonPropertyName("deltaY")]        double? DeltaY,
    [property: JsonPropertyName("button")]        int? Button,
    [property: JsonPropertyName("keyCode")]       int? KeyCode,
    [property: JsonPropertyName("mediaKey")]      string? MediaKey,
    [property: JsonPropertyName("modifierFlags")] ulong ModifierFlags,
    [property: JsonPropertyName("pressed")]       bool? Pressed,
    [property: JsonPropertyName("clickState")]    int? ClickState
);
