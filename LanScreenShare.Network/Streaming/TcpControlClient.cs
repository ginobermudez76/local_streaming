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
        public event EventHandler<string>? MonitorInfoReceived;
        public bool IsConnected => _client != null && _client.Connected && _stream != null;

        public Task ConnectAsync(string hostIp, int port = 5003, CancellationToken cancellationToken = default) =>
            ConnectAsync(new ControlClientConnectionOptions(hostIp, port), cancellationToken);

        public Task ConnectAsync(
            string hostIp,
            int port,
            SslClientAuthenticationOptions? sslOptions,
            CancellationToken cancellationToken = default) =>
            ConnectAsync(new ControlClientConnectionOptions(hostIp, port, sslOptions), cancellationToken);

        public async Task ConnectAsync(ControlClientConnectionOptions options, CancellationToken cancellationToken = default)
        {
            if (IsConnected)
                await DisconnectAsync();

            _cts = new CancellationTokenSource();
            var sessionToken = _cts.Token;
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken, cancellationToken);
            var handshakeToken = handshakeCts.Token;

            StatusChanged?.Invoke(this, $"Conectando control remoto LSI2 a {options.HostIp}:{options.Port}...");

            _client = new TcpClient { NoDelay = true };

            await _client.ConnectAsync(options.HostIp, options.Port, handshakeToken);
            Stream stream = _client.GetStream();

            if (options.SslOptions != null)
            {
                stream = await EstablishTlsHandshakeAsync(stream, options, handshakeToken);
            }

            _stream = stream;
            _sequenceNumber = 0;
            _ = Task.Run(() => ReceiveServerMessagesLoopAsync(stream, sessionToken));
            StatusChanged?.Invoke(this, $"Control remoto LSI2 conectado a {options.HostIp}:{options.Port}");
        }

        private static async Task<Stream> EstablishTlsHandshakeAsync(
            Stream rawStream,
            ControlClientConnectionOptions options,
            CancellationToken handshakeToken)
        {
            var sslStream = new SslStream(rawStream, false);
            using var authCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(handshakeToken, authCts.Token);

            await sslStream.AuthenticateAsClientAsync(options.SslOptions!, linkedCts.Token);

            if (options.SessionId != null && options.AttachmentToken != null)
            {
                byte[] attachBytes = ChannelAttachmentMessage.Serialize(options.SessionId, options.AttachmentToken, ChannelType.RemoteControl);
                await sslStream.WriteAsync(attachBytes, linkedCts.Token);

                byte[] response = new byte[1];
                int r = await sslStream.ReadAsync(response.AsMemory(0, 1), linkedCts.Token);
                if (r == 0 || response[0] != (byte)AttachmentStatus.Success)
                {
                    var status = (AttachmentStatus)response[0];
                    throw new InvalidOperationException($"Fijación de canal de control rechazada por el anfitrión: {status}");
                }
            }

            return sslStream;
        }

        private async Task ReceiveServerMessagesLoopAsync(Stream stream, CancellationToken token)
        {
            byte[] headerBuffer = new byte[Lsi2ControlPacket.PacketSize];
            try
            {
                while (!token.IsCancellationRequested && IsConnected)
                {
                    if (!await TryProcessSingleServerMessageAsync(stream, headerBuffer, token))
                    {
                        break;
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

        private async Task<bool> TryProcessSingleServerMessageAsync(Stream stream, byte[] headerBuffer, CancellationToken token)
        {
            await ReadExactBytesAsync(stream, headerBuffer, token);

            if (!Lsi2ControlPacket.TryReadPacket(
                    headerBuffer,
                    out _,
                    out var message,
                    out string? protocolError))
            {
                Console.WriteLine($"[ControlClient] Error de protocolo desde anfitrión: {protocolError}");
                return false;
            }

            if (message == null)
                return true;

            if (message.Type == InputEventType.ClipboardText || message.Type == InputEventType.MonitorInfo)
            {
                return await ReadAndDispatchTextPayloadAsync(stream, message.Type, message.KeyCode, token);
            }

            return true;
        }

        private async Task<bool> ReadAndDispatchTextPayloadAsync(
            Stream stream,
            InputEventType type,
            int payloadLength,
            CancellationToken token)
        {
            if (payloadLength <= 0 || payloadLength > Lsi2ControlPacket.MaxClipboardPayloadBytes)
            {
                Console.WriteLine($"[ControlClient] Longitud de payload inválida desde anfitrión: {payloadLength}");
                return false;
            }

            byte[] payloadBuffer = new byte[payloadLength];
            await ReadExactBytesAsync(stream, payloadBuffer, token);
            string text = Encoding.UTF8.GetString(payloadBuffer);

            DispatchServerMessage(type, text);
            return true;
        }

        private void DispatchServerMessage(InputEventType type, string text)
        {
            try
            {
                if (type == InputEventType.ClipboardText)
                {
                    ClipboardTextReceived?.Invoke(this, text);
                }
                else
                {
                    MonitorInfoReceived?.Invoke(this, text);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ControlClient] Error procesando evento {type}: {ex.Message}");
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
            await WritePacketWithLockAsync(buffer, "entrada LSI2");
        }

        public Task SendSwitchMonitorAsync(int targetIndex = -1)
        {
            return SendInputAsync(new RemoteInputMessage
            {
                Type = InputEventType.SwitchMonitor,
                KeyCode = targetIndex
            });
        }

        public Task RequestSourceCatalogAsync()
        {
            return SendInputAsync(new RemoteInputMessage
            {
                Type = InputEventType.SwitchMonitor,
                KeyCode = -2
            });
        }

        public Task SendSelectSourceAsync(string sourceId) =>
            SendTextPayloadAsync(InputEventType.SelectSource, sourceId, "selección de fuente LSI2");

        public Task SendClipboardTextAsync(string text) =>
            SendTextPayloadAsync(InputEventType.ClipboardText, text, "portapapeles LSI2");

        private static bool IsValidPayloadSize(int length) =>
            length > 0 && length <= Lsi2ControlPacket.MaxClipboardPayloadBytes;

        private bool CanSendTextPayload(string text, out byte[] utf8Bytes)
        {
            utf8Bytes = Array.Empty<byte>();
            if (!IsConnected || string.IsNullOrEmpty(text))
                return false;

            utf8Bytes = Encoding.UTF8.GetBytes(text);
            return IsValidPayloadSize(utf8Bytes.Length);
        }

        private async Task SendTextPayloadAsync(InputEventType type, string text, string errorContext)
        {
            if (!CanSendTextPayload(text, out var utf8Bytes))
                return;

            byte[] packet = new byte[Lsi2ControlPacket.PacketSize + utf8Bytes.Length];
            ulong seq = Interlocked.Increment(ref _sequenceNumber);
            ulong ts = (ulong)Stopwatch.GetTimestamp();

            var input = new RemoteInputMessage
            {
                Type = type,
                KeyCode = utf8Bytes.Length,
                ClipboardText = text
            };

            Lsi2ControlPacket.WritePacket(packet.AsSpan(0, Lsi2ControlPacket.PacketSize), seq, ts, input);
            Buffer.BlockCopy(utf8Bytes, 0, packet, Lsi2ControlPacket.PacketSize, utf8Bytes.Length);

            await WritePacketWithLockAsync(packet, errorContext);
        }

        private async Task WritePacketWithLockAsync(byte[] packet, string errorContext)
        {
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
                Console.WriteLine($"[ControlClient] Error enviando {errorContext}: {ex.Message}");
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
