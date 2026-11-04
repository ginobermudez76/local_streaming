using System;
using System.Diagnostics;
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
    public class TcpControlClient : IDisposable
    {
        private TcpClient? _client;
        private Stream? _stream;
        private CancellationTokenSource? _cts;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private ulong _sequenceNumber;
        private bool _disposed;

        public event EventHandler<string>? StatusChanged;
        public bool IsConnected => _client != null && _client.Connected && _stream != null;

        public async Task ConnectAsync(
            string hostIp,
            int port = 5003,
            SslClientAuthenticationOptions? sslOptions = null,
            string? sessionId = null,
            string? attachmentToken = null,
            CancellationToken cancellationToken = default)
        {
            if (IsConnected)
                await DisconnectAsync();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _cts.Token;

            StatusChanged?.Invoke(this, $"Conectando control remoto LSI2 a {hostIp}:{port}...");

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

                if (sessionId != null && attachmentToken != null)
                {
                    // Enviar CHANNEL_ATTACH (37 bytes)
                    byte[] attachBytes = ChannelAttachmentMessage.Serialize(sessionId, attachmentToken, ChannelType.RemoteControl);
                    await sslStream.WriteAsync(attachBytes, linkedCts.Token);

                    // Leer código de respuesta (1 byte)
                    byte[] response = new byte[1];
                    int r = await sslStream.ReadAsync(response.AsMemory(0, 1), linkedCts.Token);
                    if (r == 0 || response[0] != (byte)AttachmentStatus.Success)
                    {
                        var status = (AttachmentStatus)response[0];
                        throw new InvalidOperationException($"Fijación de canal de control rechazada por el anfitrión: {status}");
                    }
                }

                stream = sslStream;
            }

            _stream = stream;
            _sequenceNumber = 0;
            StatusChanged?.Invoke(this, $"Control remoto LSI2 conectado a {hostIp}:{port}");
        }

        public async Task SendInputAsync(RemoteInputMessage input)
        {
            if (!IsConnected || _stream == null)
                return;

            byte[] buffer = new byte[Lsi2ControlPacket.PacketSize];
            ulong seq = Interlocked.Increment(ref _sequenceNumber);
            ulong ts = (ulong)Stopwatch.GetTimestamp();

            Lsi2ControlPacket.WritePacket(buffer, seq, ts, input);

            await _sendLock.WaitAsync();
            try
            {
                if (IsConnected && _stream != null)
                {
                    await _stream.WriteAsync(buffer.AsMemory(0, buffer.Length));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ControlClient] Error enviando entrada LSI2: {ex.Message}");
                await DisconnectAsync();
            }
            finally
            {
                _sendLock.Release();
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

            StatusChanged?.Invoke(this, "Control remoto desconectado");
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _ = DisconnectAsync();
            _sendLock.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
