using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using MinecraftAdmin.WebApp.Models;

namespace MinecraftAdmin.WebApp.Services;

public sealed class RconClient(IOptions<MinecraftOptions> options)
{
    private readonly MinecraftOptions options_ = options.Value;
    private int requestId_;

    public async Task<string> ExecuteAsync(string command, CancellationToken cancellationToken = default)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(options_.RconHost, options_.RconPort, cancellationToken);
        await using var stream = client.GetStream();

        var authId = Interlocked.Increment(ref requestId_);
        await WritePacketAsync(stream, authId, 3, options_.RconPassword, cancellationToken);
        var auth = await ReadPacketAsync(stream, cancellationToken);
        if (auth.Id == -1)
            throw new InvalidOperationException("RCON authentication failed.");

        var commandId = Interlocked.Increment(ref requestId_);
        await WritePacketAsync(stream, commandId, 2, command, cancellationToken);
        var response = await ReadPacketAsync(stream, cancellationToken);

        if (response.Id != commandId)
            throw new InvalidDataException("Unexpected RCON response id.");

        return response.Body;
    }

    private static async Task WritePacketAsync(NetworkStream stream, int id, int type, string body, CancellationToken ct)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var length = 4 + 4 + bodyBytes.Length + 2;
        var packet = new byte[4 + length];

        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(0, 4), length);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, 4), id);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), type);
        bodyBytes.CopyTo(packet.AsSpan(12));

        await stream.WriteAsync(packet, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<RconPacket> ReadPacketAsync(NetworkStream stream, CancellationToken ct)
    {
        var lengthBuffer = new byte[4];
        await ReadExactlyAsync(stream, lengthBuffer, ct);
        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);

        if (length < 10 || length > 1024 * 1024)
            throw new InvalidDataException($"Invalid RCON packet length: {length}.");

        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, ct);

        var id = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4));
        var type = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4, 4));
        var bodyLength = Math.Max(0, length - 10);
        var body = Encoding.UTF8.GetString(payload, 8, bodyLength);

        return new RconPacket(id, type, body);
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], ct);
            if (read == 0)
                throw new EndOfStreamException("RCON connection closed unexpectedly.");
            offset += read;
        }
    }

    private sealed record RconPacket(int Id, int Type, string Body);
}
