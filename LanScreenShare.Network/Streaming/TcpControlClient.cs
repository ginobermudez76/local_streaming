using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Network.Streaming
{
    public class TcpControlClient : IDisposable
    {
        private const uint ControlHeaderMagic = 0x4C534931; // "LSI1"
        private const int PacketSize = 20;

        private TcpClient? _client;
        private NetworkStream? _stream;
        private CancellationTokenSource? _cts;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private bool _disposed;

        public event EventHandler<string>? StatusChanged;
        public bool IsConnected => _client != null && _client.Connected && _stream != null;

        public async Task ConnectAsync(string hostIp, int port = 5003, CancellationToken cancellationToken = default)
        {
            if (IsConnected)
                await DisconnectAsync();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _cts.Token;

            StatusChanged?.Invoke(this, $"Conectando control remoto a {hostIp}:{port}...");

            _client = new TcpClient();
            _client.NoDelay = true;

            await _client.ConnectAsync(hostIp, port, token);
            _stream = _client.GetStream();

            StatusChanged?.Invoke(this, $"Control remoto conectado a {hostIp}:{port}");
        }

        public async Task SendInputAsync(RemoteInputMessage input)
        {
            if (!IsConnected || _stream == null)
                return;

            byte[] buffer = new byte[PacketSize];
            using (var ms = new MemoryStream(buffer))
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(ControlHeaderMagic);
                writer.Write((byte)input.Type);
                writer.Write(input.NormalizedX);
                writer.Write(input.NormalizedY);
                writer.Write((byte)input.Button);
                writer.Write(input.WheelDelta);
                writer.Write(input.KeyCode);
            }

            await _sendLock.WaitAsync();
            try
            {
                if (IsConnected && _stream != null)
                {
                    await _stream.WriteAsync(buffer, 0, buffer.Length);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ControlClient] Error enviando entrada: {ex.Message}");
                await DisconnectAsync();
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public Task DisconnectAsync()
        {
            _cts?.Cancel();
            try { _stream?.Dispose(); } catch { }
            try { _client?.Close(); _client?.Dispose(); } catch { }

            _stream = null;
            _client = null;
            _cts?.Dispose();
            _cts = null;

            StatusChanged?.Invoke(this, "Control remoto desconectado");
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _ = DisconnectAsync();
            _sendLock.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
