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
        private TcpClient? _client;
        private Stream? _stream;
        private CancellationTokenSource? _cts;
        private bool _disposed;
        private long _latestAudioTimestamp;

        public event EventHandler<CapturedAudioChunk>? AudioReceived;
        public event EventHandler<string>? StatusChanged;

        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public bool IsConnected => _client?.Connected == true && _stream != null;
        public long LatestAudioTimestamp => Interlocked.Read(ref _latestAudioTimestamp);

        public Task ConnectAsync(string hostIp, int port = 5002, CancellationToken cancellationToken = default) =>
            ConnectAsync(new AudioClientConnectionOptions(hostIp, port), cancellationToken);

        public Task ConnectAsync(
            string hostIp,
            int port,
            SslClientAuthenticationOptions? sslOptions,
            CancellationToken cancellationToken = default) =>
            ConnectAsync(new AudioClientConnectionOptions(hostIp, port, sslOptions), cancellationToken);

        public async Task ConnectAsync(AudioClientConnectionOptions options, CancellationToken cancellationToken = default)
        {
            if (IsConnected)
                await DisconnectAsync();

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var sessionToken = _cts.Token;

            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, cancellationToken);
            var handshakeToken = handshakeCts.Token;

            StatusChanged?.Invoke(this, $"Conectando audio a {options.HostIp}:{options.Port}...");

            _client = new TcpClient
            {
                NoDelay = true,
                ReceiveBufferSize = 64 * 1024,
                SendBufferSize = 64 * 1024
            };

            await _client.ConnectAsync(options.HostIp, options.Port, handshakeToken);
            Stream stream = _client.GetStream();

            if (options.SslOptions != null)
            {
                stream = await EstablishTlsHandshakeAsync(stream, options, handshakeToken);
            }

            _stream = stream;
            StatusChanged?.Invoke(this, $"Audio conectado a {options.HostIp}:{options.Port}");

            _ = Task.Run(() => ReceiveLoopAsync(stream, sessionToken), sessionToken);
        }

        private static async Task<Stream> EstablishTlsHandshakeAsync(
            Stream rawStream,
            AudioClientConnectionOptions options,
            CancellationToken handshakeToken)
        {
            var sslStream = new SslStream(rawStream, false);
            using var authCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(handshakeToken, authCts.Token);

            await sslStream.AuthenticateAsClientAsync(options.SslOptions!, linkedCts.Token);

            if (options.SessionId != null && options.AttachmentToken != null)
            {
                byte[] attachBytes = ChannelAttachmentMessage.Serialize(options.SessionId, options.AttachmentToken, ChannelType.Audio);
                await sslStream.WriteAsync(attachBytes, linkedCts.Token);

                byte[] response = new byte[1];
                int r = await sslStream.ReadAsync(response.AsMemory(0, 1), linkedCts.Token);
                if (r == 0 || response[0] != (byte)AttachmentStatus.Success)
                {
                    var status = (AttachmentStatus)response[0];
                    throw new InvalidOperationException($"Fijación de canal de audio rechazada por el anfitrión: {status}");
                }
            }

            return sslStream;
        }

        private bool CanContinueReceiving(CancellationToken sessionToken) =>
            !sessionToken.IsCancellationRequested && _client?.Connected == true;

        private async Task ReceiveLoopAsync(Stream stream, CancellationToken sessionToken)
        {
            byte[] headerBuffer = new byte[AudioPacketProtocol.HeaderSize];

            try
            {
                while (CanContinueReceiving(sessionToken))
                {
                    await ReadExactBytesAsync(stream, headerBuffer, sessionToken);

                    if (!AudioPacketProtocol.TryParseHeader(headerBuffer, out var header))
                    {
                        Console.WriteLine("[AudioClient] Encabezado de audio inválido recibido");
                        break;
                    }

                    byte[] audioBuffer = new byte[header.DataLength];
                    await ReadExactBytesAsync(stream, audioBuffer, sessionToken);

                    Interlocked.Exchange(ref _latestAudioTimestamp, header.Timestamp);

                    DispatchAudioChunk(new CapturedAudioChunk
                    {
                        Data = audioBuffer,
                        SampleRate = header.SampleRate,
                        Channels = header.Channels,
                        BitsPerSample = header.BitsPerSample,
                        AudioFormat = header.AudioFormat,
                        Source = header.Source,
                        Timestamp = header.Timestamp
                    });
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
        }

        private void DispatchAudioChunk(CapturedAudioChunk chunk)
        {
            try
            {
                AudioReceived?.Invoke(this, chunk);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioClient] Error en suscriptor AudioReceived: {ex.Message}");
            }
        }

        private static bool HasPayload(CapturedAudioChunk chunk) =>
            chunk.Data != null && chunk.Data.Length > 0;

        public async Task SendAudioChunkAsync(CapturedAudioChunk chunk)
        {
            if (!IsConnected || !HasPayload(chunk))
                return;

            byte[] packet = AudioPacketProtocol.SerializePacket(chunk, AudioSourceType.ViewerMicrophone);
            await TryWritePacketAsync(packet);
        }

        private async Task TryWritePacketAsync(byte[] packet)
        {
            bool lockTaken = false;
            try
            {
                lockTaken = await _sendLock.WaitAsync(100);
                if (lockTaken && _stream != null)
                {
                    await _stream.WriteAsync(packet.AsMemory(0, packet.Length));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioClient] Error enviando audio del visor: {ex.Message}");
            }
            finally
            {
                if (lockTaken)
                {
                    try { _sendLock.Release(); } catch { }
                }
            }
        }

        private static async Task ReadExactBytesAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
        {
            int totalBytesRead = 0;
            int count = buffer.Length;
            while (totalBytesRead < count)
            {
                int bytesRead = await stream.ReadAsync(buffer.Slice(totalBytesRead, count - totalBytesRead), token);
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
