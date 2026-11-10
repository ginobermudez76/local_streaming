using System;
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LanScreenShare.Network.Streaming
{
    public enum FileTransferPacketType : byte
    {
        Offer = 1,
        Accept = 2,
        Reject = 3,
        Chunk = 4,
        Complete = 5,
        Cancel = 6
    }

    public sealed class FileTransferOfferDto
    {
        public string TransferId { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long FileSize { get; set; }
    }

    public sealed class FileTransferAcceptDto
    {
        public string TransferId { get; set; } = string.Empty;
    }

    public sealed class FileTransferRejectDto
    {
        public string TransferId { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }

    public sealed class FileTransferCompleteDto
    {
        public string TransferId { get; set; } = string.Empty;
    }

    public sealed class FileTransferCancelDto
    {
        public string TransferId { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
    }

    public static class FileTransferPacket
    {
        public const uint Magic = 0x4C535346; // "LSSF"
        public const int HeaderSize = 9; // 4 magic + 1 type + 4 length
        public const int MaxPayloadSize = 256 * 1024; // 256 KB max chunk payload
        public const int DefaultChunkSize = 64 * 1024; // 64 KB chunks

        public static async Task WriteOfferAsync(Stream stream, string transferId, string fileName, long fileSize, CancellationToken ct)
        {
            var dto = new FileTransferOfferDto
            {
                TransferId = transferId,
                FileName = Path.GetFileName(fileName),
                FileSize = fileSize
            };
            await WriteJsonPacketAsync(stream, FileTransferPacketType.Offer, dto, ct);
        }

        public static async Task WriteAcceptAsync(Stream stream, string transferId, CancellationToken ct)
        {
            var dto = new FileTransferAcceptDto { TransferId = transferId };
            await WriteJsonPacketAsync(stream, FileTransferPacketType.Accept, dto, ct);
        }

        public static async Task WriteRejectAsync(Stream stream, string transferId, string reason, CancellationToken ct)
        {
            var dto = new FileTransferRejectDto { TransferId = transferId, Reason = reason };
            await WriteJsonPacketAsync(stream, FileTransferPacketType.Reject, dto, ct);
        }

        public static async Task WriteCompleteAsync(Stream stream, string transferId, CancellationToken ct)
        {
            var dto = new FileTransferCompleteDto { TransferId = transferId };
            await WriteJsonPacketAsync(stream, FileTransferPacketType.Complete, dto, ct);
        }

        public static async Task WriteCancelAsync(Stream stream, string transferId, string reason, CancellationToken ct)
        {
            var dto = new FileTransferCancelDto { TransferId = transferId, Reason = reason };
            await WriteJsonPacketAsync(stream, FileTransferPacketType.Cancel, dto, ct);
        }

        public static async Task WriteChunkAsync(Stream stream, Guid transferId, long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            int payloadLen = 16 + 8 + data.Length;
            byte[] header = new byte[HeaderSize + 16 + 8];

            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), Magic);
            header[4] = (byte)FileTransferPacketType.Chunk;
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(5, 4), (uint)payloadLen);

            byte[] guidBytes = transferId.ToByteArray();
            Buffer.BlockCopy(guidBytes, 0, header, 9, 16);
            BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(25, 8), offset);

            await stream.WriteAsync(header, ct);
            if (!data.IsEmpty)
            {
                await stream.WriteAsync(data, ct);
            }
        }

        private static async Task WriteJsonPacketAsync<T>(Stream stream, FileTransferPacketType type, T dto, CancellationToken ct)
        {
            byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(dto);
            byte[] header = new byte[HeaderSize];

            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), Magic);
            header[4] = (byte)type;
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(5, 4), (uint)jsonBytes.Length);

            await stream.WriteAsync(header, ct);
            await stream.WriteAsync(jsonBytes, ct);
        }

        public static async Task ReadExactBytesAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int total = 0;
            while (total < count)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(offset + total, count - total), ct);
                if (read <= 0)
                {
                    throw new EndOfStreamException($"Flujo cerrado prematuramente mientras se leían {count} bytes.");
                }
                total += read;
            }
        }
    }
}
