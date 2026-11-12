using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;
using LanScreenShare.Core.Security;
using LanScreenShare.Network.Security;

namespace LanScreenShare.Network.Streaming
{
    public class TcpControlServer : IDisposable
    {
        private readonly ISessionAuthority? _sessionAuthority;
        private readonly IInputSimulatorService? _inputSimulator;
        private readonly SslServerAuthenticationOptions? _sslServerOptions;

        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private readonly ConcurrentDictionary<string, (Stream Stream, TcpClient Client, string? SessionId, SemaphoreSlim SendLock)> _clients = new();
        private ulong _serverSequenceNumber;
        private bool _disposed;

        public event EventHandler<RemoteInputMessage>? InputReceived;
        public event EventHandler<string>? ClipboardTextReceived;
        public event EventHandler<int>? SwitchMonitorRequested;
        public event EventHandler<string>? SelectSourceRequested;
        public event EventHandler? SourceCatalogRequested;
        public event EventHandler<string>? ClientConnected;
        public event EventHandler<string>? ClientDisconnected;
        public Func<string, Task<bool>>? ClientApprovalCallback { get; set; }

        public bool IsRunning => _listener != null && _cts != null && !_cts.IsCancellationRequested;

        public TcpControlServer(
            ISessionAuthority? sessionAuthority = null,
            IInputSimulatorService? inputSimulator = null,
            SslServerAuthenticationOptions? sslServerOptions = null)
        {
            _sessionAuthority = sessionAuthority;
            _inputSimulator = inputSimulator;
            _sslServerOptions = sslServerOptions;

            if (_sessionAuthority != null)
            {
                _sessionAuthority.SessionTerminated += OnSessionTerminated;
            }
        }

        private void OnSessionTerminated(object? sender, string sessionId)
        {
            foreach (var kvp in _clients)
            {
                if (kvp.Value.SessionId == sessionId)
                {
                    RemoveClient(kvp.Key);
                }
            }
        }

        private sealed record ControlClientContext(string ClientId, TcpClient Client, Stream Stream, string? SessionId);

