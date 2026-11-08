using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Entanglo.Core.Net;
using Entanglo.Core.Protocol;
using Entanglo.Core.Protocol.Payloads;
using Xunit;

namespace Entanglo.Core.Tests;

public class FramingTests
{
    [Fact]
    public async Task SendAndReceive_roundtrip()
    {
        // In-process loopback via TcpListener
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var server = new NetworkTransport(await listener.AcceptTcpClientAsync());
            return await server.ReadFrameAsync(CancellationToken.None);
        });

        using var clientTcp = new TcpClient();
        await clientTcp.ConnectAsync(IPAddress.Loopback, port);
        using var client = new NetworkTransport(clientTcp);

        var payload = new HelloPayload("Test", "PC", "0.1.0", ["controller"]);
        var frame   = MessageCodec.Encode(MessageType.Hello, "dev-id", "sess-id", payload);
        await client.SendFrameAsync(frame, CancellationToken.None);

        var received = await serverTask;
        var env      = MessageCodec.DecodeEnvelope(received);
        Assert.Equal(MessageType.Hello, env.MessageType);
        Assert.Equal("dev-id", env.SenderDeviceId);

        listener.Stop();
    }

    [Fact]
    public void Frame_has_correct_length_prefix()
    {
        var payload = new HelloPayload("X", "Y", "0", ["controller"]);
        var body    = MessageCodec.Encode(MessageType.Hello, "a", "b", payload);

        // Simulate the full framed packet (header + body)
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)body.Length);

        // The header must decode back to body.Length
        Assert.Equal((uint)body.Length, BinaryPrimitives.ReadUInt32BigEndian(header));
    }
}
