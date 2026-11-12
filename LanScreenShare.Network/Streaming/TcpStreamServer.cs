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
        public const uint FrameHeaderMagicJpeg = VideoPacketProtocol.MagicJpeg;
        public const uint FrameHeaderMagicH264 = VideoPacketProtocol.MagicH264;
        public const uint FrameHeaderMagicEnd = VideoPacketProtocol.MagicEnd;

        private sealed class ClientContext
        {
            public required TcpClient Client { get; init; }
            public required Stream Stream { get; init; }
            public required Channel<CapturedFrame> Channel { get; init; }
            public string? SessionId { get; init; }
            public int CurrentRttMs { get; set; }
            public int ViewerFps { get; set; }
            public int ViewerDroppedFrames { get; set; }
            public int AvDriftMs { get; set; }
            public long LastTelemetryTimestamp { get; set; }
        }

        private readonly ISessionAuthority? _sessionAuthority;
        private readonly SslServerAuthenticationOptions? _sslServerOptions;
        private readonly IPairedDeviceStore? _pairedDeviceStore;

        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private readonly ConcurrentDictionary<string, ClientContext> _clients = new();
        private bool _disposed;
        private int _currentRttMs;
        private long _droppedFramesCount;

        public event EventHandler<string>? ClientConnected;
        public event EventHandler<string>? ClientDisconnected;
        public event EventHandler? FrameDropped;
        public event EventHandler<StreamTelemetry>? TelemetryUpdated;

        public Func<string, string, string, Task<bool>>? PairingApprovalCallback { get; set; }
        public Func<string, Task<bool>>? ClientApprovalCallback { get; set; }

        public bool IsRunning => _listener != null && _cts != null && !_cts.IsCancellationRequested;
        public int ConnectedClientsCount => _clients.Count;
        public int CurrentRttMs => _currentRttMs;
        public long DroppedFramesCount => Interlocked.Read(ref _droppedFramesCount);
        public StreamTelemetry CurrentTelemetry { get; } = new();

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

            _ = Task.Run(() => AcceptLoopAsync(_listener, token), token);
            return Task.CompletedTask;
        }

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync(token);
                    ConfigureSocket(client);
                    _ = Task.Run(() => HandleIncomingClientAsync(client, token), token);
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
        }

        private static void ConfigureSocket(TcpClient client)
        {
            client.NoDelay = true;
            client.LingerState = new LingerOption(true, 2);
        }

        private readonly record struct HandshakeResult(Stream Stream, string? SessionId);
        private readonly record struct PeerEndpointInfo(string ClientId, string Fingerprint, string DeviceName);

        private async Task HandleIncomingClientAsync(TcpClient client, CancellationToken token)
        {
            var clientId = client.Client.RemoteEndPoint?.ToString() ?? Guid.NewGuid().ToString();

            try
            {
                Stream stream = client.GetStream();
                string? assignedSessionId = null;

                if (_sslServerOptions != null)
                {
                    var handshake = await PerformTlsHandshakeAsync(client, stream, clientId, token);
                    if (handshake == null)
                        return;

                    stream = handshake.Value.Stream;
                    assignedSessionId = handshake.Value.SessionId;
                }

                if (!await IsClientApprovedAsync(client, clientId))
                    return;

                var ctx = CreateClientContext(client, stream, assignedSessionId);
                _clients[clientId] = ctx;
                Console.WriteLine($"[StreamServer] Cliente conectado: {clientId}");
                ClientConnected?.Invoke(this, clientId);

                _ = MonitorClientLifecycleAsync(clientId, ctx, token);
                await PumpClientFramesAsync(clientId, ctx, token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StreamServer] Error con cliente {clientId}: {ex.Message}");
                RemoveClient(clientId);
            }
        }

        private static ClientContext CreateClientContext(TcpClient client, Stream stream, string? assignedSessionId)
        {
            var channel = Channel.CreateBounded<CapturedFrame>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest
            });

            return new ClientContext
            {
                Client = client,
                Stream = stream,
                Channel = channel,
                SessionId = assignedSessionId
            };
        }

        private async Task<bool> IsClientApprovedAsync(TcpClient client, string clientId)
        {
            if (ClientApprovalCallback == null)
                return true;

            bool approved;
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
                return false;
            }

            return true;
        }

        private async Task<HandshakeResult?> PerformTlsHandshakeAsync(
            TcpClient client,
            Stream rawStream,
            string clientId,
            CancellationToken token)
        {
            var sslStream = new SslStream(rawStream, false);
            using var authCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, authCts.Token);

            await sslStream.AuthenticateAsServerAsync(_sslServerOptions!, linkedCts.Token);

            if (sslStream.RemoteCertificate == null)
            {
                Console.WriteLine($"[StreamServer] Conexión rechazada para {clientId}: Falta certificado de cliente.");
                client.Close();
                return null;
            }

            using var peerCert = new X509Certificate2(sslStream.RemoteCertificate);
            string peerFingerprint = TlsConfigurationHelper.ComputeFingerprint(peerCert);
            string peerDeviceName = peerCert.GetNameInfo(X509NameType.SimpleName, false) ?? "RemotePeer";
            var peer = new PeerEndpointInfo(clientId, peerFingerprint, peerDeviceName);

            if (!await EnsureDevicePairedAsync(sslStream, peerCert, peer, linkedCts.Token))
            {
                client.Close();
                return null;
            }

            string? assignedSessionId = null;
            if (_sessionAuthority != null)
            {
                assignedSessionId = await AuthorizeSessionAsync(sslStream, peer, linkedCts.Token);
                if (assignedSessionId == null)
                {
                    client.Close();
                    return null;
                }
            }

            return new HandshakeResult(sslStream, assignedSessionId);
        }

        private async Task<bool> EnsureDevicePairedAsync(
            SslStream sslStream,
            X509Certificate2 peerCert,
            PeerEndpointInfo peer,
            CancellationToken token)
        {
            if (_pairedDeviceStore == null)
                return true;

            bool isPaired = await _pairedDeviceStore.IsDevicePairedAsync(peer.Fingerprint);
            if (isPaired || PairingApprovalCallback == null)
                return true;

            byte[] hostNonce = new byte[16];
            byte[] viewerNonce = new byte[16];
            RandomNumberGenerator.Fill(hostNonce);
            RandomNumberGenerator.Fill(viewerNonce);

            byte[] hostCertBytes = _sslServerOptions!.ServerCertificate is X509Certificate2 hostCertObj
                ? hostCertObj.RawData
                : Array.Empty<byte>();

            var pairingCreds = new PairingCredentials(hostCertBytes, peerCert.RawData, hostNonce, viewerNonce);
            var (_, sasFormatted) = CanonicalSasCalculator.ComputeSas(
                CanonicalSasCalculator.CurrentProtocolVersion,
                in pairingCreds);

            bool approved = await PairingApprovalCallback(peer.Fingerprint, peer.DeviceName, sasFormatted);
            if (!approved)
            {
                Console.WriteLine($"[StreamServer] Emparejamiento rechazado por el anfitrión para {peer.ClientId} ({peer.Fingerprint})");
                await SessionHandshakeMessage.WriteAsync(sslStream, new SessionHandshakeMessage
                {
                    Type = "ERROR",
                    ErrorMessage = "Emparejamiento rechazado por el anfitrión."
                }, token);
                return false;
            }

            await _pairedDeviceStore.AddPairedDeviceAsync(new PairedDevice(
                Guid.NewGuid().ToString("D"),
                peer.DeviceName,
                peer.Fingerprint,
                DateTime.UtcNow,
                DateTime.UtcNow,
                false));

            return true;
        }

        private async Task<string?> AuthorizeSessionAsync(
            SslStream sslStream,
            PeerEndpointInfo peer,
            CancellationToken token)
        {
            var (success, session, failureReason) = await _sessionAuthority!.TryCreateSessionAsync(
                peer.Fingerprint,
                peer.DeviceName);

            if (!success || session == null)
            {
                Console.WriteLine($"[StreamServer] Rechazada creación de sesión para {peer.ClientId}: {failureReason}");
                await SessionHandshakeMessage.WriteAsync(sslStream, new SessionHandshakeMessage
                {
                    Type = "ERROR",
                    ErrorMessage = failureReason ?? "No autorizado."
                }, token);
                return null;
            }

            var audioToken = _sessionAuthority.IssueChannelToken(session.SessionId, ChannelType.Audio);
            var controlToken = _sessionAuthority.IssueChannelToken(session.SessionId, ChannelType.RemoteControl);
            var fileToken = _sessionAuthority.IssueChannelToken(session.SessionId, ChannelType.FileTransfer);

            var okHandshake = new SessionHandshakeMessage
            {
                Type = "SESSION_OK",
                SessionId = session.SessionId,
                AudioToken = audioToken.TokenId,
                ControlToken = controlToken.TokenId,
                FileTransferToken = fileToken.TokenId,
                HostFingerprint = _sslServerOptions!.ServerCertificate is X509Certificate2 hostCert
                    ? TlsConfigurationHelper.ComputeFingerprint(hostCert)
                    : string.Empty
            };

            await SessionHandshakeMessage.WriteAsync(sslStream, okHandshake, token);
            return session.SessionId;
        }

        private async Task MonitorClientLifecycleAsync(string clientId, ClientContext ctx, CancellationToken token)
        {
            byte[] packetBuffer = new byte[StreamTelemetryPacket.PacketSize];
            try
            {
                while (!token.IsCancellationRequested && ctx.Client.Connected)
                {
                    if (!await TryReadFullTelemetryPacketAsync(ctx.Stream, packetBuffer, token))
                        return;

                    ProcessTelemetryPacket(ctx, packetBuffer);
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

        private static async Task<bool> TryReadFullTelemetryPacketAsync(
            Stream stream,
            byte[] packetBuffer,
            CancellationToken token)
        {
            int totalRead = 0;
            while (totalRead < StreamTelemetryPacket.PacketSize)
            {
                int bytesRead = await stream.ReadAsync(
                    packetBuffer.AsMemory(totalRead, StreamTelemetryPacket.PacketSize - totalRead),
                    token);
                if (bytesRead == 0)
                    return false;

                totalRead += bytesRead;
            }
            return true;
        }

        private void ProcessTelemetryPacket(ClientContext ctx, byte[] packetBuffer)
        {
            if (!StreamTelemetryPacket.TryReadFrom(packetBuffer, out var packet) || packet == null)
                return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int sampleRtt = (int)Math.Max(0, now - packet.EchoTimestamp);

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

        private async Task PumpClientFramesAsync(string clientId, ClientContext ctx, CancellationToken token)
        {
            try
            {
                while (await ctx.Channel.Reader.WaitToReadAsync(token))
                {
                    while (ctx.Channel.Reader.TryRead(out var frame))
                    {
                        uint magic = frame.Format == "h264" ? VideoPacketProtocol.MagicH264 : VideoPacketProtocol.MagicJpeg;
                        byte[] header = VideoPacketProtocol.SerializeHeader(new VideoHeaderData(
                            magic,
                            frame.Data.Length,
                            frame.Width,
                            frame.Height,
                            frame.Timestamp,
                            frame.HostFps,
                            frame.HostFrameTimeMs));

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
            byte[] endHeader = VideoPacketProtocol.SerializeHeader(new VideoHeaderData(
                VideoPacketProtocol.MagicEnd,
                0, 0, 0, 0, 0, 0));

            foreach (var kvp in _clients)
            {
                try
                {
                    kvp.Value.Stream.Write(endHeader, 0, endHeader.Length);
                    kvp.Value.Stream.Flush();
                }
                catch { }
            }

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
