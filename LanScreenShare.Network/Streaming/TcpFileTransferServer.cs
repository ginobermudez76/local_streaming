using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Models;
using LanScreenShare.Core.Security;
using LanScreenShare.Network.Security;

namespace LanScreenShare.Network.Streaming
{
    public sealed class TcpFileTransferServer : IDisposable
    {
        private readonly ISessionAuthority? _sessionAuthority;
        private readonly SslServerAuthenticationOptions? _sslServerOptions;

        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private readonly ConcurrentDictionary<string, ClientContext> _clients = new();
        private bool _disposed;

        private sealed class ClientContext
        {
            public TcpClient Client { get; set; } = null!;
            public Stream Stream { get; set; } = null!;
            public SemaphoreSlim SendLock { get; } = new(1, 1);
            public ConcurrentDictionary<string, InboundTransfer> InboundTransfers { get; } = new();
            public ConcurrentDictionary<string, TaskCompletionSource<bool>> OutboundAcceptSignals { get; } = new();
        }

        private sealed class InboundTransfer : IDisposable
        {
            public string TransferId { get; set; } = string.Empty;
            public string OriginalFileName { get; set; } = string.Empty;
            public string DestinationPath { get; set; } = string.Empty;
            public long FileSize { get; set; }
            public long ReceivedBytes;
            public FileStream? FileStream { get; set; }
            public Stopwatch Stopwatch { get; } = Stopwatch.StartNew();
            public long LastReportBytes;
            public long LastReportTimeMs;

            public void Dispose()
            {
                FileStream?.Dispose();
                FileStream = null;
            }
        }

        public string DownloadDirectory { get; set; }

        public event EventHandler<FileTransferProgress>? TransferProgress;
        public event EventHandler<FileTransferCompleted>? TransferCompleted;
        public event EventHandler<FileTransferError>? TransferError;
        public event EventHandler<string>? ClientConnected;
        public event EventHandler<string>? ClientDisconnected;

        public bool IsRunning => _listener != null && _cts != null && !_cts.IsCancellationRequested;
        public int ConnectedClientsCount => _clients.Count;

        public TcpFileTransferServer(
            ISessionAuthority? sessionAuthority = null,
            SslServerAuthenticationOptions? sslServerOptions = null)
        {
            _sessionAuthority = sessionAuthority;
            _sslServerOptions = sslServerOptions;

            string downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                "LanScreenShare");
            DownloadDirectory = downloads;
        }

        public Task StartAsync(int port = 5004, CancellationToken cancellationToken = default)
        {
            if (IsRunning)
                return Task.CompletedTask;

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _cts.Token;

            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start();

            Console.WriteLine($"[FileTransferServer] Servidor de transferencia de archivos escuchando en puerto {port}...");

            _ = Task.Run(() => AcceptConnectionsLoopAsync(token), token);
            return Task.CompletedTask;
        }

