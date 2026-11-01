using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Network.Streaming
{
    public class TcpAudioServer : IDisposable
    {
        private const uint AudioHeaderMagic = 0x4C534131; // "LSA1"
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private readonly ConcurrentDictionary<string, NetworkStream> _clients = new();
        private bool _disposed;

        public event EventHandler<string>? ClientConnected;
        public event EventHandler<string>? ClientDisconnected;

        public bool IsRunning => _listener != null && _cts != null && !_cts.IsCancellationRequested;
        public int ConnectedClientsCount => _clients.Count;

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

                        var clientId = client.Client.RemoteEndPoint?.ToString() ?? Guid.NewGuid().ToString();
                        var stream = client.GetStream();

                        _clients[clientId] = stream;
                        Console.WriteLine($"[AudioServer] Cliente de audio conectado: {clientId}");
                        ClientConnected?.Invoke(this, clientId);

                        _ = MonitorClientLifecycleAsync(clientId, client, stream, token);
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

        private async Task MonitorClientLifecycleAsync(string clientId, TcpClient client, NetworkStream stream, CancellationToken token)
        {
            var buffer = new byte[1];
            try
            {
                while (!token.IsCancellationRequested && client.Connected)
                {
                    int read = await stream.ReadAsync(buffer, 0, 1, token);
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
                writer.Write((short)0); // Reservado
                writer.Write(chunk.Timestamp);
                writer.Write(chunk.Data);
                packet = ms.ToArray();
            }

            foreach (var kvp in _clients)
            {
                var clientId = kvp.Key;
                var stream = kvp.Value;

                try
                {
                    await stream.WriteAsync(packet, 0, packet.Length);
                }
                catch
                {
                    RemoveClient(clientId, null);
                }
            }
        }

        private void RemoveClient(string clientId, TcpClient? client)
        {
            if (_clients.TryRemove(clientId, out var stream))
            {
                try
                {
                    stream.Dispose();
                }
                catch { }

                try
                {
                    client?.Dispose();
                }
                catch { }

                Console.WriteLine($"[AudioServer] Cliente desconectado: {clientId}");
                ClientDisconnected?.Invoke(this, clientId);
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
            _listener?.Stop();

            foreach (var kvp in _clients)
            {
                try
                {
                    kvp.Value.Dispose();
                }
                catch { }
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

            Stop();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}

