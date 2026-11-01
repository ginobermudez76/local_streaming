using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Network.Streaming
{
    public class TcpAudioClient : IDisposable
    {
        private const uint AudioHeaderMagic = 0x4C534131; // "LSA1"
        private TcpClient? _client;
        private CancellationTokenSource? _cts;
        private bool _disposed;

        public event EventHandler<CapturedAudioChunk>? AudioReceived;
        public event EventHandler<string>? StatusChanged;

        public bool IsConnected => _client != null && _client.Connected;

        public async Task ConnectAsync(string hostIp, int port = 5002, CancellationToken cancellationToken = default)
        {
            if (IsConnected)
                await DisconnectAsync();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _cts.Token;

            StatusChanged?.Invoke(this, $"Conectando audio a {hostIp}:{port}...");

            _client = new TcpClient();
            _client.NoDelay = true;

            await _client.ConnectAsync(hostIp, port, token);

            StatusChanged?.Invoke(this, $"Audio conectado a {hostIp}:{port}");

            _ = Task.Run(async () =>
            {
                var stream = _client.GetStream();
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
                            reader.ReadInt16(); // Reserved
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

                        var chunk = new CapturedAudioChunk
                        {
                            Data = audioBuffer,
                            SampleRate = sampleRate,
                            Channels = channels,
                            BitsPerSample = bitsPerSample,
                            AudioFormat = audioFormat,
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
                    StatusChanged?.Invoke(this, $"Audio desconectado: {ex.Message}");
                }
                finally
                {
                    await DisconnectAsync();
                }
            }, token);
        }

        private static async Task ReadExactBytesAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken token)
        {
            int totalBytesRead = 0;
            while (totalBytesRead < count)
            {
                int bytesRead = await stream.ReadAsync(buffer, offset + totalBytesRead, count - totalBytesRead, token);
                if (bytesRead == 0)
                {
                    throw new EndOfStreamException("El servidor cerró la conexión TCP de audio antes de recibir todos los datos esperados.");
                }
                totalBytesRead += bytesRead;
            }
        }

        public Task DisconnectAsync()
        {
            _cts?.Cancel();
            try
            {
                _client?.Close();
                _client?.Dispose();
            }
            catch { }

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

