using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace NivalisCoop.Net;

/// <summary>Wire format, identical to server/nivalis_coop/protocol.py: 4 byte big-endian length + one JSON object with a "t" field.</summary>
public static class Protocol
{
    public const int Version = 1;
    public const int MaxFrameFromServer = 8 * 1024 * 1024;
    public const int MaxFrameToServer = 256 * 1024;

    public static byte[] Encode(JsonObject msg)
    {
        byte[] body = Encoding.UTF8.GetBytes(msg.ToJsonString());
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    public static async Task<JsonObject> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await ReadExactAsync(stream, header, ct).ConfigureAwait(false);
        uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length == 0 || length > MaxFrameFromServer)
            throw new ProtocolException($"bad frame length {length}");
        var body = new byte[length];
        await ReadExactAsync(stream, body, ct).ConfigureAwait(false);
        JsonObject? obj;
        try { obj = JsonNode.Parse(body) as JsonObject; }
        catch (System.Text.Json.JsonException) { throw new ProtocolException("invalid json"); }
        if (obj is null || obj["t"] is not JsonValue t || !t.TryGetValue<string>(out _))
            throw new ProtocolException("frame has no packet type");
        return obj;
    }

    static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int done = 0;
        while (done < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(done), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("Connection closed");
            done += n;
        }
    }
}

public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }
}
