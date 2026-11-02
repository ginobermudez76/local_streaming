using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Network.Streaming
{
    public class TcpStreamClient : IStreamClient
    {
        private const uint FrameHeaderMagicJpeg = 0x4C535331; // "LSS1"
        private const uint FrameHeaderMagicH264 = 0x4C535332; // "LSS2"
        private TcpClient? _client;
        private CancellationTokenSource? _cts;
        private bool _disposed;

        public event EventHandler<CapturedFrame>? FrameReceived;
        public event EventHandler<string>? StatusChanged;

        public bool IsConnected => _client != null && _client.Connected;

        public async Task ConnectAsync(string hostIp, int port = 5001, CancellationToken cancellationToken = default)
        {
            if (IsConnected)
                await DisconnectAsync();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _cts.Token;

            StatusChanged?.Invoke(this, $"Conectando a {hostIp}:{port}...");

            _client = new TcpClient();
            _client.NoDelay = true;

            await _client.ConnectAsync(hostIp, port, token);

            StatusChanged?.Invoke(this, $"Conectado a {hostIp}:{port}");

            _ = Task.Run(async () =>
            {
                var stream = _client.GetStream();
                byte[] headerBuffer = new byte[32];

                try
                {
                    while (!token.IsCancellationRequested && _client.Connected)
                    {
                        // 1. Leer encabezado completo de 32 bytes
                        await ReadExactBytesAsync(stream, headerBuffer, 0, 32, token);

                        uint magic;
                        int dataLength;
                        int width;
                        int height;
                        long timestamp;
                        int hostFps;
                        int hostFrameTimeMs;

                        using (var ms = new MemoryStream(headerBuffer))
                        using (var reader = new BinaryReader(ms))
                        {
                            magic = reader.ReadUInt32();
                            dataLength = reader.ReadInt32();
                            width = reader.ReadInt32();
                            height = reader.ReadInt32();
                            timestamp = reader.ReadInt64();
                            hostFps = reader.ReadInt32();
                            hostFrameTimeMs = reader.ReadInt32();
                        }

                        if ((magic != FrameHeaderMagicJpeg && magic != FrameHeaderMagicH264) || dataLength <= 0 || dataLength > 50 * 1024 * 1024)
                        {
                            Console.WriteLine($"[StreamClient] Encabezado inválido o corrupto (Magic: {magic:X}, Length: {dataLength})");
                            continue;
                        }

                        // 2. Leer fotograma completo
                        byte[] frameBuffer = new byte[dataLength];
                        await ReadExactBytesAsync(stream, frameBuffer, 0, dataLength, token);

                        var frame = new CapturedFrame
                        {
                            Data = frameBuffer,
                            Width = width,
                            Height = height,
                            Timestamp = timestamp,
                            Format = magic == FrameHeaderMagicH264 ? "h264" : "jpeg",
                            HostFps = hostFps,
                            HostFrameTimeMs = hostFrameTimeMs
                        };

                        FrameReceived?.Invoke(this, frame);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Cancelación normal
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[StreamClient] Error en recepción de flujo: {ex.Message}");
                    StatusChanged?.Invoke(this, $"Desconectado: {ex.Message}");
                }
                finally
                {
                    await DisconnectAsync();
                }
            }, token);
        }

        private static async Task ReadExactBytesAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken token)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await stream.ReadAsync(buffer, offset + totalRead, count - totalRead, token);
                if (read == 0)
                {
                    throw new IOException("El servidor cerró la conexión prematuramente.");
                }
                totalRead += read;
            }
        }

        public Task DisconnectAsync()
        {
            _cts?.Cancel();
            try { _client?.Close(); } catch { }
            _client = null;

            _cts?.Dispose();
            _cts = null;

            StatusChanged?.Invoke(this, "Desconectado");
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            DisconnectAsync().GetAwaiter().GetResult();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
