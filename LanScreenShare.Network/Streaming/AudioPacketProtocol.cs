using System;
using System.IO;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Network.Streaming
{
    public readonly record struct AudioHeaderData(
        int DataLength,
        int SampleRate,
        short Channels,
        short BitsPerSample,
        short AudioFormat,
        AudioSourceType Source,
        long Timestamp);

    internal static class AudioPacketProtocol
    {
        public const uint Magic = 0x4C534131; // "LSA1"
        public const int HeaderSize = 28;
        public const int MaxAudioPayloadSize = 10 * 1024 * 1024;

        public static byte[] SerializePacket(in CapturedAudioChunk chunk, AudioSourceType? overrideSource = null)
        {
            var data = chunk.Data ?? Array.Empty<byte>();
            byte[] packet = new byte[HeaderSize + data.Length];
            using var ms = new MemoryStream(packet);
            using var writer = new BinaryWriter(ms);
            writer.Write(Magic);
            writer.Write(data.Length);
            writer.Write(chunk.SampleRate);
            writer.Write((short)chunk.Channels);
            writer.Write((short)chunk.BitsPerSample);
            writer.Write(chunk.AudioFormat);
            writer.Write((short)(overrideSource ?? chunk.Source));
            writer.Write(chunk.Timestamp);
            if (data.Length > 0)
            {
                writer.Write(data);
            }
            return packet;
        }

        public static bool TryParseHeader(ReadOnlySpan<byte> headerBuffer, out AudioHeaderData header)
        {
            header = default;

            if (headerBuffer.Length < HeaderSize)
                return false;

            uint magic = BitConverter.ToUInt32(headerBuffer.Slice(0, 4));
            if (magic != Magic)
                return false;

            int dataLength = BitConverter.ToInt32(headerBuffer.Slice(4, 4));
            if (dataLength <= 0 || dataLength > MaxAudioPayloadSize)
                return false;

            header = new AudioHeaderData(
                dataLength,
                BitConverter.ToInt32(headerBuffer.Slice(8, 4)),
                BitConverter.ToInt16(headerBuffer.Slice(12, 2)),
                BitConverter.ToInt16(headerBuffer.Slice(14, 2)),
                BitConverter.ToInt16(headerBuffer.Slice(16, 2)),
                (AudioSourceType)BitConverter.ToInt16(headerBuffer.Slice(18, 2)),
                BitConverter.ToInt64(headerBuffer.Slice(20, 8)));

            return true;
        }
    }
}
