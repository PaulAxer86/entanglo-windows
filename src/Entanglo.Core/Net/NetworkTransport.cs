using System.Buffers.Binary;
using System.Net.Sockets;
using Entanglo.Core.Protocol;

namespace Entanglo.Core.Net;

/// <summary>Framed TCP transport: 4-byte big-endian length prefix + JSON body.</summary>
public sealed class NetworkTransport : IDisposable
{
    readonly TcpClient _client;
    readonly NetworkStream _stream;
    readonly SemaphoreSlim _writeLock = new(1, 1);
    bool _disposed;

    public NetworkTransport(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public async Task SendFrameAsync(byte[] body, CancellationToken ct)
    {
        if (body.Length > MessageCodec.MaxMessageBytes)
            throw new InvalidOperationException($"Frame too large: {body.Length}");

        await _writeLock.WaitAsync(ct);
        try
        {
            var header = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)body.Length);
            await _stream.WriteAsync(header, ct);
            await _stream.WriteAsync(body, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<byte[]> ReadFrameAsync(CancellationToken ct)
    {
        var header = new byte[4];
        await ReadExactAsync(header, ct);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length == 0 || length > MessageCodec.MaxMessageBytes)
            throw new InvalidOperationException($"Invalid frame length: {length}");
        var body = new byte[length];
        await ReadExactAsync(body, ct);
        return body;
    }

    async Task ReadExactAsync(byte[] buf, CancellationToken ct)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n = await _stream.ReadAsync(buf.AsMemory(read), ct);
            if (n == 0) throw new EndOfStreamException("Connection closed by peer");
            read += n;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writeLock.Dispose();
        _stream.Dispose();
        _client.Dispose();
    }
}
