using Entanglo.Core.Protocol;
using Entanglo.Core.Protocol.Payloads;
using Xunit;

namespace Entanglo.Core.Tests;

public class ProtocolRoundtripTests
{
    const string DeviceId = "A56B8029-C69E-4D97-8589-99C81524093B";
    const string SessionId = "5F2C9C0A-AA31-4D80-BCEA-2C7B0E0D0AFB";

    [Fact]
    public void Hello_roundtrips()
    {
        var payload = new HelloPayload("WinDev", "PC", "0.1.0", ["controller", "receiver"]);
        var bytes = MessageCodec.Encode(MessageType.Hello, DeviceId, SessionId, payload);
        var env   = MessageCodec.DecodeEnvelope(bytes);
        var back  = MessageCodec.DecodePayload<HelloPayload>(env);

        Assert.Equal(payload.DeviceName,  back.DeviceName);
        Assert.Equal(payload.AppVersion,  back.AppVersion);
        Assert.Equal(payload.Roles,       back.Roles);
        Assert.Equal(MessageType.Hello,   env.MessageType);
        Assert.Equal(1,                   env.ProtocolVersion);
        Assert.Equal(DeviceId,            env.SenderDeviceId);
    }

    [Fact]
    public void Message_type_serializes_as_swift_raw_value()
    {
        var payload = new HeartbeatMessage(1, null, 10, null);
        var json = System.Text.Encoding.UTF8.GetString(
            MessageCodec.Encode(MessageType.Heartbeat, DeviceId, SessionId, payload));

        Assert.Contains("\"messageType\":\"heartbeat\"", json);
        Assert.DoesNotContain("\"messageType\":\"Heartbeat\"", json);
    }

    [Fact]
    public void Heartbeat_roundtrips()
    {
        var payload = new HeartbeatMessage(42, 1.8, 27429103.4, 27429102.6);
        var bytes   = MessageCodec.Encode(MessageType.Heartbeat, DeviceId, SessionId, payload);
        var env     = MessageCodec.DecodeEnvelope(bytes);
        var back    = MessageCodec.DecodePayload<HeartbeatMessage>(env);

        Assert.Equal(42L,       back.Sequence);
        Assert.Equal(1.8,       back.LastRttMs);
    }

    [Fact]
    public void InputEvent_roundtrips()
    {
        var payload = new InputEventMessage(
            Kind: "mouseMove", X: 1042.0, Y: 318.0,
            DeltaX: null, DeltaY: null, Button: null, KeyCode: null,
            MediaKey: null, ModifierFlags: 0, Pressed: null, ClickState: null);
        var bytes = MessageCodec.Encode(MessageType.InputEvent, DeviceId, SessionId, payload);
        var env   = MessageCodec.DecodeEnvelope(bytes);
        var back  = MessageCodec.DecodePayload<InputEventMessage>(env);

        Assert.Equal("mouseMove", back.Kind);
        Assert.Equal(1042.0, back.X);
        Assert.Equal(318.0,  back.Y);
    }

    [Fact]
    public void PairRequest_pinHash_defaults_to_empty()
    {
        var payload = new PairRequestPayload(DeviceId, "WinDev");
        Assert.Equal("", payload.PinHash);
        var bytes = MessageCodec.Encode(MessageType.PairRequest, DeviceId, SessionId, payload);
        var back  = MessageCodec.DecodePayload<PairRequestPayload>(
                        MessageCodec.DecodeEnvelope(bytes));
        Assert.Equal("", back.PinHash);
    }

    [Fact]
    public void Unknown_protocol_version_throws()
    {
        // Craft a frame with version 99
        const string json = """{"protocolVersion":99,"messageType":"hello","senderDeviceId":"x","sessionId":"y","timestamp":0,"payload":"e30="}""";
        var data = System.Text.Encoding.UTF8.GetBytes(json);
        Assert.Throws<InvalidOperationException>(() => MessageCodec.DecodeEnvelope(data));
    }

    [Fact]
    public void Oversize_frame_throws()
    {
        var data = new byte[MessageCodec.MaxMessageBytes + 1];
        Assert.Throws<InvalidOperationException>(() => MessageCodec.DecodeEnvelope(data));
    }
}
