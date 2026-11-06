using System;
using System.Buffers.Binary;

namespace LanScreenShare.Core.Models
{
    /// <summary>
    /// Paquete binario compacto de telemetría de 32 bytes enviado por el espectador al anfitrión (LSST).
    /// Estructura fija:
    ///   0..3:   Magic (0x4C535354 - "LSST")
    ///   4..11:  EchoTimestamp (Int64 BE) - Marca de tiempo del host para cálculo exacto de RTT (reloj del host)
    ///   12..19: ClientTimestamp (Int64 BE) - Marca de tiempo local del visor
    ///   20..23: ViewerFps (Int32 BE) - FPS reales de decodificación y renderizado en el visor
    ///   24..27: ViewerDroppedFrames (Int32 BE) - Fotogramas descartados en el visor
    ///   28..31: AvDriftMs (Int32 BE) - Desfase medido entre Audio y Video (AudioTimestamp - VideoTimestamp)
    /// </summary>
    public sealed class StreamTelemetryPacket
    {
        public const uint MagicLsst = 0x4C535354; // "LSST"
        public const int PacketSize = 32;

        public long EchoTimestamp { get; set; }
        public long ClientTimestamp { get; set; }
        public int ViewerFps { get; set; }
        public int ViewerDroppedFrames { get; set; }
        public int AvDriftMs { get; set; }

        public void WriteTo(Span<byte> destination)
        {
            if (destination.Length < PacketSize)
                throw new ArgumentException($"El búfer debe tener al menos {PacketSize} bytes.", nameof(destination));

            BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(0, 4), MagicLsst);
            BinaryPrimitives.WriteInt64BigEndian(destination.Slice(4, 8), EchoTimestamp);
            BinaryPrimitives.WriteInt64BigEndian(destination.Slice(12, 8), ClientTimestamp);
            BinaryPrimitives.WriteInt32BigEndian(destination.Slice(20, 4), ViewerFps);
            BinaryPrimitives.WriteInt32BigEndian(destination.Slice(24, 4), ViewerDroppedFrames);
            BinaryPrimitives.WriteInt32BigEndian(destination.Slice(28, 4), AvDriftMs);
        }

        public byte[] ToByteArray()
        {
            var bytes = new byte[PacketSize];
            WriteTo(bytes);
            return bytes;
        }

        public static bool TryReadFrom(ReadOnlySpan<byte> source, out StreamTelemetryPacket? packet)
        {
            packet = null;
            if (source.Length < PacketSize)
                return false;

            uint magic = BinaryPrimitives.ReadUInt32BigEndian(source.Slice(0, 4));
            if (magic != MagicLsst)
                return false;

            packet = new StreamTelemetryPacket
            {
                EchoTimestamp = BinaryPrimitives.ReadInt64BigEndian(source.Slice(4, 8)),
                ClientTimestamp = BinaryPrimitives.ReadInt64BigEndian(source.Slice(12, 8)),
                ViewerFps = BinaryPrimitives.ReadInt32BigEndian(source.Slice(20, 4)),
                ViewerDroppedFrames = BinaryPrimitives.ReadInt32BigEndian(source.Slice(24, 4)),
                AvDriftMs = BinaryPrimitives.ReadInt32BigEndian(source.Slice(28, 4))
            };

            return true;
        }
    }
}
