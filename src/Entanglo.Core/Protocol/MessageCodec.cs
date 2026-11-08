using System.Text.Json;
using System.Text.Json.Serialization;

namespace Entanglo.Core.Protocol;

public static class MessageCodec
{
    public const int ProtocolVersion = 1;
    public const int MaxMessageBytes = 4 * 1024 * 1024;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
        },
    };

    public static byte[] Encode<TPayload>(
        MessageType type,
        string senderDeviceId,
        string sessionId,
        TPayload payload,
        double? timestampOverride = null)
    {
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        return EncodeRaw(type, senderDeviceId, sessionId, payloadBytes, timestampOverride);
    }

    /// <summary>
    /// Wraps an already-JSON-serialized payload in an envelope. Used by
    /// FileTransferService which builds the payload bytes itself.
    /// </summary>
    public static byte[] EncodeRaw(
        MessageType type,
        string senderDeviceId,
        string sessionId,
        byte[] payloadJson,
        double? timestampOverride = null)
    {
        var envelope = new EntangloMessage(
            ProtocolVersion,
            type,
            senderDeviceId,
            sessionId,
            timestampOverride ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            Convert.ToBase64String(payloadJson));

        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        if (bytes.Length > MaxMessageBytes)
            throw new InvalidOperationException($"Message too large: {bytes.Length} bytes");
        return bytes;
    }

    public static EntangloMessage DecodeEnvelope(byte[] data)
    {
        if (data.Length > MaxMessageBytes)
            throw new InvalidOperationException($"Frame too large: {data.Length} bytes");
        var env = JsonSerializer.Deserialize<EntangloMessage>(data, JsonOptions)
                  ?? throw new InvalidOperationException("Null envelope");
        if (env.ProtocolVersion != ProtocolVersion)
            throw new InvalidOperationException($"Unsupported protocol version: {env.ProtocolVersion}");
        return env;
    }

    public static T DecodePayload<T>(EntangloMessage env)
    {
        var payloadBytes = Convert.FromBase64String(env.Payload);
        return JsonSerializer.Deserialize<T>(payloadBytes, JsonOptions)
               ?? throw new InvalidOperationException("Null payload");
    }
}
