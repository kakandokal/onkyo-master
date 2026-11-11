using System.Buffers.Binary;
using System.Text;

namespace OnkyoMaster.Protocol;

/// <summary>
/// eISCP framing: a 16-byte header ("ISCP", header size, data size, version) followed by
/// the ISCP message "!1CMDparam" and an end marker.
/// </summary>
public static class EiscpPacket
{
    public const int Port = 60128;

    private const int HeaderSize = 16;
    private const int MaxDataSize = 4 * 1024 * 1024;

    public static byte[] Encode(string command, char unitType = '1')
    {
        var payload = Encoding.UTF8.GetBytes($"!{unitType}{command}\r");
        var packet = new byte[HeaderSize + payload.Length];
        "ISCP"u8.CopyTo(packet);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(4), HeaderSize);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8), payload.Length);
        packet[12] = 0x01;
        payload.CopyTo(packet, HeaderSize);
        return packet;
    }

    /// <summary>
    /// Takes one message off the front of <paramref name="buffer"/>.
    /// Returns false when more bytes are needed. When it returns true, <paramref name="consumed"/> bytes
    /// must be dropped; <paramref name="message"/> is null if those bytes were garbage.
    /// The returned message has the "!1" prefix and the end marker removed.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> buffer, out string? message, out int consumed)
    {
        message = null;
        consumed = 0;
        if (buffer.Length < HeaderSize) return false;

        if (!buffer.StartsWith("ISCP"u8))
        {
            // Resynchronise on the next magic; keep the tail in case the magic is split across reads.
            var next = buffer[1..].IndexOf("ISCP"u8);
            consumed = next < 0 ? buffer.Length - 3 : next + 1;
            return true;
        }

        var headerSize = BinaryPrimitives.ReadInt32BigEndian(buffer[4..]);
        var dataSize = BinaryPrimitives.ReadInt32BigEndian(buffer[8..]);
        if (headerSize < HeaderSize || headerSize > 1024 || dataSize < 0 || dataSize > MaxDataSize)
        {
            consumed = 4;
            return true;
        }

        if (buffer.Length < headerSize + dataSize) return false;

        // The end marker varies: EOF (0x1A) over TCP, 0x19 in discovery replies, optionally followed by CR/LF.
        var text = Encoding.UTF8.GetString(buffer.Slice(headerSize, dataSize));
        var end = text.Length;
        while (end > 0 && text[end - 1] < ' ') end--;
        text = text[..end];
        consumed = headerSize + dataSize;
        message = text.Length >= 2 && text[0] == '!' ? text[2..] : text;
        return true;
    }
}
