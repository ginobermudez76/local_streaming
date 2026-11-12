using System;
using System.Buffers.Binary;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Network.Security;

public readonly record struct Lsi2PacketHeader(ulong SequenceNumber, ulong Timestamp);

/// <summary>
/// Encapsula la serialización y deserialización del paquete de control remoto LSI2 (28 bytes).
/// Especificación: ADR-001 Sección 10.
/// </summary>
public sealed class Lsi2ControlPacket
{
    public const uint MagicLsi2 = 0x4C534932; // "LSI2"
    public const uint MagicLsi1Legacy = 0x4C534931; // "LSI1"
    public const int PacketSize = 28;
    public const int LegacyPacketSize = 20;
    public const int MaxClipboardPayloadBytes = 256 * 1024; // 256 KB

    public ulong SequenceNumber { get; set; }
    public ulong Timestamp { get; set; }
    public InputEventType EventType { get; set; }
    public RemoteMouseButton Button { get; set; }
    public short WheelDelta { get; set; }
    public int KeyCode { get; set; }
    public float NormalizedX { get; set; }
    public float NormalizedY { get; set; }

    /// <summary>
    /// Serializa el paquete LSI2 en el búfer proporcionado (debe ser de al menos 28 bytes).
    /// </summary>
    public static void WritePacket(
        Span<byte> destination,
        ulong sequenceNumber,
        ulong timestamp,
        RemoteInputMessage input)
    {
        if (destination.Length < PacketSize)
            throw new ArgumentException($"El destino debe tener al menos {PacketSize} bytes.", nameof(destination));

        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(0, 4), MagicLsi2);
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(4, 8), sequenceNumber);
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(12, 8), timestamp);

        destination[20] = (byte)input.Type;
        destination[21] = (byte)input.Button;
        BinaryPrimitives.WriteInt16BigEndian(destination.Slice(22, 2), input.WheelDelta);

        WritePayloadField(destination.Slice(24, 4), input);
    }

    private static void WritePayloadField(Span<byte> destination, RemoteInputMessage input)
    {
        if (IsKeyOrCommandEvent(input.Type))
        {
            BinaryPrimitives.WriteInt32BigEndian(destination, input.KeyCode);
            return;
        }

        if (input.Type == InputEventType.MouseMoveRelative)
        {
            WriteRelativeCoordinates(destination, input.NormalizedX, input.NormalizedY);
            return;
        }

        WriteAbsoluteCoordinates(destination, input.NormalizedX, input.NormalizedY);
    }

    private static void WriteRelativeCoordinates(Span<byte> destination, float x, float y)
    {
        short relX = (short)Math.Clamp((int)Math.Round(x), short.MinValue, short.MaxValue);
        short relY = (short)Math.Clamp((int)Math.Round(y), short.MinValue, short.MaxValue);
        BinaryPrimitives.WriteInt16BigEndian(destination.Slice(0, 2), relX);
        BinaryPrimitives.WriteInt16BigEndian(destination.Slice(2, 2), relY);
    }

    private static void WriteAbsoluteCoordinates(Span<byte> destination, float x, float y)
    {
        if (x < 0f || y < 0f)
        {
            BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(0, 2), 0xFFFF);
            BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(2, 2), 0xFFFF);
            return;
        }

        ushort packedX = (ushort)Math.Clamp((int)Math.Round(x * 65535f), 0, 65535);
        ushort packedY = (ushort)Math.Clamp((int)Math.Round(y * 65535f), 0, 65535);
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(0, 2), packedX);
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(2, 2), packedY);
    }

    public static bool TryReadPacket(
        ReadOnlySpan<byte> source,
        out Lsi2PacketHeader header,
        out RemoteInputMessage? inputMessage,
        out string? protocolError)
    {
        header = default;
        inputMessage = null;

        if (!ValidatePacketHeader(source, out protocolError))
        {
            return false;
        }

        ulong sequenceNumber = BinaryPrimitives.ReadUInt64BigEndian(source.Slice(4, 8));
        ulong timestamp = BinaryPrimitives.ReadUInt64BigEndian(source.Slice(12, 8));
        header = new Lsi2PacketHeader(sequenceNumber, timestamp);

        var eventType = (InputEventType)source[20];
        var button = (RemoteMouseButton)source[21];
        short wheelDelta = BinaryPrimitives.ReadInt16BigEndian(source.Slice(22, 2));

        inputMessage = ReadInputMessage(source.Slice(24, 4), eventType, button, wheelDelta);
        return true;
    }

    private static bool ValidatePacketHeader(ReadOnlySpan<byte> source, out string? protocolError)
    {
        if (source.Length < 4)
        {
            protocolError = "PacketTooShort";
            return false;
        }

        uint magic = BinaryPrimitives.ReadUInt32BigEndian(source.Slice(0, 4));
        if (magic == MagicLsi1Legacy)
        {
            protocolError = "BadProtocolVersion: LSI1 legacy clients are strictly rejected (ADR-001 Sec 10)";
            return false;
        }

        if (magic != MagicLsi2)
        {
            protocolError = $"InvalidMagic: 0x{magic:X8}";
            return false;
        }

        if (source.Length < PacketSize)
        {
            protocolError = $"IncompletePacket: expected {PacketSize} bytes, got {source.Length}";
            return false;
        }

        protocolError = null;
        return true;
    }

    private static RemoteInputMessage ReadInputMessage(
        ReadOnlySpan<byte> payloadSlice,
        InputEventType eventType,
        RemoteMouseButton button,
        short wheelDelta)
    {
        int keyCode = 0;
        float normX = 0f;
        float normY = 0f;

        if (IsKeyOrCommandEvent(eventType))
        {
            keyCode = BinaryPrimitives.ReadInt32BigEndian(payloadSlice);
        }
        else if (eventType == InputEventType.MouseMoveRelative)
        {
            normX = BinaryPrimitives.ReadInt16BigEndian(payloadSlice.Slice(0, 2));
            normY = BinaryPrimitives.ReadInt16BigEndian(payloadSlice.Slice(2, 2));
        }
        else
        {
            (normX, normY) = ReadAbsoluteCoordinates(payloadSlice);
        }

        return new RemoteInputMessage
        {
            Type = eventType,
            Button = button,
            WheelDelta = wheelDelta,
            KeyCode = keyCode,
            NormalizedX = normX,
            NormalizedY = normY
        };
    }

    private static (float X, float Y) ReadAbsoluteCoordinates(ReadOnlySpan<byte> slice)
    {
        ushort packedX = BinaryPrimitives.ReadUInt16BigEndian(slice.Slice(0, 2));
        ushort packedY = BinaryPrimitives.ReadUInt16BigEndian(slice.Slice(2, 2));

        if (packedX == 0xFFFF && packedY == 0xFFFF)
        {
            return (-1f, -1f);
        }

        return (packedX / 65535f, packedY / 65535f);
    }

    private static bool IsKeyOrCommandEvent(InputEventType type) =>
        type is InputEventType.KeyDown or
                InputEventType.KeyUp or
                InputEventType.ClipboardText or
                InputEventType.SwitchMonitor or
                InputEventType.MonitorInfo or
                InputEventType.SelectSource;
}
