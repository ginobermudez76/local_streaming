using System;
using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
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
        public event EventHandler<string>? ClipboardTextReceived;
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

            _cts = new CancellationTokenSource();
            var sessionToken = _cts.Token;
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, cancellationToken);
            var handshakeToken = handshakeCts.Token;

            StatusChanged?.Invoke(this, $"Conectando control remoto LSI2 a {hostIp}:{port}...");

            _client = new TcpClient();
            _client.NoDelay = true;

            await _client.ConnectAsync(hostIp, port, handshakeToken);
            Stream stream = _client.GetStream();

            if (sslOptions != null)
            {
                var sslStream = new SslStream(stream, false);
                using var authCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(handshakeToken, authCts.Token);

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
            _ = Task.Run(() => ReceiveServerMessagesLoopAsync(stream, sessionToken));
            StatusChanged?.Invoke(this, $"Control remoto LSI2 conectado a {hostIp}:{port}");
        }

        private async Task ReceiveServerMessagesLoopAsync(Stream stream, CancellationToken token)
        {
            byte[] headerBuffer = new byte[Lsi2ControlPacket.PacketSize];
            try
            {
                while (!token.IsCancellationRequested && IsConnected)
                {
                    await ReadExactBytesAsync(stream, headerBuffer, 0, Lsi2ControlPacket.PacketSize, token);

                    if (!Lsi2ControlPacket.TryReadPacket(
                            headerBuffer,
                            out _,
                            out _,
                            out var message,
                            out string? protocolError))
                    {
                        Console.WriteLine($"[ControlClient] Error de protocolo desde anfitrión: {protocolError}");
                        break;
                    }

                    if (message == null)
                        continue;

                    if (message.Type == InputEventType.ClipboardText)
                    {
                        int payloadLength = message.KeyCode;
                        if (payloadLength <= 0 || payloadLength > Lsi2ControlPacket.MaxClipboardPayloadBytes)
                        {
                            Console.WriteLine($"[ControlClient] Longitud de portapapeles inválida desde anfitrión: {payloadLength}");
                            break;
                        }

                        byte[] payloadBuffer = new byte[payloadLength];
                        await ReadExactBytesAsync(stream, payloadBuffer, 0, payloadLength, token);
                        string text = Encoding.UTF8.GetString(payloadBuffer);

                        try
                        {
                            ClipboardTextReceived?.Invoke(this, text);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[ControlClient] Error en ClipboardTextReceived: {ex.Message}");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Cancelación normal
            }
            catch (EndOfStreamException)
            {
                // Cierre normal del servidor
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    Console.WriteLine($"[ControlClient] Bucle de recepción finalizado: {ex.Message}");
                }
            }
        }

        private static async Task ReadExactBytesAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken token)
        {
            int totalBytesRead = 0;
            while (totalBytesRead < count)
            {
                int bytesRead = await stream.ReadAsync(buffer.AsMemory(offset + totalBytesRead, count - totalBytesRead), token);
                if (bytesRead == 0)
                {
                    throw new EndOfStreamException("El anfitrión cerró el canal de control.");
                }
                totalBytesRead += bytesRead;
            }
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

        public async Task SendClipboardTextAsync(string text)
        {
            if (!IsConnected || _stream == null || string.IsNullOrEmpty(text))
                return;

            byte[] utf8Bytes = Encoding.UTF8.GetBytes(text);
            if (utf8Bytes.Length == 0 || utf8Bytes.Length > Lsi2ControlPacket.MaxClipboardPayloadBytes)
                return;

            byte[] packet = new byte[Lsi2ControlPacket.PacketSize + utf8Bytes.Length];
            ulong seq = Interlocked.Increment(ref _sequenceNumber);
            ulong ts = (ulong)Stopwatch.GetTimestamp();

            var input = new RemoteInputMessage
            {
                Type = InputEventType.ClipboardText,
                KeyCode = utf8Bytes.Length,
                ClipboardText = text
            };

            Lsi2ControlPacket.WritePacket(packet.AsSpan(0, Lsi2ControlPacket.PacketSize), seq, ts, input);
            Buffer.BlockCopy(utf8Bytes, 0, packet, Lsi2ControlPacket.PacketSize, utf8Bytes.Length);

            await _sendLock.WaitAsync();
            try
            {
                if (IsConnected && _stream != null)
                {
                    await _stream.WriteAsync(packet.AsMemory(0, packet.Length));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ControlClient] Error enviando portapapeles LSI2: {ex.Message}");
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
