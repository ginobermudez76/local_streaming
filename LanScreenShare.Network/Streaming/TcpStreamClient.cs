using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;
using LanScreenShare.Network.Security;

namespace LanScreenShare.Network.Streaming
{
    public class TcpStreamClient : IStreamClient
    {
        private const uint FrameHeaderMagicJpeg = 0x4C535331; // "LSS1"
        private const uint FrameHeaderMagicH264 = 0x4C535332; // "LSS2"

        private TcpClient? _client;
        private Stream? _stream;
        private CancellationTokenSource? _cts;
        private bool _disposed;

        public event EventHandler<CapturedFrame>? FrameReceived;
        public event EventHandler<string>? StatusChanged;

        public bool IsConnected => _client != null && _client.Connected && _stream != null;
        public SessionHandshakeMessage? ActiveSessionHandshake { get; private set; }

        public async Task ConnectAsync(string hostIp, int port = 5001, CancellationToken cancellationToken = default)
        {
            await ConnectAsync(hostIp, port, null, cancellationToken);
        }

        public async Task ConnectAsync(
            string hostIp,
            int port = 5001,
            SslClientAuthenticationOptions? sslOptions = null,
            CancellationToken cancellationToken = default)
        {
            if (IsConnected)
                await DisconnectAsync();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _cts.Token;

            StatusChanged?.Invoke(this, $"Conectando a {hostIp}:{port}...");

            _client = new TcpClient();
            _client.NoDelay = true;

            await _client.ConnectAsync(hostIp, port, token);
            Stream stream = _client.GetStream();

            if (sslOptions != null)
            {
                var sslStream = new SslStream(stream, false);
                using var authCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, authCts.Token);

                await sslStream.AuthenticateAsClientAsync(sslOptions, linkedCts.Token);

                // Leer mensaje de señalización inicial (SESSION_OK)
                var handshake = await SessionHandshakeMessage.ReadAsync(sslStream, linkedCts.Token);
                if (handshake.Type != "SESSION_OK")
                {
                    throw new InvalidOperationException($"Sesión rechazada por el anfitrión: {handshake.ErrorMessage}");
                }

                ActiveSessionHandshake = handshake;
                stream = sslStream;
            }

            _stream = stream;
            StatusChanged?.Invoke(this, $"Conectado a {hostIp}:{port}");

            _ = Task.Run(async () =>
            {
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
                    Console.WriteLine($"[StreamClient] Desconexión de flujo: {ex.Message}");
                }
                finally
                {
                    await DisconnectAsync();
                }
            }, token);
        }

        private static async Task ReadExactBytesAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken token)
        {
            int totalBytesRead = 0;
            while (totalBytesRead < count)
            {
                int bytesRead = await stream.ReadAsync(buffer.AsMemory(offset + totalBytesRead, count - totalBytesRead), token);
                if (bytesRead == 0)
                {
                    throw new EndOfStreamException("El servidor cerró la conexión.");
                }
                totalBytesRead += bytesRead;
            }
        }

        public Task DisconnectAsync()
        {
            _cts?.Cancel();
            try { _stream?.Dispose(); } catch { }
            try { _client?.Close(); _client?.Dispose(); } catch { }

            _stream = null;
            _client = null;
            _cts?.Dispose();
            _cts = null;
            ActiveSessionHandshake = null;

            StatusChanged?.Invoke(this, "Desconectado");
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _ = DisconnectAsync();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