        public Task StartAsync(int port = 5003, CancellationToken cancellationToken = default)
        {
            if (IsRunning)
                return Task.CompletedTask;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _cts.Token;

            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start();

            Console.WriteLine($"[ControlServer] Servidor de control remoto (LSI2) escuchando en puerto {port}...");

            _ = Task.Run(() => AcceptLoopAsync(token), token);
            return Task.CompletedTask;
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener!.AcceptTcpClientAsync(token);
                    client.NoDelay = true;
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
                    Console.WriteLine($"[ControlServer] Error aceptando cliente de control: {ex.Message}");
                }
            }
        }

        private readonly record struct EstablishedClientSession(Stream Stream, string? SessionId);

        private async Task HandleIncomingClientAsync(TcpClient client, CancellationToken token)
        {
            var clientId = client.Client.RemoteEndPoint?.ToString() ?? Guid.NewGuid().ToString();

            try
            {
                var session = await SetupClientSessionAsync(client, clientId, token);
                if (session == null)
                    return;

                _clients[clientId] = (session.Value.Stream, client, session.Value.SessionId, new SemaphoreSlim(1, 1));

                if (_sslServerOptions != null && session.Value.SessionId != null)
                {
                    await session.Value.Stream.WriteAsync(new byte[] { (byte)AttachmentStatus.Success }, token);
                }

                Console.WriteLine($"[ControlServer] Controlador LSI2 conectado: {clientId}");
                ClientConnected?.Invoke(this, clientId);

                var ctx = new ControlClientContext(clientId, client, session.Value.Stream, session.Value.SessionId);
                await ProcessClientInputAsync(ctx, token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ControlServer] Error inicializando cliente {clientId}: {ex.Message}");
                RemoveClient(clientId);
            }
        }

        private async Task<EstablishedClientSession?> SetupClientSessionAsync(
            TcpClient client,
            string clientId,
            CancellationToken token)
        {
            Stream stream = client.GetStream();
            string? assignedSessionId = null;

            if (_sslServerOptions != null)
            {
                var (sslStream, sessionId, success) = await NegotiateTlsSessionAsync(client, stream, clientId, token);
                if (!success || sslStream == null)
                    return null;

                stream = sslStream;
                assignedSessionId = sessionId;
            }

            if (!await IsClientApprovedAsync(client, stream, clientId, token))
                return null;

            if (!TryAcquireControlSession(client, stream, clientId, assignedSessionId))
                return null;

            return new EstablishedClientSession(stream, assignedSessionId);
        }

        private async Task<(SslStream? SslStream, string? SessionId, bool Success)> NegotiateTlsSessionAsync(
            TcpClient client, Stream stream, string clientId, CancellationToken token)
        {
            var sslStream = new SslStream(stream, false);
            using var authCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, authCts.Token);

            await sslStream.AuthenticateAsServerAsync(_sslServerOptions!, linkedCts.Token);

            if (sslStream.RemoteCertificate == null)
            {
                Console.WriteLine($"[ControlServer] Conexión rechazada para {clientId}: Falta certificado de cliente.");
                client.Close();
                return (null, null, false);
            }

            using var peerCert = new X509Certificate2(sslStream.RemoteCertificate);
            string peerFingerprint = TlsConfigurationHelper.ComputeFingerprint(peerCert);

            byte[] attachBuffer = new byte[ChannelAttachmentMessage.MessageSize];
            await ReadExactBytesAsync(sslStream, attachBuffer, linkedCts.Token);

            if (!ChannelAttachmentMessage.TryDeserialize(
                    attachBuffer,
                    out string reqSessionId,
                    out string reqTokenId,
                    out var reqChannel,
                    out string? attachError))
            {
                Console.WriteLine($"[ControlServer] Error en CHANNEL_ATTACH de {clientId}: {attachError}");
                client.Close();
                return (null, null, false);
            }

            if (_sessionAuthority != null)
            {
                var status = _sessionAuthority.TryAttachChannel(reqTokenId, reqSessionId, peerFingerprint, reqChannel);
                if (status != AttachmentStatus.Success)
                {
                    await sslStream.WriteAsync(new byte[] { (byte)status }, linkedCts.Token);
                    Console.WriteLine($"[ControlServer] Rechazado CHANNEL_ATTACH ({status}) para {clientId}");
                    client.Close();
                    return (null, null, false);
                }
            }

            return (sslStream, reqSessionId, true);
        }

        private async Task<bool> IsClientApprovedAsync(TcpClient client, Stream stream, string clientId, CancellationToken token)
        {
            if (ClientApprovalCallback == null) return true;

            bool approved;
            try
            {
                approved = await ClientApprovalCallback(clientId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ControlServer] Error evaluando aprobación para {clientId}: {ex.Message}");
                approved = false;
            }

            if (!approved)
            {
                Console.WriteLine($"[ControlServer] Control remoto denegado por el anfitrión para: {clientId}");
                if (_sslServerOptions != null && _sessionAuthority != null)
                {
                    try { await stream.WriteAsync(new byte[] { (byte)AttachmentStatus.SessionNotFound }, token); } catch { }
                }
                try { client.Close(); } catch { }
                return false;
            }

            return true;
        }

        private bool TryAcquireControlSession(TcpClient client, Stream stream, string clientId, string? assignedSessionId)
        {
            if (_sessionAuthority == null || assignedSessionId == null)
                return true;

            if (!_sessionAuthority.TryAcquireControl(assignedSessionId, out string? acquireError))
            {
                Console.WriteLine($"[ControlServer] Conflicto de controlador único para {clientId}: {acquireError}");
                if (_sslServerOptions != null)
                {
                    try { stream.Write(new byte[] { (byte)AttachmentStatus.SessionNotFound }); } catch { }
                }
                try { client.Close(); } catch { }
                return false;
            }

            return true;
        }

        private bool IsAuthorizedSession(string? sessionId)
        {
            if (_sessionAuthority == null || sessionId == null)
                return true;

            return _sessionAuthority.ActiveControllerSessionId == sessionId;
        }

        private async Task ProcessClientInputAsync(ControlClientContext ctx, CancellationToken token)
        {
            byte[] packetBuffer = new byte[Lsi2ControlPacket.PacketSize];

            try
            {
                while (!token.IsCancellationRequested && ctx.Client.Connected)
                {
                    if (!await ProcessSingleInputPacketAsync(ctx, packetBuffer, token))
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Cancelación esperada
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ControlServer] Conexión de control finalizada ({ctx.ClientId}): {ex.Message}");
            }
            finally
            {
                RemoveClient(ctx.ClientId);
            }
        }

        private async Task<bool> ProcessSingleInputPacketAsync(
            ControlClientContext ctx,
            byte[] packetBuffer,
            CancellationToken token)
        {
            await ReadExactBytesAsync(ctx.Stream, packetBuffer, token);

            if (!Lsi2ControlPacket.TryReadPacket(
                    packetBuffer,
                    out _,
                    out _,
                    out var input,
                    out string? protocolError))
            {
                Console.WriteLine($"[ControlServer] Error de protocolo de {ctx.ClientId}: {protocolError}. Desconectando.");
                return false;
            }

            if (input == null)
                return true;

            return input.Type switch
            {
                InputEventType.ClipboardText => await HandleClipboardTextPacketAsync(ctx, input, token),
                InputEventType.SelectSource => await HandleSelectSourcePacketAsync(ctx, input, token),
                InputEventType.SwitchMonitor => HandleSwitchMonitorPacket(ctx, input),
                _ => HandleStandardInput(ctx, input)
            };
        }

        private bool HandleStandardInput(ControlClientContext ctx, RemoteInputMessage input)
        {
            if (IsAuthorizedSession(ctx.SessionId))
            {
                DispatchInputSafe(input);
            }
            return true;
        }

        private async Task<string?> TryReadTextPayloadAsync(Stream stream, int payloadLength, string clientId, CancellationToken token)
        {
            if (payloadLength <= 0 || payloadLength > Lsi2ControlPacket.MaxClipboardPayloadBytes)
            {
                Console.WriteLine($"[ControlServer] Longitud de payload inválida ({payloadLength}) de {clientId}. Desconectando.");
                return null;
            }

            byte[] textBytes = new byte[payloadLength];
            await ReadExactBytesAsync(stream, textBytes, token);
            return Encoding.UTF8.GetString(textBytes);
        }

        private async Task<bool> HandleClipboardTextPacketAsync(ControlClientContext ctx, RemoteInputMessage input, CancellationToken token)
        {
            string? text = await TryReadTextPayloadAsync(ctx.Stream, input.KeyCode, ctx.ClientId, token);
            if (text == null)
                return false;

            if (!IsAuthorizedSession(ctx.SessionId))
                return true;

            input.ClipboardText = text;

            try { InputReceived?.Invoke(this, input); }
            catch (Exception ex) { Console.WriteLine($"[ControlServer] Error en InputReceived (portapapeles): {ex.Message}"); }

            try { ClipboardTextReceived?.Invoke(this, text); }
            catch (Exception ex) { Console.WriteLine($"[ControlServer] Error en ClipboardTextReceived: {ex.Message}"); }

            return true;
        }

        private async Task<bool> HandleSelectSourcePacketAsync(ControlClientContext ctx, RemoteInputMessage input, CancellationToken token)
        {
            string? sourceId = await TryReadTextPayloadAsync(ctx.Stream, input.KeyCode, ctx.ClientId, token);
            if (sourceId == null)
                return false;

            if (!IsAuthorizedSession(ctx.SessionId))
                return true;

            input.ClipboardText = sourceId;

            try { InputReceived?.Invoke(this, input); }
            catch (Exception ex) { Console.WriteLine($"[ControlServer] Error en InputReceived (SelectSource): {ex.Message}"); }

            try { SelectSourceRequested?.Invoke(this, sourceId); }
            catch (Exception ex) { Console.WriteLine($"[ControlServer] Error en SelectSourceRequested: {ex.Message}"); }

            return true;
        }

        private bool HandleSwitchMonitorPacket(ControlClientContext ctx, RemoteInputMessage input)
        {
            if (!IsAuthorizedSession(ctx.SessionId))
                return true;

            try { InputReceived?.Invoke(this, input); }
            catch (Exception ex) { Console.WriteLine($"[ControlServer] Error en InputReceived (cambio de monitor): {ex.Message}"); }

            try
            {
                if (input.KeyCode == -2)
                {
                    SourceCatalogRequested?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    SwitchMonitorRequested?.Invoke(this, input.KeyCode);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ControlServer] Error en SwitchMonitorRequested: {ex.Message}");
            }

            return true;
        }

        private void DispatchInputSafe(RemoteInputMessage input)
        {
            try
            {
                InputReceived?.Invoke(this, input);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ControlServer] Error en InputReceived: {ex.Message}");
            }

            try
            {
                _inputSimulator?.SimulateInput(input);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ControlServer] Error simulando entrada: {ex.Message}");
            }
        }

        public Task BroadcastClipboardTextAsync(string text) =>
            BroadcastTextPayloadAsync(InputEventType.ClipboardText, text);

        public Task BroadcastMonitorInfoAsync(string monitorLabel) =>
            BroadcastTextPayloadAsync(InputEventType.MonitorInfo, monitorLabel);

        private bool HasActiveClients => IsRunning && !_clients.IsEmpty;

        private bool CanBroadcastText(string text, out byte[] utf8Bytes)
        {
            utf8Bytes = Array.Empty<byte>();
            if (!HasActiveClients || string.IsNullOrEmpty(text))
                return false;

            utf8Bytes = Encoding.UTF8.GetBytes(text);
            return utf8Bytes.Length > 0 && utf8Bytes.Length <= Lsi2ControlPacket.MaxClipboardPayloadBytes;
        }

        private byte[] BuildServerTextPacket(InputEventType payloadType, string text, byte[] utf8Bytes)
        {
            byte[] packet = new byte[Lsi2ControlPacket.PacketSize + utf8Bytes.Length];
            ulong seq = Interlocked.Increment(ref _serverSequenceNumber);
            ulong ts = (ulong)Stopwatch.GetTimestamp();

            var msg = new RemoteInputMessage
            {
                Type = payloadType,
                KeyCode = utf8Bytes.Length,
                ClipboardText = text
            };

            Lsi2ControlPacket.WritePacket(packet.AsSpan(0, Lsi2ControlPacket.PacketSize), seq, ts, msg);
            Buffer.BlockCopy(utf8Bytes, 0, packet, Lsi2ControlPacket.PacketSize, utf8Bytes.Length);
            return packet;
        }

        private async Task BroadcastTextPayloadAsync(InputEventType payloadType, string text)
        {
            if (!CanBroadcastText(text, out var utf8Bytes))
                return;

            byte[] packet = BuildServerTextPacket(payloadType, text, utf8Bytes);
            var tasks = _clients.Select(kvp => SendPayloadToClientAsync(kvp.Key, kvp.Value, packet, payloadType));
            await Task.WhenAll(tasks);
        }

        private bool IsActiveController(string? sessionId)
        {
            if (_sessionAuthority == null || sessionId == null)
                return true;

            return _sessionAuthority.ActiveControllerSessionId == sessionId;
        }

        private async Task SendPayloadToClientAsync(
            string clientId,
            (Stream Stream, TcpClient Client, string? SessionId, SemaphoreSlim SendLock) ctx,
            byte[] packet,
            InputEventType payloadType)
        {
            if (!IsActiveController(ctx.SessionId))
            {
                return;
            }

            try
            {
                await ctx.SendLock.WaitAsync();
                try
                {
                    if (ctx.Client.Connected)
                    {
                        await ctx.Stream.WriteAsync(packet.AsMemory(0, packet.Length));
                    }
                }
                finally
                {
                    ctx.SendLock.Release();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ControlServer] Error enviando payload {payloadType} a {clientId}: {ex.Message}");
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
                    throw new EndOfStreamException("Cliente desconectó canal de control.");
                }
                totalBytesRead += bytesRead;
            }
        }

        private void RemoveClient(string clientId)
        {
            if (_clients.TryRemove(clientId, out var ctx))
            {
                try { ctx.Stream.Dispose(); } catch { }
                try { ctx.Client.Dispose(); } catch { }

                if (_sessionAuthority != null && ctx.SessionId != null)
                {
                    _sessionAuthority.ReleaseRemoteControl(ctx.SessionId);
                }

                // ADR-001 Sección 9: Liberación idempotente de entradas inyectadas ante desconexión
                _inputSimulator?.ReleaseInjectedInputs();

                ClientDisconnected?.Invoke(this, clientId);
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _listener?.Stop(); } catch { }

            foreach (var kvp in _clients)
            {
                try { kvp.Value.Stream.Dispose(); } catch { }
                try { kvp.Value.Client.Dispose(); } catch { }

                if (_sessionAuthority != null && kvp.Value.SessionId != null)
                {
                    _sessionAuthority.ReleaseRemoteControl(kvp.Value.SessionId);
                }
            }
            _clients.Clear();

            _inputSimulator?.ReleaseInjectedInputs();

            _cts?.Dispose();
            _cts = null;
            _listener = null;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            if (_sessionAuthority != null)
            {
                _sessionAuthority.SessionTerminated -= OnSessionTerminated;
            }

            Stop();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
