using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;
using LanScreenShare.Core.Security;
using LanScreenShare.Network.Security;

namespace LanScreenShare.Network.Streaming
{
    public class TcpStreamServer : IStreamServer
    {
        public const uint FrameHeaderMagicJpeg = 0x4C535331; // "LSS1"
        public const uint FrameHeaderMagicH264 = 0x4C535332; // "LSS2"
        public const uint FrameHeaderMagicEnd  = 0x4C535330; // "LSS0"

        private readonly ISessionAuthority? _sessionAuthority;
        private readonly SslServerAuthenticationOptions? _sslServerOptions;
        private readonly IPairedDeviceStore? _pairedDeviceStore;

        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private readonly ConcurrentDictionary<string, ClientContext> _clients = new();
        private bool _disposed;

        private class ClientContext
        {
            public TcpClient Client { get; set; } = null!;
            public Stream Stream { get; set; } = null!;
            public Channel<CapturedFrame> Channel { get; set; } = null!;
            public string? SessionId { get; set; }
            public int CurrentRttMs { get; set; }
            public int ViewerFps { get; set; }
            public int ViewerDroppedFrames { get; set; }
            public int AvDriftMs { get; set; }
            public long LastTelemetryTimestamp { get; set; }
        }

        public event EventHandler<string>? ClientConnected;
        public event EventHandler<string>? ClientDisconnected;
        public event EventHandler<StreamTelemetry>? TelemetryUpdated;
        public event EventHandler? FrameDropped;
        public Func<string, Task<bool>>? ClientApprovalCallback { get; set; }
        public Func<string, string, string, Task<bool>>? PairingApprovalCallback { get; set; }

        public bool IsRunning => _listener != null && _cts != null && !_cts.IsCancellationRequested;
        public int ConnectedClientsCount => _clients.Count;
        private long _droppedFramesCount;
        public long DroppedFramesCount => Interlocked.Read(ref _droppedFramesCount);
        public StreamTelemetry CurrentTelemetry { get; } = new();
        private int _currentRttMs;
        public int CurrentRttMs => _currentRttMs;

        public TcpStreamServer(
            ISessionAuthority? sessionAuthority = null,
            SslServerAuthenticationOptions? sslServerOptions = null,
            IPairedDeviceStore? pairedDeviceStore = null)
        {
            _sessionAuthority = sessionAuthority;
            _sslServerOptions = sslServerOptions;
            _pairedDeviceStore = pairedDeviceStore;
        }

