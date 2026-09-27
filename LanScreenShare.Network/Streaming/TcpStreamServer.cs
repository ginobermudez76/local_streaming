using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Network.Streaming
{
    public class TcpStreamServer : IStreamServer
    {
        private const uint FrameHeaderMagic = 0x4C535331; // "LSS1"
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private readonly ConcurrentDictionary<string, NetworkStream> _clients = new();
        private bool _disposed;

        public event EventHandler<string>? ClientConnected;
        public event EventHandler<string>? ClientDisconnected;

        public bool IsRunning => _listener != null && _cts != null && !_cts.IsCancellationRequested;
        public int ConnectedClientsCount => _clients.Count;

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

                        var clientId = client.Client.RemoteEndPoint?.ToString() ?? Guid.NewGuid().ToString();
                        var stream = client.GetStream();

                        _clients[clientId] = stream;
                        Console.WriteLine($"[StreamServer] Cliente conectado: {clientId}");
                        ClientConnected?.Invoke(this, clientId);

                        // Monitorear desconexión del cliente en segundo plano
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
                        Console.WriteLine($"[StreamServer] Error aceptando cliente: {ex.Message}");
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
                    // Si el cliente cierra el socket, ReadAsync retornará 0
                    int read = await stream.ReadAsync(buffer, 0, 1, token);
                    if (read == 0)
                        break;
                }
            }
            catch
            {
                // Conexión finalizada o error de lectura
            }
            finally
            {
                RemoveClient(clientId, client);
            }
        }

        public async Task BroadcastFrameAsync(CapturedFrame frame)
        {
            if (_clients.IsEmpty || frame.Data.Length == 0)
                return;

            // Formato de encabezado:
            // 4 bytes: Magic (0x4C535331)
            // 4 bytes: Data length
            // 4 bytes: Width
            // 4 bytes: Height
            // 8 bytes: Timestamp
            // Total header = 24 bytes
            byte[] header = new byte[24];
            using (var ms = new MemoryStream(header))
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(FrameHeaderMagic);
                writer.Write(frame.Data.Length);
                writer.Write(frame.Width);
                writer.Write(frame.Height);
                writer.Write(frame.Timestamp);
            }

            var sendTasks = new System.Collections.Generic.List<Task>();
            foreach (var kvp in _clients)
            {
                var clientId = kvp.Key;
                var stream = kvp.Value;

                sendTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await stream.WriteAsync(header, 0, header.Length);
                        await stream.WriteAsync(frame.Data, 0, frame.Data.Length);
                        await stream.FlushAsync();
                    }
                    catch (Exception)
                    {
                        // Si falla la escritura, desconectar cliente
                        _clients.TryRemove(clientId, out _);
                        ClientDisconnected?.Invoke(this, clientId);
                    }
                }));
            }

            await Task.WhenAll(sendTasks);
        }

        private void RemoveClient(string clientId, TcpClient client)
        {
            if (_clients.TryRemove(clientId, out var stream))
            {
                try { stream.Dispose(); } catch { }
                try { client.Close(); } catch { }
                Console.WriteLine($"[StreamServer] Cliente desconectado: {clientId}");
                ClientDisconnected?.Invoke(this, clientId);
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
            try { _listener?.Stop(); } catch { }
            _listener = null;

            foreach (var kvp in _clients)
            {
                try { kvp.Value.Dispose(); } catch { }
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
