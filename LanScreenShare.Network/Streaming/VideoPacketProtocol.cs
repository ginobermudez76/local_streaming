using System;
using System.IO;

namespace LanScreenShare.Network.Streaming
{
    public readonly record struct VideoHeaderData(
        uint Magic,
        int DataLength,
        int Width,
        int Height,
        long Timestamp,
        int HostFps,
        int HostFrameTimeMs);

    internal static class VideoPacketProtocol
    {
        public const uint MagicJpeg = 0x4C535331; // "LSS1"
        public const uint MagicH264 = 0x4C535332; // "LSS2"
        public const uint MagicEnd = 0x4C535330;  // "LSS0"
        public const int HeaderSize = 32;
        public const int MaxPayloadSize = 50 * 1024 * 1024;

        public static byte[] SerializeHeader(in VideoHeaderData header)
        {
            byte[] buffer = new byte[HeaderSize];
            using var ms = new MemoryStream(buffer);
            using var writer = new BinaryWriter(ms);
            writer.Write(header.Magic);
            writer.Write(header.DataLength);
            writer.Write(header.Width);
            writer.Write(header.Height);
            writer.Write(header.Timestamp);
            writer.Write(header.HostFps);
            writer.Write(header.HostFrameTimeMs);
            return buffer;
        }

        public static bool TryParseHeader(ReadOnlySpan<byte> buffer, out VideoHeaderData header)
        {
            header = default;
            if (buffer.Length < HeaderSize)
                return false;

            uint magic = BitConverter.ToUInt32(buffer.Slice(0, 4));
            int dataLength = BitConverter.ToInt32(buffer.Slice(4, 4));
            int width = BitConverter.ToInt32(buffer.Slice(8, 4));
            int height = BitConverter.ToInt32(buffer.Slice(12, 4));
            long timestamp = BitConverter.ToInt64(buffer.Slice(16, 8));
            int hostFps = BitConverter.ToInt32(buffer.Slice(24, 4));
            int hostFrameTimeMs = BitConverter.ToInt32(buffer.Slice(28, 4));

            header = new VideoHeaderData(magic, dataLength, width, height, timestamp, hostFps, hostFrameTimeMs);
            return true;
        }

        public static bool IsValidFrameMagic(uint magic) =>
            magic == MagicJpeg || magic == MagicH264;
    }
}
