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

        private const uint AudioHeaderMagic = 0x4C534131; // "LSA1"
        private readonly ISessionAuthority? _sessionAuthority;
        private readonly SslServerAuthenticationOptions? _sslServerOptions;

        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private readonly ConcurrentDictionary<string, AudioClientContext> _clients = new();
        private bool _disposed;

        public event EventHandler<string>? ClientConnected;
        public event EventHandler<string>? ClientDisconnected;

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
                                        Console.WriteLine($"[AudioServer] Conexión rechazada para {clientId}: Falta certificado de cliente.");
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
                                        Console.WriteLine($"[AudioServer] Error en CHANNEL_ATTACH de {clientId}: {attachError}");
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
                                            Console.WriteLine($"[AudioServer] Rechazado CHANNEL_ATTACH ({status}) para {clientId}");
                                            client.Close();
                                            return;
                                        }

                                        assignedSessionId = reqSessionId;
                                    }

                                    stream = sslStream;
                                }

                                var clientCtx = new AudioClientContext
                                {
                                    Client = client,
                                    Stream = stream,
                                    SessionId = assignedSessionId
                                };
                                _clients[clientId] = clientCtx;
                                Console.WriteLine($"[AudioServer] Cliente de audio conectado: {clientId}");
                                ClientConnected?.Invoke(this, clientId);

                                await MonitorClientLifecycleAsync(clientId, client, stream, token);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[AudioServer] Error inicializando cliente de audio {clientId}: {ex.Message}");
                                RemoveClient(clientId, client);
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
                        Console.WriteLine($"[AudioServer] Error aceptando cliente: {ex.Message}");
                    }
                }
            }, token);

            return Task.CompletedTask;
        }

        private async Task MonitorClientLifecycleAsync(string clientId, TcpClient client, Stream stream, CancellationToken token)
        {
            var buffer = new byte[1];
            try
            {
                while (!token.IsCancellationRequested && client.Connected)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(0, 1), token);
                    if (read == 0)
                        break;
                }
            }
            catch
            {
                // Conexión cerrada
            }
            finally
            {
                RemoveClient(clientId, client);
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
                    throw new EndOfStreamException("Cliente desconectó canal de audio.");
                }
                totalBytesRead += bytesRead;
            }
        }

        public async Task BroadcastAudioAsync(CapturedAudioChunk chunk)
        {
            if (!IsRunning || _clients.IsEmpty || chunk.Data == null || chunk.Data.Length == 0)
                return;

            byte[] packet;
            using (var ms = new MemoryStream(28 + chunk.Data.Length))
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(AudioHeaderMagic);
                writer.Write(chunk.Data.Length);
                writer.Write(chunk.SampleRate);
                writer.Write((short)chunk.Channels);
                writer.Write((short)chunk.BitsPerSample);
                writer.Write(chunk.AudioFormat);
                writer.Write((short)chunk.Source); // 0 = Desktop, 1 = Microphone
                writer.Write(chunk.Timestamp);
                writer.Write(chunk.Data);
                packet = ms.ToArray();
            }

            var tasks = _clients.Select(async kvp =>
            {
                var clientId = kvp.Key;
                var clientCtx = kvp.Value;
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
                        clientCtx.SendLock.Release();
                    }
                }
            });

            await Task.WhenAll(tasks);
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
