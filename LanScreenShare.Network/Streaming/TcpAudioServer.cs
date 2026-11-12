using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Models;
using LanScreenShare.Core.Security;
using LanScreenShare.Network.Security;

namespace LanScreenShare.Network.Streaming
{
    public class TcpAudioServer : IDisposable
    {
        private sealed class AudioClientContext : IDisposable
        {
            public required TcpClient Client { get; init; }
            public required Stream Stream { get; init; }
            public string? SessionId { get; init; }
            public SemaphoreSlim SendLock { get; } = new(1, 1);

            public void Dispose()
            {
                try { Stream.Dispose(); } catch { }
                try { Client.Dispose(); } catch { }
                try { SendLock.Dispose(); } catch { }
            }
        }

        private readonly ISessionAuthority? _sessionAuthority;
        private readonly SslServerAuthenticationOptions? _sslServerOptions;

        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private readonly ConcurrentDictionary<string, AudioClientContext> _clients = new();
        private bool _disposed;

        public event EventHandler<string>? ClientConnected;
        public event EventHandler<string>? ClientDisconnected;
        public event EventHandler<CapturedAudioChunk>? ClientAudioReceived;

        public bool IsRunning => _listener != null && _cts != null && !_cts.IsCancellationRequested;
        public int ConnectedClientsCount => _clients.Count;

