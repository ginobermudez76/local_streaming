using System;
using System.Buffers.Binary;
using LanScreenShare.Core.Security;

namespace LanScreenShare.Network.Security;

public readonly record struct ChannelAttachmentPayload(
    string SessionId,
    string TokenId,
    ChannelType Channel
);

/// <summary>
/// Encapsula el mensaje binario de fijación de canal para puertos secundarios (Audio 5002, Control 5003).
/// Especificación: ADR-001 Sección 8.
/// Estructura fija de 37 bytes:
/// 0..3:   Magic 0x4C534154 ("LSAT")
/// 4..19:  SessionId (16 bytes GUID)
/// 20..35: TokenId (16 bytes crudos derivados del hex de 32 caracteres)
/// 36:     ChannelType (1 = Video, 2 = Audio, 3 = RemoteControl)
/// </summary>
public static class ChannelAttachmentMessage
{
    public const uint Magic = 0x4C534154; // "LSAT"
    public const int MessageSize = 37;

    public static byte[] Serialize(string sessionId, string tokenId, ChannelType channel)
    {
        byte[] buffer = new byte[MessageSize];
        Span<byte> span = buffer.AsSpan();

        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0, 4), Magic);
        WriteSessionGuid(span.Slice(4, 16), sessionId);
        WriteTokenBytes(span.Slice(20, 16), tokenId);
        span[36] = MapChannelToByte(channel);

        return buffer;
    }

    private static void WriteSessionGuid(Span<byte> destination, string sessionId)
    {
        if (!Guid.TryParse(sessionId, out var sessionGuid))
        {
            throw new ArgumentException("SessionId debe ser un GUID válido.", nameof(sessionId));
        }
        sessionGuid.TryWriteBytes(destination);
    }

    private static void WriteTokenBytes(Span<byte> destination, string tokenId)
    {
        byte[] tokenBytes = Convert.FromHexString(tokenId);
        if (tokenBytes.Length != 16)
        {
            throw new ArgumentException("TokenId debe corresponder a 16 bytes (32 caracteres hexadecimales).", nameof(tokenId));
        }
        tokenBytes.CopyTo(destination);
    }

    private static byte MapChannelToByte(ChannelType channel) => channel switch
    {
        ChannelType.Video => 1,
        ChannelType.Audio => 2,
        ChannelType.RemoteControl => 3,
        ChannelType.FileTransfer => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(channel))
    };

    public static bool TryDeserialize(
        ReadOnlySpan<byte> data,
        out ChannelAttachmentPayload payload,
        out string? error)
    {
        payload = default;
        if (!ValidateHeader(data, out error))
        {
            return false;
        }

        string sessionId = new Guid(data.Slice(4, 16)).ToString("D");
        string tokenId = Convert.ToHexString(data.Slice(20, 16)).ToLowerInvariant();

        if (!TryParseChannelType(data[36], out var channel, out error))
        {
            return false;
        }

        payload = new ChannelAttachmentPayload(sessionId, tokenId, channel);
        return true;
    }

    private static bool ValidateHeader(ReadOnlySpan<byte> data, out string? error)
    {
        if (data.Length < MessageSize)
        {
            error = $"Attachment frame too short: expected {MessageSize} bytes, got {data.Length}";
            return false;
        }

        uint magic = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(0, 4));
        if (magic != Magic)
        {
            error = $"Invalid attachment magic: 0x{magic:X8}";
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryParseChannelType(byte channelByte, out ChannelType channel, out string? error)
    {
        channel = channelByte switch
        {
            1 => ChannelType.Video,
            2 => ChannelType.Audio,
            3 => ChannelType.RemoteControl,
            4 => ChannelType.FileTransfer,
            _ => (ChannelType)255
        };

        if ((int)channel == 255)
        {
            error = $"Unknown channel type byte: {channelByte}";
            return false;
        }

        error = null;
        return true;
    }
}
