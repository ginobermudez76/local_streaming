using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
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

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var client = await _listener.AcceptTcpClientAsync(token);
                        client.NoDelay = true;

                        _ = Task.Run(async () =>
                        {
                            var clientId = client.Client.RemoteEndPoint?.ToString() ?? Guid.NewGuid().ToString();

                            try
                            {
                                Stream stream = client.GetStream();
                                string? assignedSessionId = null;

                                // 1. Negociación mTLS si está habilitada
                                if (_sslServerOptions != null)
                                {
                                    var sslStream = new SslStream(stream, false);
                                    using var authCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, authCts.Token);

                                    await sslStream.AuthenticateAsServerAsync(_sslServerOptions, linkedCts.Token);

                                    if (sslStream.RemoteCertificate == null)
                                    {
                                        Console.WriteLine($"[ControlServer] Conexión rechazada para {clientId}: Falta certificado de cliente.");
                                        client.Close();
                                        return;
                                    }

                                    using var peerCert = new X509Certificate2(sslStream.RemoteCertificate);
                                    string peerFingerprint = TlsConfigurationHelper.ComputeFingerprint(peerCert);

                                    // 2. Leer mensaje de fijación de canal (CHANNEL_ATTACH, 37 bytes)
                                    byte[] attachBuffer = new byte[ChannelAttachmentMessage.MessageSize];
                                    await ReadExactBytesAsync(sslStream, attachBuffer, 0, attachBuffer.Length, linkedCts.Token);

                                    if (!ChannelAttachmentMessage.TryDeserialize(
                                            attachBuffer,
                                            out string reqSessionId,
                                            out string reqTokenId,
                                            out var reqChannel,
                                            out string? attachError))
                                    {
                                        Console.WriteLine($"[ControlServer] Error en CHANNEL_ATTACH de {clientId}: {attachError}");
                                        client.Close();
                                        return;
                                    }

                                    if (_sessionAuthority != null)
                                    {
                                        var status = _sessionAuthority.TryAttachChannel(
                                            reqTokenId,
                                            reqSessionId,
                                            peerFingerprint,
                                            reqChannel);

                                        await sslStream.WriteAsync(new byte[] { (byte)status }, linkedCts.Token);

                                        if (status != AttachmentStatus.Success)
                                        {
                                            Console.WriteLine($"[ControlServer] Rechazado CHANNEL_ATTACH ({status}) para {clientId}");
                                            client.Close();
                                            return;
                                        }

                                        assignedSessionId = reqSessionId;
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
                                        Console.WriteLine($"[ControlServer] Error evaluando aprobación para {clientId}: {ex.Message}");
                                        approved = false;
                                    }

                                    if (!approved)
                                    {
                                        Console.WriteLine($"[ControlServer] Control remoto denegado por el anfitrión para: {clientId}");
                                        try { client.Close(); } catch { }
                                        return;
                                    }
                                }

                                if (_sessionAuthority != null && assignedSessionId != null)
                                {
                                    if (!_sessionAuthority.TryAcquireControl(assignedSessionId, out string? acquireError))
                                    {
                                        Console.WriteLine($"[ControlServer] Conflicto de controlador único para {clientId}: {acquireError}");
                                        try { client.Close(); } catch { }
                                        return;
                                    }
                                }

                                _clients[clientId] = (stream, client, assignedSessionId, new SemaphoreSlim(1, 1));
                                Console.WriteLine($"[ControlServer] Controlador LSI2 conectado: {clientId}");
                                ClientConnected?.Invoke(this, clientId);

                                await ProcessClientInputAsync(clientId, client, stream, assignedSessionId, token);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[ControlServer] Error inicializando cliente {clientId}: {ex.Message}");
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
                        Console.WriteLine($"[ControlServer] Error aceptando cliente de control: {ex.Message}");
                    }
                }
            }, token);

            return Task.CompletedTask;
        }

        private async Task ProcessClientInputAsync(
            string clientId,
            TcpClient client,
            Stream stream,
            string? sessionId,
            CancellationToken token)
        {
            byte[] packetBuffer = new byte[Lsi2ControlPacket.PacketSize];

            try
            {
                while (!token.IsCancellationRequested && client.Connected)
                {
                    await ReadExactBytesAsync(stream, packetBuffer, 0, Lsi2ControlPacket.PacketSize, token);

                    if (!Lsi2ControlPacket.TryReadPacket(
                            packetBuffer,
                            out ulong sequenceNumber,
                            out ulong timestamp,
                            out var input,
                            out string? protocolError))
                    {
                        Console.WriteLine($"[ControlServer] Error de protocolo de {clientId}: {protocolError}. Desconectando.");
                        break;
                    }

                    if (input == null) continue;

                    if (input.Type == InputEventType.ClipboardText)
                    {
                        int payloadLength = input.KeyCode;
                        if (payloadLength <= 0 || payloadLength > Lsi2ControlPacket.MaxClipboardPayloadBytes)
                        {
                            Console.WriteLine($"[ControlServer] Longitud de portapapeles inválida ({payloadLength}) de {clientId}. Desconectando.");
                            break;
                        }

                        byte[] textBytes = new byte[payloadLength];
                        await ReadExactBytesAsync(stream, textBytes, 0, payloadLength, token);

                        if (_sessionAuthority != null && sessionId != null)
                        {
                            string? activeCtrl = _sessionAuthority.ActiveControllerSessionId;
                            if (activeCtrl != sessionId)
                            {
                                continue;
                            }
                        }

                        string text = Encoding.UTF8.GetString(textBytes);
                        input.ClipboardText = text;

                        try
                        {
                            InputReceived?.Invoke(this, input);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[ControlServer] Error en InputReceived (portapapeles): {ex.Message}");
                        }

                        try
                        {
                            ClipboardTextReceived?.Invoke(this, text);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[ControlServer] Error en ClipboardTextReceived: {ex.Message}");
                        }

                        continue;
                    }

                    // Si hay SessionAuthority configurada, comprobar que la sesión es el controlador activo
                    if (_sessionAuthority != null && sessionId != null)
                    {
                        string? activeCtrl = _sessionAuthority.ActiveControllerSessionId;
                        if (activeCtrl != sessionId)
                        {
                            // No tiene el control remoto activo concedido
                            continue;
                        }
                    }

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
            }
            catch (OperationCanceledException)
            {
                // Cancelación esperada
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ControlServer] Conexión de control finalizada ({clientId}): {ex.Message}");
            }
            finally
            {
                RemoveClient(clientId);
            }
        }

        public async Task BroadcastClipboardTextAsync(string text)
        {
            if (!IsRunning || _clients.IsEmpty || string.IsNullOrEmpty(text))
                return;

            byte[] utf8Bytes = Encoding.UTF8.GetBytes(text);
            if (utf8Bytes.Length == 0 || utf8Bytes.Length > Lsi2ControlPacket.MaxClipboardPayloadBytes)
                return;

            byte[] packet = new byte[Lsi2ControlPacket.PacketSize + utf8Bytes.Length];
            ulong seq = Interlocked.Increment(ref _serverSequenceNumber);
            ulong ts = (ulong)Stopwatch.GetTimestamp();

            var msg = new RemoteInputMessage
            {
                Type = InputEventType.ClipboardText,
                KeyCode = utf8Bytes.Length,
                ClipboardText = text
            };

            Lsi2ControlPacket.WritePacket(packet.AsSpan(0, Lsi2ControlPacket.PacketSize), seq, ts, msg);
            Buffer.BlockCopy(utf8Bytes, 0, packet, Lsi2ControlPacket.PacketSize, utf8Bytes.Length);

            foreach (var kvp in _clients)
            {
                var clientId = kvp.Key;
                var ctx = kvp.Value;

                if (_sessionAuthority != null && ctx.SessionId != null)
                {
                    if (_sessionAuthority.ActiveControllerSessionId != ctx.SessionId)
                        continue;
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
                    Console.WriteLine($"[ControlServer] Error enviando portapapeles a {clientId}: {ex.Message}");
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
