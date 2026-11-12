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
        private TcpClient? _client;
        private Stream? _stream;
        private CancellationTokenSource? _cts;
        private int _isDisconnecting;
        private bool _disposed;
        private int _currentAvDriftMs;

        public event EventHandler<CapturedFrame>? FrameReceived;
        public event EventHandler<string>? StatusChanged;
        public event EventHandler<StreamTelemetry>? TelemetryUpdated;

        public bool IsConnected => _client != null && _client.Connected && _stream != null;
        public SessionHandshakeMessage? ActiveSessionHandshake { get; private set; }
        public StreamTelemetry CurrentTelemetry { get; } = new();

        public void UpdateAvDrift(int driftMs) => _currentAvDriftMs = driftMs;

        public Task ConnectAsync(string hostIp, int port = 5001, CancellationToken cancellationToken = default) =>
            ConnectAsync(hostIp, port, null, cancellationToken);

        public async Task ConnectAsync(
            string hostIp,
            int port = 5001,
            SslClientAuthenticationOptions? sslOptions = null,
            CancellationToken cancellationToken = default)
        {
            if (IsConnected)
                await DisconnectAsync();

            _isDisconnecting = 0;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _cts.Token;

            StatusChanged?.Invoke(this, $"Conectando a {hostIp}:{port}...");

            _client = new TcpClient { NoDelay = true };
            await _client.ConnectAsync(hostIp, port, token);
            Stream stream = _client.GetStream();

            if (sslOptions != null)
            {
                stream = await EstablishTlsHandshakeAsync(stream, sslOptions, token);
            }

            _stream = stream;
            StatusChanged?.Invoke(this, $"Conectado a {hostIp}:{port}");

            _ = Task.Run(() => ReceiveFramesLoopAsync(stream, token), token);
        }

        private async Task<Stream> EstablishTlsHandshakeAsync(
            Stream rawStream,
            SslClientAuthenticationOptions sslOptions,
            CancellationToken token)
        {
            var sslStream = new SslStream(rawStream, false);
            using var authCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, authCts.Token);

            await sslStream.AuthenticateAsClientAsync(sslOptions, linkedCts.Token);

            var handshake = await SessionHandshakeMessage.ReadAsync(sslStream, linkedCts.Token);
            if (handshake.Type != "SESSION_OK")
            {
                throw new InvalidOperationException($"Sesión rechazada por el anfitrión: {handshake.ErrorMessage}");
            }

            ActiveSessionHandshake = handshake;
            return sslStream;
        }

        private sealed class FrameTrackingState
        {
            public int IntervalFrames;
            public long IntervalBytes;
            public DateTime LastTelemetryTime = DateTime.UtcNow;
            public bool EndedByHost;
        }

        private bool CanContinueReceiving(CancellationToken token) =>
            !token.IsCancellationRequested && _client?.Connected == true;

        private async Task ReceiveFramesLoopAsync(Stream stream, CancellationToken token)
        {
            byte[] headerBuffer = new byte[VideoPacketProtocol.HeaderSize];
            var state = new FrameTrackingState();

            try
            {
                while (CanContinueReceiving(token))
                {
                    if (!await TryProcessSingleFrameAsync(stream, headerBuffer, state, token))
                    {
                        break;
                    }
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
                string reason = state.EndedByHost
                    ? "El anfitrión ha finalizado la transmisión"
                    : (token.IsCancellationRequested ? "Desconectado" : "Conexión perdida con el anfitrión");
                await DisconnectInternalAsync(reason);
            }
        }

        private static bool IsValidPayload(int dataLength) =>
            dataLength > 0 && dataLength <= VideoPacketProtocol.MaxPayloadSize;

        private async Task<bool> TryProcessSingleFrameAsync(
            Stream stream,
            byte[] headerBuffer,
            FrameTrackingState state,
            CancellationToken token)
        {
            await ReadExactBytesAsync(stream, headerBuffer, token);

            if (!VideoPacketProtocol.TryParseHeader(headerBuffer, out var header))
            {
                return true;
            }

            if (header.Magic == VideoPacketProtocol.MagicEnd)
            {
                Console.WriteLine("[StreamClient] Recibida señal de fin de transmisión del anfitrión (LSS0).");
                state.EndedByHost = true;
                return false;
            }

            if (!VideoPacketProtocol.IsValidFrameMagic(header.Magic) || !IsValidPayload(header.DataLength))
            {
                Console.WriteLine($"[StreamClient] Encabezado inválido o corrupto (Magic: {header.Magic:X}, Length: {header.DataLength})");
                return true;
            }

            byte[] frameBuffer = new byte[header.DataLength];
            await ReadExactBytesAsync(stream, frameBuffer, token);

            state.IntervalFrames++;
            state.IntervalBytes += VideoPacketProtocol.HeaderSize + header.DataLength;

            DispatchFrame(header, frameBuffer);
            EmitTelemetryIfDue(stream, header, state, token);

            return true;
        }

        private void DispatchFrame(in VideoHeaderData header, byte[] frameBuffer)
        {
            var frame = new CapturedFrame
            {
                Data = frameBuffer,
                Width = header.Width,
                Height = header.Height,
                Timestamp = header.Timestamp,
                Format = header.Magic == VideoPacketProtocol.MagicH264 ? "h264" : "jpeg",
                HostFps = header.HostFps,
                HostFrameTimeMs = header.HostFrameTimeMs
            };

            FrameReceived?.Invoke(this, frame);
        }

        private void EmitTelemetryIfDue(
            Stream stream,
            in VideoHeaderData header,
            FrameTrackingState state,
            CancellationToken token)
        {
            var elapsed = (DateTime.UtcNow - state.LastTelemetryTime).TotalMilliseconds;
            if (elapsed < 250)
                return;

            double seconds = elapsed / 1000.0;
            int currentViewerFps = (int)Math.Round(state.IntervalFrames / seconds);
            double bitrateMbps = (state.IntervalBytes * 8.0) / (seconds * 1_000_000.0);

            var telemetryPacket = new StreamTelemetryPacket
            {
                EchoTimestamp = header.Timestamp,
                ClientTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ViewerFps = currentViewerFps,
                ViewerDroppedFrames = 0,
                AvDriftMs = _currentAvDriftMs
            };

            SendTelemetryAsync(stream, telemetryPacket.ToByteArray(), token);
            UpdateTelemetryState(header, currentViewerFps, bitrateMbps);

            state.IntervalFrames = 0;
            state.IntervalBytes = 0;
            state.LastTelemetryTime = DateTime.UtcNow;
        }

        private static void SendTelemetryAsync(Stream stream, byte[] telemetryBytes, CancellationToken token)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await stream.WriteAsync(telemetryBytes.AsMemory(), token);
                    await stream.FlushAsync(token);
                }
                catch { }
            }, token);
        }

        private void UpdateTelemetryState(in VideoHeaderData header, int currentViewerFps, double bitrateMbps)
        {
            CurrentTelemetry.HostFps = header.HostFps;
            CurrentTelemetry.HostFrameTimeMs = header.HostFrameTimeMs;
            CurrentTelemetry.ViewerFps = currentViewerFps;
            CurrentTelemetry.BitrateMbps = bitrateMbps;
            CurrentTelemetry.AvDriftMs = _currentAvDriftMs;

            long transitMs = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - header.Timestamp);
            if (transitMs < 1000)
            {
                CurrentTelemetry.RttMs = (int)transitMs;
            }

            TelemetryUpdated?.Invoke(this, CurrentTelemetry);
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
                    throw new EndOfStreamException("El servidor cerró la conexión.");
                }
                totalBytesRead += bytesRead;
            }
        }

        private Task DisconnectInternalAsync(string reason)
        {
            if (Interlocked.Exchange(ref _isDisconnecting, 1) != 0)
                return Task.CompletedTask;

            _cts?.Cancel();
            try { _stream?.Dispose(); } catch { }
            try { _client?.Close(); _client?.Dispose(); } catch { }

            _stream = null;
            _client = null;
            _cts?.Dispose();
            _cts = null;
            ActiveSessionHandshake = null;

            StatusChanged?.Invoke(this, reason);
            _isDisconnecting = 0;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync() => DisconnectInternalAsync("Desconectado");

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