        private async Task AcceptConnectionsLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener!.AcceptTcpClientAsync(token);
                    client.NoDelay = true;
                    _ = Task.Run(() => HandleIncomingConnectionAsync(client, token), token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        Console.WriteLine($"[FileTransferServer] Error en AcceptTcpClient: {ex.Message}");
                    }
                }
            }
        }

        private async Task<bool> ValidateChannelAttachmentAsync(
            SslStream sslStream,
            string peerFingerprint,
            string clientId,
            CancellationToken token)
        {
            byte[] attachBuffer = new byte[ChannelAttachmentMessage.MessageSize];
            await FileTransferPacket.ReadExactBytesAsync(sslStream, attachBuffer, 0, attachBuffer.Length, token);

            if (!ChannelAttachmentMessage.TryDeserialize(
                    attachBuffer,
                    out string reqSessionId,
                    out string reqTokenId,
                    out var reqChannel,
                    out string? attachError))
            {
                Console.WriteLine($"[FileTransferServer] Error en CHANNEL_ATTACH de {clientId}: {attachError}");
                return false;
            }

            if (reqChannel != ChannelType.FileTransfer)
            {
                Console.WriteLine($"[FileTransferServer] Canal incorrecto en CHANNEL_ATTACH de {clientId}: {reqChannel}");
                return false;
            }

            if (_sessionAuthority != null)
            {
                var status = _sessionAuthority.TryAttachChannel(
                    reqTokenId,
                    reqSessionId,
                    peerFingerprint,
                    reqChannel);

                if (status != AttachmentStatus.Success)
                {
                    await sslStream.WriteAsync(new byte[] { (byte)status }, token);
                    Console.WriteLine($"[FileTransferServer] Rechazado CHANNEL_ATTACH ({status}) para {clientId}");
                    return false;
                }
            }

            await sslStream.WriteAsync(new byte[] { (byte)AttachmentStatus.Success }, token);
            return true;
        }

        private async Task<Stream?> AuthenticateTlsAndChannelAsync(
            TcpClient client,
            Stream stream,
            string clientId,
            CancellationToken token)
        {
            if (_sslServerOptions == null)
                return stream;

            var sslStream = new SslStream(stream, false);
            using var authCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, authCts.Token);

            await sslStream.AuthenticateAsServerAsync(_sslServerOptions, linkedCts.Token);

            if (sslStream.RemoteCertificate == null)
            {
                Console.WriteLine($"[FileTransferServer] Conexión rechazada para {clientId}: Falta certificado.");
                return null;
            }

            using var peerCert = new X509Certificate2(sslStream.RemoteCertificate);
            string peerFingerprint = TlsConfigurationHelper.ComputeFingerprint(peerCert);

            bool attached = await ValidateChannelAttachmentAsync(sslStream, peerFingerprint, clientId, linkedCts.Token);
            return attached ? sslStream : null;
        }

        private void CleanupClient(string clientId, TcpClient client)
        {
            if (_clients.TryRemove(clientId, out var ctx))
            {
                foreach (var inbound in ctx.InboundTransfers.Values)
                {
                    inbound.Dispose();
                }
                ctx.SendLock.Dispose();
            }
            try { client.Close(); } catch { }
            ClientDisconnected?.Invoke(this, clientId);
        }

        private async Task HandleIncomingConnectionAsync(TcpClient client, CancellationToken token)
        {
            var clientId = client.Client.RemoteEndPoint?.ToString() ?? Guid.NewGuid().ToString();
            try
            {
                Stream? stream = await AuthenticateTlsAndChannelAsync(client, client.GetStream(), clientId, token);
                if (stream == null)
                {
                    client.Close();
                    return;
                }

                var ctx = new ClientContext
                {
                    Client = client,
                    Stream = stream
                };

                _clients[clientId] = ctx;
                ClientConnected?.Invoke(this, clientId);
                Console.WriteLine($"[FileTransferServer] Cliente conectado para transferencia de archivos: {clientId}");

                await ProcessClientStreamAsync(ctx, token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FileTransferServer] Error cliente {clientId}: {ex.Message}");
            }
            finally
            {
                CleanupClient(clientId, client);
            }
        }

        private Task DispatchPacketAsync(ClientContext ctx, FileTransferPacketType type, uint payloadLength, CancellationToken ct)
        {
            if (type == FileTransferPacketType.Chunk)
            {
                return HandleChunkPacketAsync(ctx, payloadLength, ct);
            }

            return DispatchControlPacketAsync(ctx, type, payloadLength, ct);
        }

        private Task DispatchControlPacketAsync(ClientContext ctx, FileTransferPacketType type, uint payloadLength, CancellationToken ct) => type switch
        {
            FileTransferPacketType.Offer => HandleOfferPacketAsync(ctx, payloadLength, ct),
            FileTransferPacketType.Accept => HandleAcceptPacketAsync(ctx, payloadLength, ct),
            FileTransferPacketType.Reject => HandleRejectPacketAsync(ctx, payloadLength, ct),
            FileTransferPacketType.Complete => HandleCompletePacketAsync(ctx, payloadLength, ct),
            FileTransferPacketType.Cancel => HandleCancelPacketAsync(ctx, payloadLength, ct),
            _ => Task.CompletedTask
        };

        private async Task ProcessClientStreamAsync(ClientContext ctx, CancellationToken ct)
        {
            byte[] headerBuffer = new byte[FileTransferPacket.HeaderSize];

            while (!ct.IsCancellationRequested)
            {
                await FileTransferPacket.ReadExactBytesAsync(ctx.Stream, headerBuffer, 0, FileTransferPacket.HeaderSize, ct);

                uint magic = BinaryPrimitives.ReadUInt32BigEndian(headerBuffer.AsSpan(0, 4));
                if (magic != FileTransferPacket.Magic)
                {
                    throw new InvalidDataException($"Magic de paquete inválido: 0x{magic:X8}");
                }

                var type = (FileTransferPacketType)headerBuffer[4];
                uint payloadLength = BinaryPrimitives.ReadUInt32BigEndian(headerBuffer.AsSpan(5, 4));

                if (payloadLength > FileTransferPacket.MaxPayloadSize && type == FileTransferPacketType.Chunk)
                {
                    throw new InvalidDataException($"Carga útil de fragmento excede el máximo permitido ({payloadLength} bytes).");
                }

                await DispatchPacketAsync(ctx, type, payloadLength, ct);
            }
        }

        private static async Task<T?> ReadJsonDtoAsync<T>(Stream stream, uint payloadLength, CancellationToken ct)
        {
            byte[] jsonBytes = new byte[payloadLength];
            await FileTransferPacket.ReadExactBytesAsync(stream, jsonBytes, 0, (int)payloadLength, ct);
            return JsonSerializer.Deserialize<T>(jsonBytes);
        }

        private async Task HandleOfferPacketAsync(ClientContext ctx, uint payloadLength, CancellationToken ct)
        {
            var offer = await ReadJsonDtoAsync<FileTransferOfferDto>(ctx.Stream, payloadLength, ct);
            if (offer != null && !string.IsNullOrWhiteSpace(offer.TransferId))
            {
                await HandleInboundOfferAsync(ctx, offer, ct);
            }
        }

        private static async Task HandleAcceptPacketAsync(ClientContext ctx, uint payloadLength, CancellationToken ct)
        {
            var accept = await ReadJsonDtoAsync<FileTransferAcceptDto>(ctx.Stream, payloadLength, ct);
            if (accept != null && ctx.OutboundAcceptSignals.TryGetValue(accept.TransferId, out var tcs))
            {
                tcs.TrySetResult(true);
            }
        }

        private static async Task HandleRejectPacketAsync(ClientContext ctx, uint payloadLength, CancellationToken ct)
        {
            var reject = await ReadJsonDtoAsync<FileTransferRejectDto>(ctx.Stream, payloadLength, ct);
            if (reject != null && ctx.OutboundAcceptSignals.TryGetValue(reject.TransferId, out var tcs))
            {
                tcs.TrySetResult(false);
            }
        }

        private async Task HandleChunkPacketAsync(ClientContext ctx, uint payloadLength, CancellationToken ct)
        {
            byte[] metaBuffer = new byte[24]; // 16 bytes Guid + 8 bytes Offset
            await FileTransferPacket.ReadExactBytesAsync(ctx.Stream, metaBuffer, 0, 24, ct);

            Guid transferId = new(metaBuffer.AsSpan(0, 16));
            long offset = BinaryPrimitives.ReadInt64BigEndian(metaBuffer.AsSpan(16, 8));
            int dataLength = (int)(payloadLength - 24);

            byte[] chunkData = ArrayPool<byte>.Shared.Rent(dataLength);
            try
            {
                await FileTransferPacket.ReadExactBytesAsync(ctx.Stream, chunkData, 0, dataLength, ct);

                string idStr = transferId.ToString("D");
                if (ctx.InboundTransfers.TryGetValue(idStr, out var inbound))
                {
                    await HandleInboundChunkAsync(inbound, offset, chunkData.AsMemory(0, dataLength));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunkData);
            }
        }

        private async Task HandleCompletePacketAsync(ClientContext ctx, uint payloadLength, CancellationToken ct)
        {
            var complete = await ReadJsonDtoAsync<FileTransferCompleteDto>(ctx.Stream, payloadLength, ct);
            if (complete != null && ctx.InboundTransfers.TryRemove(complete.TransferId, out var inbound))
            {
                inbound.Dispose();
                TransferCompleted?.Invoke(this, new FileTransferCompleted
                {
                    TransferId = inbound.TransferId,
                    FileName = inbound.OriginalFileName,
                    FilePath = inbound.DestinationPath,
                    FileSize = inbound.FileSize,
                    IsUpload = false
                });
                Console.WriteLine($"[FileTransferServer] Archivo recibido con éxito: {inbound.DestinationPath} ({inbound.FileSize} bytes)");
            }
        }

        private async Task HandleCancelPacketAsync(ClientContext ctx, uint payloadLength, CancellationToken ct)
        {
            var cancel = await ReadJsonDtoAsync<FileTransferCancelDto>(ctx.Stream, payloadLength, ct);
            if (cancel != null && ctx.InboundTransfers.TryRemove(cancel.TransferId, out var inbound))
            {
                inbound.Dispose();
                try { if (File.Exists(inbound.DestinationPath)) File.Delete(inbound.DestinationPath); } catch { }
                TransferError?.Invoke(this, new FileTransferError
                {
                    TransferId = inbound.TransferId,
                    FileName = inbound.OriginalFileName,
                    ErrorMessage = cancel.Reason ?? "Transferencia cancelada por el remitente.",
                    IsUpload = false
                });
            }
        }

        private async Task HandleInboundOfferAsync(ClientContext ctx, FileTransferOfferDto offer, CancellationToken ct)
        {
            string destination = GetUniqueDestinationPath(DownloadDirectory, offer.FileName);
            var inbound = new InboundTransfer
            {
                TransferId = offer.TransferId,
                OriginalFileName = offer.FileName,
                DestinationPath = destination,
                FileSize = offer.FileSize,
                FileStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true)
            };

            ctx.InboundTransfers[offer.TransferId] = inbound;

            await ctx.SendLock.WaitAsync(ct);
            try
            {
                await FileTransferPacket.WriteAcceptAsync(ctx.Stream, offer.TransferId, ct);
            }
            finally
            {
                ctx.SendLock.Release();
            }

            TransferProgress?.Invoke(this, new FileTransferProgress
            {
                TransferId = offer.TransferId,
                FileName = offer.FileName,
                BytesTransferred = 0,
                TotalBytes = offer.FileSize,
                SpeedBytesPerSec = 0,
                IsUpload = false
            });
        }

        private async Task HandleInboundChunkAsync(InboundTransfer inbound, long offset, ReadOnlyMemory<byte> data)
        {
            if (inbound.FileStream == null) return;

            if (inbound.FileStream.Position != offset)
            {
                inbound.FileStream.Seek(offset, SeekOrigin.Begin);
            }

            await inbound.FileStream.WriteAsync(data);
            long totalReceived = Interlocked.Add(ref inbound.ReceivedBytes, data.Length);

            long nowMs = inbound.Stopwatch.ElapsedMilliseconds;
            if (nowMs - inbound.LastReportTimeMs >= 200 || totalReceived >= inbound.FileSize)
            {
                double elapsedSec = Math.Max(0.001, (nowMs - inbound.LastReportTimeMs) / 1000.0);
                long bytesDiff = totalReceived - inbound.LastReportBytes;
                double speed = bytesDiff / elapsedSec;

                inbound.LastReportTimeMs = nowMs;
                inbound.LastReportBytes = totalReceived;

                TransferProgress?.Invoke(this, new FileTransferProgress
                {
                    TransferId = inbound.TransferId,
                    FileName = inbound.OriginalFileName,
                    BytesTransferred = totalReceived,
                    TotalBytes = inbound.FileSize,
                    SpeedBytesPerSec = speed,
                    IsUpload = false
                });
            }
        }

        private async Task<bool> WaitOfferAcceptedAsync(ClientContext ctx, string transferId, FileInfo fileInfo, CancellationToken ct)
        {
            var acceptTcs = new TaskCompletionSource<bool>();
            ctx.OutboundAcceptSignals[transferId] = acceptTcs;

            await ctx.SendLock.WaitAsync(ct);
            try
            {
                await FileTransferPacket.WriteOfferAsync(ctx.Stream, transferId, fileInfo.Name, fileInfo.Length, ct);
            }
            finally
            {
                ctx.SendLock.Release();
            }

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            using var reg = linked.Token.Register(() => acceptTcs.TrySetResult(false));

            bool accepted = await acceptTcs.Task;
            if (!accepted)
            {
                TransferError?.Invoke(this, new FileTransferError
                {
                    TransferId = transferId,
                    FileName = fileInfo.Name,
                    ErrorMessage = "El receptor rechazó la transferencia.",
                    IsUpload = true
                });
                return false;
            }

            return true;
        }

        private async Task TransmitFileChunksAsync(ClientContext ctx, string transferId, FileInfo fileInfo, CancellationToken ct)
        {
            Guid transferGuid = Guid.Parse(transferId);
            using var fs = new FileStream(fileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, FileTransferPacket.DefaultChunkSize, true);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(FileTransferPacket.DefaultChunkSize);
            long totalSent = 0;
            var sw = Stopwatch.StartNew();
            long lastReportMs = 0;
            long lastReportBytes = 0;

            try
            {
                int read;
                while ((read = await fs.ReadAsync(buffer.AsMemory(0, FileTransferPacket.DefaultChunkSize), ct)) > 0)
                {
                    await ctx.SendLock.WaitAsync(ct);
                    try
                    {
                        await FileTransferPacket.WriteChunkAsync(ctx.Stream, transferGuid, totalSent, buffer.AsMemory(0, read), ct);
                    }
                    finally
                    {
                        ctx.SendLock.Release();
                    }

                    totalSent += read;

                    long nowMs = sw.ElapsedMilliseconds;
                    if (nowMs - lastReportMs >= 200 || totalSent >= fileInfo.Length)
                    {
                        double elapsed = Math.Max(0.001, (nowMs - lastReportMs) / 1000.0);
                        double speed = (totalSent - lastReportBytes) / elapsed;
                        lastReportMs = nowMs;
                        lastReportBytes = totalSent;

                        TransferProgress?.Invoke(this, new FileTransferProgress
                        {
                            TransferId = transferId,
                            FileName = fileInfo.Name,
                            BytesTransferred = totalSent,
                            TotalBytes = fileInfo.Length,
                            SpeedBytesPerSec = speed,
                            IsUpload = true
                        });
                    }
                }

                await ctx.SendLock.WaitAsync(ct);
                try
                {
                    await FileTransferPacket.WriteCompleteAsync(ctx.Stream, transferId, ct);
                }
                finally
                {
                    ctx.SendLock.Release();
                }

                TransferCompleted?.Invoke(this, new FileTransferCompleted
                {
                    TransferId = transferId,
                    FileName = fileInfo.Name,
                    FilePath = fileInfo.FullName,
                    FileSize = fileInfo.Length,
                    IsUpload = true
                });
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private static async Task TrySendCancelAsync(ClientContext ctx, string transferId, string message)
        {
            try
            {
                await ctx.SendLock.WaitAsync(CancellationToken.None);
                try
                {
                    await FileTransferPacket.WriteCancelAsync(ctx.Stream, transferId, message, CancellationToken.None);
                }
                finally
                {
                    ctx.SendLock.Release();
                }
            }
            catch { }
        }

        private async Task<bool> SendFileToClientAsync(ClientContext ctx, string transferId, FileInfo fileInfo, CancellationToken ct)
        {
            try
            {
                if (!await WaitOfferAcceptedAsync(ctx, transferId, fileInfo, ct))
                {
                    return false;
                }

                await TransmitFileChunksAsync(ctx, transferId, fileInfo, ct);
                return true;
            }
            catch (Exception ex)
            {
                await TrySendCancelAsync(ctx, transferId, ex.Message);

                TransferError?.Invoke(this, new FileTransferError
                {
                    TransferId = transferId,
                    FileName = fileInfo.Name,
                    ErrorMessage = ex.Message,
                    IsUpload = true
                });

                return false;
            }
            finally
            {
                ctx.OutboundAcceptSignals.TryRemove(transferId, out _);
            }
        }

        public async Task<bool> SendFileAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("El archivo a enviar no existe.", filePath);

            if (_clients.IsEmpty)
                throw new InvalidOperationException("No hay clientes conectados para recibir el archivo.");

            var fileInfo = new FileInfo(filePath);
            string transferId = Guid.NewGuid().ToString("D");

            foreach (var ctx in _clients.Values)
            {
                if (await SendFileToClientAsync(ctx, transferId, fileInfo, cancellationToken))
                {
                    return true;
                }
            }

            return false;
        }

        public static string GetUniqueDestinationPath(string directory, string originalFileName)
        {
            Directory.CreateDirectory(directory);
            string safeName = Path.GetFileName(originalFileName);
            if (string.IsNullOrWhiteSpace(safeName))
            {
                safeName = $"archivo_{DateTime.Now:yyyyMMdd_HHmmss}.bin";
            }

            string fullPath = Path.Combine(directory, safeName);
            if (!File.Exists(fullPath))
                return fullPath;

            string nameWithoutExt = Path.GetFileNameWithoutExtension(safeName);
            string ext = Path.GetExtension(safeName);

            int count = 1;
            while (true)
            {
                string candidate = Path.Combine(directory, $"{nameWithoutExt} ({count}){ext}");
                if (!File.Exists(candidate))
                    return candidate;
                count++;
            }
        }

        public void Stop()
        {
            _cts?.Cancel();
            _listener?.Stop();
            _listener = null;

            foreach (var ctx in _clients.Values)
            {
                try { ctx.Client.Close(); } catch { }
            }
            _clients.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _cts?.Dispose();
        }
    }
}
