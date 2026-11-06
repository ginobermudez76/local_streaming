using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Models;
using LanScreenShare.Core.Security;
using LanScreenShare.Network.Security;

namespace LanScreenShare.Network.Streaming
{
    public class TcpAudioClient : IDisposable
    {
        private const uint AudioHeaderMagic = 0x4C534131; // "LSA1"
        private TcpClient? _client;
        private Stream? _stream;
        private CancellationTokenSource? _cts;
        private bool _disposed;
        private long _latestAudioTimestamp;

        public event EventHandler<CapturedAudioChunk>? AudioReceived;
        public event EventHandler<string>? StatusChanged;

        public bool IsConnected => _client != null && _client.Connected && _stream != null;
        public long LatestAudioTimestamp => Interlocked.Read(ref _latestAudioTimestamp);

        public async Task ConnectAsync(
            string hostIp,
            int port = 5002,
            SslClientAuthenticationOptions? sslOptions = null,
            string? sessionId = null,
            string? attachmentToken = null,
            CancellationToken cancellationToken = default)
        {
            if (IsConnected)
                await DisconnectAsync();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _cts.Token;

            StatusChanged?.Invoke(this, $"Conectando audio a {hostIp}:{port}...");

            _client = new TcpClient();
            _client.NoDelay = true;
            _client.ReceiveBufferSize = 64 * 1024;
            _client.SendBufferSize = 64 * 1024;

            await _client.ConnectAsync(hostIp, port, token);
            Stream stream = _client.GetStream();

            if (sslOptions != null)
            {
                var sslStream = new SslStream(stream, false);
                using var authCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, authCts.Token);

                await sslStream.AuthenticateAsClientAsync(sslOptions, linkedCts.Token);

                if (sessionId != null && attachmentToken != null)
                {
                    // Enviar CHANNEL_ATTACH (37 bytes)
                    byte[] attachBytes = ChannelAttachmentMessage.Serialize(sessionId, attachmentToken, ChannelType.Audio);
                    await sslStream.WriteAsync(attachBytes, linkedCts.Token);

                    // Leer código de respuesta (1 byte)
                    byte[] response = new byte[1];
                    int r = await sslStream.ReadAsync(response.AsMemory(0, 1), linkedCts.Token);
                    if (r == 0 || response[0] != (byte)AttachmentStatus.Success)
                    {
                        var status = (AttachmentStatus)response[0];
                        throw new InvalidOperationException($"Fijación de canal de audio rechazada por el anfitrión: {status}");
                    }
                }

                stream = sslStream;
            }

            _stream = stream;
            StatusChanged?.Invoke(this, $"Audio conectado a {hostIp}:{port}");

            _ = Task.Run(async () =>
            {
                byte[] headerBuffer = new byte[28];

                try
                {
                    while (!token.IsCancellationRequested && _client.Connected)
                    {
                        // 1. Leer encabezado de 28 bytes
                        await ReadExactBytesAsync(stream, headerBuffer, 0, 28, token);

                        uint magic;
                        int dataLength;
                        int sampleRate;
                        short channels;
                        short bitsPerSample;
                        short audioFormat;
                        short sourceTypeVal;
                        long timestamp;

                        using (var ms = new MemoryStream(headerBuffer))
                        using (var reader = new BinaryReader(ms))
                        {
                            magic = reader.ReadUInt32();
                            dataLength = reader.ReadInt32();
                            sampleRate = reader.ReadInt32();
                            channels = reader.ReadInt16();
                            bitsPerSample = reader.ReadInt16();
                            audioFormat = reader.ReadInt16();
                            sourceTypeVal = reader.ReadInt16(); // 0 = Desktop, 1 = Microphone
                            timestamp = reader.ReadInt64();
                        }

                        if (magic != AudioHeaderMagic || dataLength <= 0 || dataLength > 10 * 1024 * 1024)
                        {
                            Console.WriteLine($"[AudioClient] Encabezado de audio inválido (Magic: {magic:X}, Length: {dataLength})");
                            continue;
                        }

                        // 2. Leer fragmento de audio PCM
                        byte[] audioBuffer = new byte[dataLength];
                        await ReadExactBytesAsync(stream, audioBuffer, 0, dataLength, token);

                        Interlocked.Exchange(ref _latestAudioTimestamp, timestamp);

                        var chunk = new CapturedAudioChunk
                        {
                            Data = audioBuffer,
                            SampleRate = sampleRate,
                            Channels = channels,
                            BitsPerSample = bitsPerSample,
                            AudioFormat = audioFormat,
                            Source = (AudioSourceType)sourceTypeVal,
                            Timestamp = timestamp
                        };

                        AudioReceived?.Invoke(this, chunk);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Cancelación esperada
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AudioClient] Desconexión de audio: {ex.Message}");
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
                    throw new EndOfStreamException("El servidor cerró la conexión de audio.");
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

            StatusChanged?.Invoke(this, "Audio desconectado");
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
