using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Network.Streaming
{
    public class TcpStreamServer : IStreamServer
    {
        private const uint FrameHeaderMagicJpeg = 0x4C535331; // "LSS1"
        private const uint FrameHeaderMagicH264 = 0x4C535332; // "LSS2"
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private readonly ConcurrentDictionary<string, ClientContext> _clients = new();
        private bool _disposed;

        private class ClientContext
        {
            public TcpClient Client { get; set; } = null!;
            public NetworkStream Stream { get; set; } = null!;
            public Channel<CapturedFrame> Channel { get; set; } = null!;
        }

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

                        // Cola acotada de 1 frame: si el cliente no puede descargar a tiempo, se descarta el frame viejo
                        var channel = Channel.CreateBounded<CapturedFrame>(new BoundedChannelOptions(1)
                        {
                            FullMode = BoundedChannelFullMode.DropOldest
                        });

                        var ctx = new ClientContext
                        {
                            Client = client,
                            Stream = stream,
                            Channel = channel
                        };

                        _clients[clientId] = ctx;
                        Console.WriteLine($"[StreamServer] Cliente conectado: {clientId}");
                        ClientConnected?.Invoke(this, clientId);

                        // Iniciar bomba de envío secuencial dedicada para este cliente
                        _ = PumpClientFramesAsync(clientId, ctx, token);
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

        private async Task PumpClientFramesAsync(string clientId, ClientContext ctx, CancellationToken token)
        {
            byte[] header = new byte[24];
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
                        }

                        await ctx.Stream.WriteAsync(header, 0, header.Length, token);
                        await ctx.Stream.WriteAsync(frame.Data, 0, frame.Data.Length, token);
                        await ctx.Stream.FlushAsync(token);
                    }
                }
            }
            catch
            {
                // Desconexión normal
            }
            finally
            {
                RemoveClient(clientId);
            }
        }

        public Task BroadcastFrameAsync(CapturedFrame frame)
        {
            if (_clients.IsEmpty || frame.Data.Length == 0)
                return Task.CompletedTask;

            foreach (var kvp in _clients)
            {
                kvp.Value.Channel.Writer.TryWrite(frame);
            }

            return Task.CompletedTask;
        }

        private void RemoveClient(string clientId)
        {
            if (_clients.TryRemove(clientId, out var ctx))
            {
                try { ctx.Channel.Writer.Complete(); } catch { }
                try { ctx.Stream.Dispose(); } catch { }
                try { ctx.Client.Close(); } catch { }
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
                try { kvp.Value.Channel.Writer.Complete(); } catch { }
                try { kvp.Value.Stream.Dispose(); } catch { }
                try { kvp.Value.Client.Close(); } catch { }
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