        public TcpAudioServer(
            ISessionAuthority? sessionAuthority = null,
            SslServerAuthenticationOptions? sslServerOptions = null)
        {
            _sessionAuthority = sessionAuthority;
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
                    RemoveClient(kvp.Key, kvp.Value.Client);
                }
            }
        }

        public Task StartAsync(int port = 5002, CancellationToken cancellationToken = default)
        {
            if (IsRunning)
                return Task.CompletedTask;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _cts.Token;

            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start();

            Console.WriteLine($"[AudioServer] Escuchando en TCP port {port}...");

            _ = Task.Run(() => AcceptClientsLoopAsync(_listener, token), token);
            return Task.CompletedTask;
        }

        private async Task AcceptClientsLoopAsync(TcpListener listener, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync(token);
                    ConfigureSocket(client);
                    _ = Task.Run(() => HandleClientConnectionAsync(client, token), token);
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
                    Console.WriteLine($"[AudioServer] Error aceptando cliente: {ex.Message}");
                }
            }
        }

        private static void ConfigureSocket(TcpClient client)
        {
            client.NoDelay = true;
            client.SendBufferSize = 64 * 1024;
            client.ReceiveBufferSize = 64 * 1024;
        }

        private async Task HandleClientConnectionAsync(TcpClient client, CancellationToken token)
        {
            var clientId = client.Client.RemoteEndPoint?.ToString() ?? Guid.NewGuid().ToString();

            try
            {
                Stream stream = client.GetStream();
                string? assignedSessionId = null;

                if (_sslServerOptions != null)
                {
                    var tlsResult = await AuthenticateClientAsync(client, stream, clientId, token);
                    if (tlsResult == null)
                    {
                        client.Close();
                        return;
                    }

                    stream = tlsResult.Value.Stream;
                    assignedSessionId = tlsResult.Value.SessionId;
                }

                RegisterClient(clientId, client, stream, assignedSessionId);
                await MonitorClientLifecycleAsync(clientId, client, stream, token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioServer] Error inicializando cliente de audio {clientId}: {ex.Message}");
                RemoveClient(clientId, client);
            }
        }

        private readonly record struct TlsHandshakeResult(Stream Stream, string? SessionId);

        private async Task<TlsHandshakeResult?> AuthenticateClientAsync(
            TcpClient client,
            Stream rawStream,
            string clientId,
            CancellationToken token)
        {
            var sslStream = new SslStream(rawStream, false);
            using var authCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, authCts.Token);

            await sslStream.AuthenticateAsServerAsync(_sslServerOptions!, linkedCts.Token);

            if (sslStream.RemoteCertificate == null)
            {
                Console.WriteLine($"[AudioServer] Conexión rechazada para {clientId}: Falta certificado de cliente.");
                return null;
            }

            using var peerCert = new X509Certificate2(sslStream.RemoteCertificate);
            string peerFingerprint = TlsConfigurationHelper.ComputeFingerprint(peerCert);

            byte[] attachBuffer = new byte[ChannelAttachmentMessage.MessageSize];
            await ReadExactBytesAsync(sslStream, attachBuffer, linkedCts.Token);

            if (!ChannelAttachmentMessage.TryDeserialize(
                    attachBuffer,
                    out var attachPayload,
                    out string? attachError))
            {
                Console.WriteLine($"[AudioServer] Error en CHANNEL_ATTACH de {clientId}: {attachError}");
                return null;
            }

            if (_sessionAuthority != null)
            {
                var status = _sessionAuthority.TryAttachChannel(
                    attachPayload.TokenId,
                    attachPayload.SessionId,
                    peerFingerprint,
                    attachPayload.Channel);

                await sslStream.WriteAsync(new byte[] { (byte)status }, linkedCts.Token);

                if (status != AttachmentStatus.Success)
                {
                    Console.WriteLine($"[AudioServer] Rechazado CHANNEL_ATTACH ({status}) para {clientId}");
                    return null;
                }
            }

            return new TlsHandshakeResult(sslStream, attachPayload.SessionId);
        }

        private void RegisterClient(string clientId, TcpClient client, Stream stream, string? sessionId)
        {
            var clientCtx = new AudioClientContext
            {
                Client = client,
                Stream = stream,
                SessionId = sessionId
            };
            _clients[clientId] = clientCtx;
            Console.WriteLine($"[AudioServer] Cliente de audio conectado: {clientId}");
            ClientConnected?.Invoke(this, clientId);
        }

        private async Task MonitorClientLifecycleAsync(
            string clientId,
            TcpClient client,
            Stream stream,
            CancellationToken token)
        {
            byte[] headerBuffer = new byte[AudioPacketProtocol.HeaderSize];
            try
            {
                while (!token.IsCancellationRequested && client.Connected)
                {
                    await ReadExactBytesAsync(stream, headerBuffer, token);

                    if (!AudioPacketProtocol.TryParseHeader(headerBuffer, out var header))
                    {
                        Console.WriteLine($"[AudioServer] Paquete de audio inválido de {clientId}");
                        break;
                    }

                    byte[] audioData = new byte[header.DataLength];
                    await ReadExactBytesAsync(stream, audioData, token);

                    DispatchClientAudio(header, audioData, clientId);
                }
            }
            catch (OperationCanceledException)
            {
                // Cancelación normal
            }
            catch (EndOfStreamException)
            {
                Console.WriteLine($"[AudioServer] Cliente {clientId} cerró la conexión de audio.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioServer] Conexión de audio con cliente {clientId} finalizada: {ex.Message}");
            }
            finally
            {
                RemoveClient(clientId, client);
            }
        }

        private void DispatchClientAudio(in AudioHeaderData header, byte[] audioData, string clientId)
        {
            var chunk = new CapturedAudioChunk
            {
                Data = audioData,
                SampleRate = header.SampleRate,
                Channels = header.Channels,
                BitsPerSample = header.BitsPerSample,
                AudioFormat = header.AudioFormat,
                Source = header.Source,
                Timestamp = header.Timestamp
            };

            try
            {
                ClientAudioReceived?.Invoke(this, chunk);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AudioServer] Error en suscriptor ClientAudioReceived para {clientId}: {ex.Message}");
            }
        }

        private static bool HasPayload(CapturedAudioChunk chunk) =>
            chunk.Data != null && chunk.Data.Length > 0;

        private bool CanBroadcast(CapturedAudioChunk chunk) =>
            IsRunning && !_clients.IsEmpty && HasPayload(chunk);

        public async Task BroadcastAudioAsync(CapturedAudioChunk chunk)
        {
            if (!CanBroadcast(chunk))
                return;

            byte[] packet = AudioPacketProtocol.SerializePacket(chunk);
            var tasks = _clients.Select(kvp => SendPacketToClientAsync(kvp.Value, kvp.Key, packet));
            await Task.WhenAll(tasks);
        }

        private async Task SendPacketToClientAsync(AudioClientContext clientCtx, string clientId, byte[] packet)
        {
            bool lockTaken = false;
            try
            {
                lockTaken = await clientCtx.SendLock.WaitAsync(100);
                if (lockTaken)
                {
                    await clientCtx.Stream.WriteAsync(packet.AsMemory(0, packet.Length));
                }
            }
            catch
            {
                RemoveClient(clientId, clientCtx.Client);
            }
            finally
            {
                if (lockTaken)
                {
                    try { clientCtx.SendLock.Release(); } catch (ObjectDisposedException) { }
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
                    throw new EndOfStreamException("Cliente desconectó canal de audio.");
                }
                totalBytesRead += bytesRead;
            }
        }

        private void RemoveClient(string clientId, TcpClient? client)
        {
            if (_clients.TryRemove(clientId, out var ctx))
            {
                ctx.Dispose();
                Console.WriteLine($"[AudioServer] Cliente de audio desconectado: {clientId}");
                ClientDisconnected?.Invoke(this, clientId);
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _listener?.Stop(); } catch { }

            foreach (var kvp in _clients)
            {
                kvp.Value.Dispose();
            }
            _clients.Clear();

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