        public Task StartAsync(int port = 5001, CancellationToken cancellationToken = default)
        {
            if (IsRunning)
                return Task.CompletedTask;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _cts.Token;

            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start();

            Console.WriteLine($"[StreamServer] Escuchando en TCP port {port}...");

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var client = await _listener.AcceptTcpClientAsync(token);
                        client.NoDelay = true; // Deshabilitar Nagle para menor latencia
                        client.LingerState = new LingerOption(true, 2); // Evitar RST prematuro al cerrar socket

                        _ = Task.Run(async () =>
                        {
                            var clientId = client.Client.RemoteEndPoint?.ToString() ?? Guid.NewGuid().ToString();

                            try
                            {
                                Stream stream = client.GetStream();
                                string? assignedSessionId = null;

                                // 1. Negociación mTLS 1.3 / 1.2 si está habilitada
                                if (_sslServerOptions != null)
                                {
                                    var sslStream = new SslStream(stream, false);
                                    using var authCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, authCts.Token);

                                    await sslStream.AuthenticateAsServerAsync(_sslServerOptions, linkedCts.Token);

                                    if (sslStream.RemoteCertificate == null)
                                    {
                                        Console.WriteLine($"[StreamServer] Conexión rechazada para {clientId}: Falta certificado de cliente.");
                                        client.Close();
                                        return;
                                    }

                                    using var peerCert = new X509Certificate2(sslStream.RemoteCertificate);
                                    string peerFingerprint = TlsConfigurationHelper.ComputeFingerprint(peerCert);
                                    string peerDeviceName = peerCert.GetNameInfo(X509NameType.SimpleName, false) ?? "RemotePeer";

                                    if (_pairedDeviceStore != null)
                                    {
                                        bool isPaired = await _pairedDeviceStore.IsDevicePairedAsync(peerFingerprint);
                                        if (!isPaired && PairingApprovalCallback != null)
                                        {
                                            byte[] hostNonce = new byte[16];
                                            byte[] viewerNonce = new byte[16];
                                            RandomNumberGenerator.Fill(hostNonce);
                                            RandomNumberGenerator.Fill(viewerNonce);

                                            byte[] hostCertBytes = _sslServerOptions.ServerCertificate is X509Certificate2 hostCertObj
                                                ? hostCertObj.RawData
                                                : Array.Empty<byte>();

                                            var (sasCode, sasFormatted) = CanonicalSasCalculator.ComputeSas(
                                                CanonicalSasCalculator.CurrentProtocolVersion,
                                                hostCertBytes,
                                                peerCert.RawData,
                                                hostNonce,
                                                viewerNonce);

                                            bool approved = await PairingApprovalCallback(peerFingerprint, peerDeviceName, sasFormatted);
                                            if (!approved)
                                            {
                                                Console.WriteLine($"[StreamServer] Emparejamiento rechazado por el anfitrión para {clientId} ({peerFingerprint})");
                                                var errHandshake = new SessionHandshakeMessage
                                                {
                                                    Type = "ERROR",
                                                    ErrorMessage = "Emparejamiento rechazado por el anfitrión."
                                                };
                                                await SessionHandshakeMessage.WriteAsync(sslStream, errHandshake, linkedCts.Token);
                                                client.Close();
                                                return;
                                            }

                                            // Registrar dispositivo emparejado de confianza
                                            await _pairedDeviceStore.AddPairedDeviceAsync(new PairedDevice(
                                                Guid.NewGuid().ToString("D"),
                                                peerDeviceName,
                                                peerFingerprint,
                                                DateTime.UtcNow,
                                                DateTime.UtcNow,
                                                false));
                                        }
                                    }

                                    if (_sessionAuthority != null)
                                    {
                                        var (success, session, failureReason) = await _sessionAuthority.TryCreateSessionAsync(
                                            peerFingerprint,
                                            peerDeviceName);

                                        if (!success || session == null)
                                        {
                                            Console.WriteLine($"[StreamServer] Rechazada creación de sesión para {clientId}: {failureReason}");
                                            var errHandshake = new SessionHandshakeMessage
                                            {
                                                Type = "ERROR",
                                                ErrorMessage = failureReason ?? "No autorizado."
                                            };
                                            await SessionHandshakeMessage.WriteAsync(sslStream, errHandshake, linkedCts.Token);
                                            client.Close();
                                            return;
                                        }

                                        assignedSessionId = session.SessionId;

                                        // Emitir tokens de asociación para Audio (5002) y Control (5003)
                                        var audioToken = _sessionAuthority.IssueChannelToken(session.SessionId, ChannelType.Audio);
                                        var controlToken = _sessionAuthority.IssueChannelToken(session.SessionId, ChannelType.RemoteControl);

                                        var okHandshake = new SessionHandshakeMessage
                                        {
                                            Type = "SESSION_OK",
                                            SessionId = session.SessionId,
                                            AudioToken = audioToken.TokenId,
                                            ControlToken = controlToken.TokenId,
                                            HostFingerprint = _sslServerOptions.ServerCertificate is X509Certificate2 hostCert
                                                ? TlsConfigurationHelper.ComputeFingerprint(hostCert)
                                                : string.Empty
                                        };

                                        await SessionHandshakeMessage.WriteAsync(sslStream, okHandshake, linkedCts.Token);
                                    }

                                    stream = sslStream;
                                }

                                if (ClientApprovalCallback != null)
                                {
                                    bool approved = false;
                                    try
                                    {
                                        approved = await ClientApprovalCallback(clientId);
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"[StreamServer] Error evaluando aprobación para {clientId}: {ex.Message}");
                                        approved = false;
                                    }

                                    if (!approved)
                                    {
                                        Console.WriteLine($"[StreamServer] Conexión rechazada por el anfitrión para: {clientId}");
                                        try { client.Close(); } catch { }
                                        return;
                                    }
                                }

                                // Cola acotada de 1 frame: si el cliente no puede descargar a tiempo, se descarta el frame viejo
                                var channel = Channel.CreateBounded<CapturedFrame>(new BoundedChannelOptions(1)
                                {
                                    FullMode = BoundedChannelFullMode.DropOldest
                                });

                                var ctx = new ClientContext
                                {
                                    Client = client,
                                    Stream = stream,
                                    Channel = channel,
                                    SessionId = assignedSessionId
                                };

                                _clients[clientId] = ctx;
                                Console.WriteLine($"[StreamServer] Cliente conectado: {clientId}");
                                ClientConnected?.Invoke(this, clientId);

                                // Iniciar monitor de ciclo de vida del socket para detectar desconexión inmediata
                                _ = MonitorClientLifecycleAsync(clientId, ctx, token);

                                // Iniciar bomba de envío secuencial dedicada para este cliente
                                await PumpClientFramesAsync(clientId, ctx, token);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[StreamServer] Error con cliente {clientId}: {ex.Message}");
                                RemoveClient(clientId);
                            }
                        }, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[StreamServer] Error aceptando cliente: {ex.Message}");
                    }
                }
            }, token);

            return Task.CompletedTask;
        }

        private async Task MonitorClientLifecycleAsync(string clientId, ClientContext ctx, CancellationToken token)
        {
            byte[] packetBuffer = new byte[StreamTelemetryPacket.PacketSize];
            try
            {
                while (!token.IsCancellationRequested && ctx.Client.Connected)
                {
                    int totalRead = 0;
                    while (totalRead < StreamTelemetryPacket.PacketSize)
                    {
                        int bytesRead = await ctx.Stream.ReadAsync(
                            packetBuffer.AsMemory(totalRead, StreamTelemetryPacket.PacketSize - totalRead),
                            token);
                        if (bytesRead == 0)
                        {
                            // Socket cerrado por el cliente
                            return;
                        }
                        totalRead += bytesRead;
                    }

                    if (StreamTelemetryPacket.TryReadFrom(packetBuffer, out var packet) && packet != null)
                    {
                        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        int sampleRtt = (int)Math.Max(0, now - packet.EchoTimestamp);

                        // Filtro EMA para suavizar RTT
                        int smoothedRtt = _currentRttMs == 0
                            ? sampleRtt
                            : (int)(_currentRttMs * 0.75 + sampleRtt * 0.25);

                        _currentRttMs = smoothedRtt;
                        ctx.CurrentRttMs = smoothedRtt;
                        ctx.ViewerFps = packet.ViewerFps;
                        ctx.ViewerDroppedFrames = packet.ViewerDroppedFrames;
                        ctx.AvDriftMs = packet.AvDriftMs;
                        ctx.LastTelemetryTimestamp = now;

                        CurrentTelemetry.RttMs = smoothedRtt;
                        CurrentTelemetry.ViewerFps = packet.ViewerFps;
                        CurrentTelemetry.DroppedFrames = DroppedFramesCount + packet.ViewerDroppedFrames;
                        CurrentTelemetry.AvDriftMs = packet.AvDriftMs;

                        TelemetryUpdated?.Invoke(this, CurrentTelemetry);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Conexión cerrada
            }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    RemoveClient(clientId);
                }
            }
        }

        private async Task PumpClientFramesAsync(string clientId, ClientContext ctx, CancellationToken token)
        {
            byte[] header = new byte[32];
            try
            {
                while (await ctx.Channel.Reader.WaitToReadAsync(token))
                {
                    while (ctx.Channel.Reader.TryRead(out var frame))
                    {
                        using (var ms = new MemoryStream(header))
                        using (var writer = new BinaryWriter(ms))
                        {
                            uint magic = frame.Format == "h264" ? FrameHeaderMagicH264 : FrameHeaderMagicJpeg;
                            writer.Write(magic);
                            writer.Write(frame.Data.Length);
                            writer.Write(frame.Width);
                            writer.Write(frame.Height);
                            writer.Write(frame.Timestamp);
                            writer.Write(frame.HostFps);
                            writer.Write(frame.HostFrameTimeMs);
                        }

                        await ctx.Stream.WriteAsync(header.AsMemory(0, header.Length), token);
                        await ctx.Stream.WriteAsync(frame.Data.AsMemory(0, frame.Data.Length), token);
                        await ctx.Stream.FlushAsync(token);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // Desconexión normal
            }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    RemoveClient(clientId);
                }
            }
        }

        public Task BroadcastFrameAsync(CapturedFrame frame)
        {
            if (_clients.IsEmpty || frame.Data.Length == 0)
                return Task.CompletedTask;

            CurrentTelemetry.HostFps = frame.HostFps;
            CurrentTelemetry.HostFrameTimeMs = frame.HostFrameTimeMs;

            bool anyDropped = false;
            foreach (var kvp in _clients)
            {
                // Si la cola acotada ya contiene un fotograma en espera, se incrementa el contador de descartes
                if (kvp.Value.Channel.Reader.Count > 0)
                {
                    Interlocked.Increment(ref _droppedFramesCount);
                    anyDropped = true;
                }
                kvp.Value.Channel.Writer.TryWrite(frame);
            }

            if (anyDropped)
            {
                FrameDropped?.Invoke(this, EventArgs.Empty);
            }

            CurrentTelemetry.DroppedFrames = DroppedFramesCount;
            return Task.CompletedTask;
        }

        private void RemoveClient(string clientId)
        {
            if (_clients.TryRemove(clientId, out var ctx))
            {
                try { ctx.Channel.Writer.Complete(); } catch { }
                try { ctx.Stream.Dispose(); } catch { }
                try { ctx.Client.Close(); } catch { }

                // ADR-001 Sección 8: Invalidación en cascada de la sesión y todos sus tokens asociados
                if (ctx.SessionId != null && _sessionAuthority != null)
                {
                    _sessionAuthority.CloseSession(ctx.SessionId);
                }

                Console.WriteLine($"[StreamServer] Cliente desconectado: {clientId}");
                ClientDisconnected?.Invoke(this, clientId);
            }
        }

        public void Stop()
        {
            // Notificar a todos los espectadores que la transmisión finaliza explícitamente (LSS0)
            byte[] endHeader = new byte[32];
            using (var ms = new MemoryStream(endHeader))
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(FrameHeaderMagicEnd);
                writer.Write(0); // dataLength = 0
            }

            foreach (var kvp in _clients)
            {
                try
                {
                    kvp.Value.Stream.Write(endHeader, 0, endHeader.Length);
                    kvp.Value.Stream.Flush();
                }
                catch
                {
                    // Ignorar si el cliente ya cerró el socket
                }
            }

            // Pausa breve (60ms) para garantizar que el buffer TCP drene el encabezado LSS0 antes del cierre del socket
            Thread.Sleep(60);

            _cts?.Cancel();
            try { _listener?.Stop(); } catch { }
            _listener = null;

            foreach (var kvp in _clients)
            {
                try { kvp.Value.Channel.Writer.Complete(); } catch { }
                try { kvp.Value.Client.Client.Shutdown(SocketShutdown.Both); } catch { }
                try { kvp.Value.Stream.Dispose(); } catch { }
                try { kvp.Value.Client.Close(); } catch { }

                if (kvp.Value.SessionId != null && _sessionAuthority != null)
                {
                    _sessionAuthority.CloseSession(kvp.Value.SessionId);
                }
            }
            _clients.Clear();

            _cts?.Dispose();
            _cts = null;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            Stop();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
