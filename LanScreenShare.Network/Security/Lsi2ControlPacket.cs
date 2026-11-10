using System;
using System.Buffers.Binary;
using System.IO;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Network.Security;

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
    /// Estructura de 28 bytes:
    /// 0..3:   Magic (0x4C534932)
    /// 4..11:  SequenceNumber (uint64 BE)
    /// 12..19: Timestamp (uint64 BE)
    /// 20:     EventType (uint8)
    /// 21:     Button (uint8)
    /// 22..23: WheelDelta (int16 BE)
    /// 24..27: KeyCode (int32 BE)
    /// Para eventos de ratón con coordenadas (Move/Down/Up), se codifican en NormalizedX y NormalizedY.
    /// Para mantener 28 bytes exactos según ADR-001:
    /// Si EventType es MouseMove, Wheel o MouseButton con coordenadas:
    /// KeyCode almacena NormalizedX (ushort, 16-bit) y NormalizedY (ushort, 16-bit) empaquetados.
    /// </summary>
    public static void WritePacket(
        Span<byte> destination,
        ulong sequenceNumber,
        ulong timestamp,
        RemoteInputMessage input)
    {
        if (destination.Length < PacketSize)
            throw new ArgumentException($"El destino debe tener al menos {PacketSize} bytes.", nameof(destination));

        // 1. Magic (4 bytes Big-Endian)
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(0, 4), MagicLsi2);

        // 2. SequenceNumber (8 bytes Big-Endian)
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(4, 8), sequenceNumber);

        // 3. Timestamp (8 bytes Big-Endian)
        BinaryPrimitives.WriteUInt64BigEndian(destination.Slice(12, 8), timestamp);

        // 4. Payload (8 bytes):
        // Offset 20: EventType (1B)
        destination[20] = (byte)input.Type;

        // Offset 21: Button (1B)
        destination[21] = (byte)input.Button;

        // Offset 22..23: WheelDelta (2B)
        BinaryPrimitives.WriteInt16BigEndian(destination.Slice(22, 2), input.WheelDelta);

        // Offset 24..27: KeyCode / Longitud de payload / Índice de monitor / Coordenadas empaquetadas (4B)
        if (input.Type == InputEventType.KeyDown ||
            input.Type == InputEventType.KeyUp ||
            input.Type == InputEventType.ClipboardText ||
            input.Type == InputEventType.SwitchMonitor ||
            input.Type == InputEventType.MonitorInfo ||
            input.Type == InputEventType.SelectSource)
        {
            BinaryPrimitives.WriteInt32BigEndian(destination.Slice(24, 4), input.KeyCode);
        }
        else if (input.Type == InputEventType.MouseMoveRelative)
        {
            // Coordenadas relativas en modo juego: deltas de píxeles con signo (-32768 a 32767)
            short relX = (short)Math.Clamp((int)Math.Round(input.NormalizedX), short.MinValue, short.MaxValue);
            short relY = (short)Math.Clamp((int)Math.Round(input.NormalizedY), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16BigEndian(destination.Slice(24, 2), relX);
            BinaryPrimitives.WriteInt16BigEndian(destination.Slice(26, 2), relY);
        }
        else
        {
            // Coordenadas absolutas normalizadas (0.0 a 1.0) en dos enteros de 16 bits (0 a 65535)
            if (input.NormalizedX < 0f || input.NormalizedY < 0f)
            {
                BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(24, 2), 0xFFFF);
                BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(26, 2), 0xFFFF);
            }
            else
            {
                ushort packedX = (ushort)Math.Clamp((int)Math.Round(input.NormalizedX * 65535f), 0, 65535);
                ushort packedY = (ushort)Math.Clamp((int)Math.Round(input.NormalizedY * 65535f), 0, 65535);
                BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(24, 2), packedX);
                BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(26, 2), packedY);
            }
        }
    }

    /// <summary>
    /// Intenta parsear un paquete de 28 bytes recibido.
    /// Retorna false y desconecta si se detecta magic anterior LSI1 o magic corrupto.
    /// </summary>
    public static bool TryReadPacket(
        ReadOnlySpan<byte> source,
        out ulong sequenceNumber,
        out ulong timestamp,
        out RemoteInputMessage? inputMessage,
        out string? protocolError)
    {
        sequenceNumber = 0;
        timestamp = 0;
        inputMessage = null;
        protocolError = null;

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

        sequenceNumber = BinaryPrimitives.ReadUInt64BigEndian(source.Slice(4, 8));
        timestamp = BinaryPrimitives.ReadUInt64BigEndian(source.Slice(12, 8));

        var eventType = (InputEventType)source[20];
        var button = (RemoteMouseButton)source[21];
        short wheelDelta = BinaryPrimitives.ReadInt16BigEndian(source.Slice(22, 2));

        int keyCode = 0;
        float normX = 0f;
        float normY = 0f;

        if (eventType == InputEventType.KeyDown ||
            eventType == InputEventType.KeyUp ||
            eventType == InputEventType.ClipboardText ||
            eventType == InputEventType.SwitchMonitor ||
            eventType == InputEventType.MonitorInfo ||
            eventType == InputEventType.SelectSource)
        {
            keyCode = BinaryPrimitives.ReadInt32BigEndian(source.Slice(24, 4));
        }
        else if (eventType == InputEventType.MouseMoveRelative)
        {
            short relX = BinaryPrimitives.ReadInt16BigEndian(source.Slice(24, 2));
            short relY = BinaryPrimitives.ReadInt16BigEndian(source.Slice(26, 2));
            normX = relX;
            normY = relY;
        }
        else
        {
            ushort packedX = BinaryPrimitives.ReadUInt16BigEndian(source.Slice(24, 2));
            ushort packedY = BinaryPrimitives.ReadUInt16BigEndian(source.Slice(26, 2));
            if (packedX == 0xFFFF && packedY == 0xFFFF)
            {
                normX = -1f;
                normY = -1f;
            }
            else
            {
                normX = packedX / 65535f;
                normY = packedY / 65535f;
            }
        }

        inputMessage = new RemoteInputMessage
        {
            Type = eventType,
            Button = button,
            WheelDelta = wheelDelta,
            KeyCode = keyCode,
            NormalizedX = normX,
            NormalizedY = normY
        };

        return true;
    }
}

