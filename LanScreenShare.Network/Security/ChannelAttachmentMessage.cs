using System;
using System.Buffers.Binary;
using LanScreenShare.Core.Security;

namespace LanScreenShare.Network.Security;

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

        // 1. Magic
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(0, 4), Magic);

        // 2. SessionId (Guid 16 bytes)
        if (!Guid.TryParse(sessionId, out var sessionGuid))
        {
            throw new ArgumentException("SessionId debe ser un GUID válido.", nameof(sessionId));
        }
        sessionGuid.TryWriteBytes(span.Slice(4, 16));

        // 3. TokenId (Hex 32 chars -> 16 bytes)
        byte[] tokenBytes = Convert.FromHexString(tokenId);
        if (tokenBytes.Length != 16)
        {
            throw new ArgumentException("TokenId debe corresponder a 16 bytes (32 caracteres hexadecimales).", nameof(tokenId));
        }
        tokenBytes.CopyTo(span.Slice(20, 16));

        // 4. ChannelType (1B)
        span[36] = channel switch
        {
            ChannelType.Video => 1,
            ChannelType.Audio => 2,
            ChannelType.RemoteControl => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(channel))
        };

        return buffer;
    }

    public static bool TryDeserialize(
        ReadOnlySpan<byte> data,
        out string sessionId,
        out string tokenId,
        out ChannelType channel,
        out string? error)
    {
        sessionId = string.Empty;
        tokenId = string.Empty;
        channel = ChannelType.Video;
        error = null;

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

        var guidBytes = data.Slice(4, 16);
        sessionId = new Guid(guidBytes).ToString("D");

        var tokenBytes = data.Slice(20, 16);
        tokenId = Convert.ToHexString(tokenBytes).ToLowerInvariant();

        byte channelByte = data[36];
        channel = channelByte switch
        {
            1 => ChannelType.Video,
            2 => ChannelType.Audio,
            3 => ChannelType.RemoteControl,
            _ => (ChannelType)255
        };

        if ((int)channel == 255)
        {
            error = $"Unknown channel type byte: {channelByte}";
            return false;
        }

        return true;
    }
}

