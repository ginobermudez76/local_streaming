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
    public class TcpControlServer : IDisposable
    {
        private const uint ControlHeaderMagic = 0x4C534931; // "LSI1" -> Lan Screen Input v1
        private const int PacketSize = 20;

        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private readonly ConcurrentDictionary<string, NetworkStream> _clients = new();
        private bool _disposed;

        public event EventHandler<RemoteInputMessage>? InputReceived;
        public event EventHandler<string>? ClientConnected;
        public event EventHandler<string>? ClientDisconnected;
        public Func<string, Task<bool>>? ClientApprovalCallback { get; set; }

        public bool IsRunning => _listener != null && _cts != null && !_cts.IsCancellationRequested;

        public Task StartAsync(int port = 5003, CancellationToken cancellationToken = default)
        {
            if (IsRunning)
                return Task.CompletedTask;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _cts.Token;

            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start();

            Console.WriteLine($"[ControlServer] Servidor de control remoto escuchando en puerto {port}...");

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
                        var stream = client.GetStream();

                        _clients[clientId] = stream;
                        Console.WriteLine($"[ControlServer] Controlador conectado: {clientId}");
                        ClientConnected?.Invoke(this, clientId);

                        _ = ProcessClientInputAsync(clientId, client, stream, token);
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

        private async Task ProcessClientInputAsync(string clientId, TcpClient client, NetworkStream stream, CancellationToken token)
        {
            byte[] packetBuffer = new byte[PacketSize];

            try
            {
                while (!token.IsCancellationRequested && client.Connected)
                {
                    await ReadExactBytesAsync(stream, packetBuffer, 0, PacketSize, token);

                    using var ms = new MemoryStream(packetBuffer);
                    using var reader = new BinaryReader(ms);

                    uint magic = reader.ReadUInt32();
                    if (magic != ControlHeaderMagic)
                    {
                        Console.WriteLine($"[ControlServer] Paquete de control inválido (Magic: {magic:X})");
                        continue;
                    }

                    var input = new RemoteInputMessage
                    {
                        Type = (InputEventType)reader.ReadByte(),
                        NormalizedX = reader.ReadSingle(),
                        NormalizedY = reader.ReadSingle(),
                        Button = (RemoteMouseButton)reader.ReadByte(),
                        WheelDelta = reader.ReadInt16(),
                        KeyCode = reader.ReadInt32()
                    };

                    InputReceived?.Invoke(this, input);
                    if (input.Type != InputEventType.MouseMove)
                    {
                        Console.WriteLine($"[ControlServer] Procesado input: {input.Type} btn={input.Button} key={input.KeyCode}");
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
                RemoveClient(clientId, client);
            }
        }

        private static async Task ReadExactBytesAsync(NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken token)
        {
            int totalBytesRead = 0;
            while (totalBytesRead < count)
            {
                int bytesRead = await stream.ReadAsync(buffer, offset + totalBytesRead, count - totalBytesRead, token);
                if (bytesRead == 0)
                {
                    throw new EndOfStreamException("Cliente desconectó canal de control.");
                }
                totalBytesRead += bytesRead;
            }
        }

        private void RemoveClient(string clientId, TcpClient? client)
        {
            if (_clients.TryRemove(clientId, out var stream))
            {
                try { stream.Dispose(); } catch { }
                try { client?.Dispose(); } catch { }
                ClientDisconnected?.Invoke(this, clientId);
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
            _listener?.Stop();

            foreach (var kvp in _clients)
            {
                try { kvp.Value.Dispose(); } catch { }
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
