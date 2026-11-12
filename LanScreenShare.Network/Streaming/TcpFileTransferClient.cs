using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
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
    public sealed class TcpFileTransferClient : IDisposable
    {
        private TcpClient? _client;
        private Stream? _stream;
        private CancellationTokenSource? _cts;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ConcurrentDictionary<string, InboundTransfer> _inboundTransfers = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _outboundAcceptSignals = new();
        private bool _disposed;

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
        public bool IsConnected => _client != null && _client.Connected && _stream != null;

        public event EventHandler<FileTransferProgress>? TransferProgress;
        public event EventHandler<FileTransferCompleted>? TransferCompleted;
        public event EventHandler<FileTransferError>? TransferError;
        public event EventHandler? Disconnected;

        public TcpFileTransferClient()
        {
            string downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                "LanScreenShare");
            DownloadDirectory = downloads;
        }

        public async Task ConnectAsync(
            string hostIp,
            int port = 5004,
            SslClientAuthenticationOptions? sslOptions = null,
            string? sessionId = null,
            string? attachmentToken = null,
            CancellationToken cancellationToken = default)
        {
            if (IsConnected)
                await DisconnectAsync();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _cts.Token;

            var client = new TcpClient
            {
                NoDelay = true
            };

            await client.ConnectAsync(hostIp, port, token);
            Stream stream = client.GetStream();

            if (sslOptions != null)
            {
                var sslStream = new SslStream(stream, false);
                using var authCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, authCts.Token);

                await sslStream.AuthenticateAsClientAsync(sslOptions, linkedCts.Token);

                if (!string.IsNullOrEmpty(sessionId) && !string.IsNullOrEmpty(attachmentToken))
                {
                    byte[] attachBytes = ChannelAttachmentMessage.Serialize(sessionId, attachmentToken, ChannelType.FileTransfer);
                    await sslStream.WriteAsync(attachBytes, linkedCts.Token);

                    byte[] resp = new byte[1];
                    await FileTransferPacket.ReadExactBytesAsync(sslStream, resp, 0, 1, linkedCts.Token);
                    var status = (AttachmentStatus)resp[0];

                    if (status != AttachmentStatus.Success)
                    {
                        client.Close();
                        throw new InvalidOperationException($"Asociación de canal de transferencia de archivos rechazada por el servidor ({status}).");
                    }
                }

                stream = sslStream;
            }

            _client = client;
            _stream = stream;

            _ = Task.Run(() => ProcessIncomingStreamAsync(token), token);
            Console.WriteLine($"[FileTransferClient] Conectado a {hostIp}:{port} para transferencia de archivos.");
        }

        private async Task ProcessIncomingStreamAsync(CancellationToken ct)
        {
            byte[] headerBuffer = new byte[FileTransferPacket.HeaderSize];

            try
            {
                while (!ct.IsCancellationRequested && _stream != null)
                {
                    await FileTransferPacket.ReadExactBytesAsync(_stream, headerBuffer, 0, FileTransferPacket.HeaderSize, ct);

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

                    switch (type)
                    {
                        case FileTransferPacketType.Offer:
                            await HandleOfferPacketAsync(payloadLength, ct);
                            break;
                        case FileTransferPacketType.Accept:
                            await HandleAcceptPacketAsync(payloadLength, ct);
                            break;
                        case FileTransferPacketType.Reject:
                            await HandleRejectPacketAsync(payloadLength, ct);
                            break;
                        case FileTransferPacketType.Chunk:
                            await HandleChunkPacketAsync(payloadLength, ct);
                            break;
                        case FileTransferPacketType.Complete:
                            await HandleCompletePacketAsync(payloadLength, ct);
                            break;
                        case FileTransferPacketType.Cancel:
                            await HandleCancelPacketAsync(payloadLength, ct);
                            break;
                    }
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Console.WriteLine($"[FileTransferClient] Desconectado del servidor: {ex.Message}");
            }
            finally
            {
                foreach (var inbound in _inboundTransfers.Values)
                {
                    inbound.Dispose();
                }
                _inboundTransfers.Clear();
                Disconnected?.Invoke(this, EventArgs.Empty);
            }
        }

        private static async Task<T?> ReadJsonDtoAsync<T>(Stream stream, uint payloadLength, CancellationToken ct)
        {
            byte[] jsonBytes = new byte[payloadLength];
            await FileTransferPacket.ReadExactBytesAsync(stream, jsonBytes, 0, (int)payloadLength, ct);
            return JsonSerializer.Deserialize<T>(jsonBytes);
        }

        private async Task HandleOfferPacketAsync(uint payloadLength, CancellationToken ct)
        {
            if (_stream == null) return;
            var offer = await ReadJsonDtoAsync<FileTransferOfferDto>(_stream, payloadLength, ct);
            if (offer != null && !string.IsNullOrWhiteSpace(offer.TransferId))
            {
                await HandleInboundOfferAsync(offer, ct);
            }
        }

        private async Task HandleAcceptPacketAsync(uint payloadLength, CancellationToken ct)
        {
            if (_stream == null) return;
            var accept = await ReadJsonDtoAsync<FileTransferAcceptDto>(_stream, payloadLength, ct);
            if (accept != null && _outboundAcceptSignals.TryGetValue(accept.TransferId, out var tcs))
            {
                tcs.TrySetResult(true);
            }
        }

        private async Task HandleRejectPacketAsync(uint payloadLength, CancellationToken ct)
        {
            if (_stream == null) return;
            var reject = await ReadJsonDtoAsync<FileTransferRejectDto>(_stream, payloadLength, ct);
            if (reject != null && _outboundAcceptSignals.TryGetValue(reject.TransferId, out var tcs))
            {
                tcs.TrySetResult(false);
            }
        }

        private async Task HandleChunkPacketAsync(uint payloadLength, CancellationToken ct)
        {
            if (_stream == null) return;
            byte[] metaBuffer = new byte[24];
            await FileTransferPacket.ReadExactBytesAsync(_stream, metaBuffer, 0, 24, ct);

            Guid transferId = new(metaBuffer.AsSpan(0, 16));
            long offset = BinaryPrimitives.ReadInt64BigEndian(metaBuffer.AsSpan(16, 8));
            int dataLength = (int)(payloadLength - 24);

            byte[] chunkData = ArrayPool<byte>.Shared.Rent(dataLength);
            try
            {
                await FileTransferPacket.ReadExactBytesAsync(_stream, chunkData, 0, dataLength, ct);

                string idStr = transferId.ToString("D");
                if (_inboundTransfers.TryGetValue(idStr, out var inbound))
                {
                    await HandleInboundChunkAsync(inbound, offset, chunkData.AsMemory(0, dataLength));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunkData);
            }
        }

        private async Task HandleCompletePacketAsync(uint payloadLength, CancellationToken ct)
        {
            if (_stream == null) return;
            var complete = await ReadJsonDtoAsync<FileTransferCompleteDto>(_stream, payloadLength, ct);
            if (complete != null && _inboundTransfers.TryRemove(complete.TransferId, out var inbound))
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
                Console.WriteLine($"[FileTransferClient] Archivo recibido con éxito: {inbound.DestinationPath} ({inbound.FileSize} bytes)");
            }
        }

        private async Task HandleCancelPacketAsync(uint payloadLength, CancellationToken ct)
        {
            if (_stream == null) return;
            var cancel = await ReadJsonDtoAsync<FileTransferCancelDto>(_stream, payloadLength, ct);
            if (cancel != null && _inboundTransfers.TryRemove(cancel.TransferId, out var inbound))
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

        private async Task HandleInboundOfferAsync(FileTransferOfferDto offer, CancellationToken ct)
        {
            if (_stream == null) return;

            string destination = TcpFileTransferServer.GetUniqueDestinationPath(DownloadDirectory, offer.FileName);
            var inbound = new InboundTransfer
            {
                TransferId = offer.TransferId,
                OriginalFileName = offer.FileName,
                DestinationPath = destination,
                FileSize = offer.FileSize,
                FileStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true)
            };

            _inboundTransfers[offer.TransferId] = inbound;

            await _sendLock.WaitAsync(ct);
            try
            {
                await FileTransferPacket.WriteAcceptAsync(_stream, offer.TransferId, ct);
            }
            finally
            {
                _sendLock.Release();
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

        public async Task<bool> SendFileAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("El archivo a enviar no existe.", filePath);

            if (_stream == null || !IsConnected)
                throw new InvalidOperationException("No hay conexión activa con el anfitrión.");

            var fileInfo = new FileInfo(filePath);
            string transferId = Guid.NewGuid().ToString("D");
            Guid transferGuid = Guid.Parse(transferId);

            var acceptTcs = new TaskCompletionSource<bool>();
            _outboundAcceptSignals[transferId] = acceptTcs;

            try
            {
                await _sendLock.WaitAsync(cancellationToken);
                try
                {
                    await FileTransferPacket.WriteOfferAsync(_stream, transferId, fileInfo.Name, fileInfo.Length, cancellationToken);
                }
                finally
                {
                    _sendLock.Release();
                }

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
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

                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, FileTransferPacket.DefaultChunkSize, true);
                byte[] buffer = ArrayPool<byte>.Shared.Rent(FileTransferPacket.DefaultChunkSize);
                long totalSent = 0;
                var sw = Stopwatch.StartNew();
                long lastReportMs = 0;
                long lastReportBytes = 0;

                try
                {
                    int read;
                    while ((read = await fs.ReadAsync(buffer.AsMemory(0, FileTransferPacket.DefaultChunkSize), cancellationToken)) > 0)
                    {
                        await _sendLock.WaitAsync(cancellationToken);
                        try
                        {
                            await FileTransferPacket.WriteChunkAsync(_stream, transferGuid, totalSent, buffer.AsMemory(0, read), cancellationToken);
                        }
                        finally
                        {
                            _sendLock.Release();
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

                    await _sendLock.WaitAsync(cancellationToken);
                    try
                    {
                        await FileTransferPacket.WriteCompleteAsync(_stream, transferId, cancellationToken);
                    }
                    finally
                    {
                        _sendLock.Release();
                    }

                    TransferCompleted?.Invoke(this, new FileTransferCompleted
                    {
                        TransferId = transferId,
                        FileName = fileInfo.Name,
                        FilePath = filePath,
                        FileSize = fileInfo.Length,
                        IsUpload = true
                    });

                    return true;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            catch (Exception ex)
            {
                if (_stream != null)
                {
                    try
                    {
                        await _sendLock.WaitAsync(CancellationToken.None);
                        try
                        {
                            await FileTransferPacket.WriteCancelAsync(_stream, transferId, ex.Message, CancellationToken.None);
                        }
                        finally
                        {
                            _sendLock.Release();
                        }
                    }
                    catch { }
                }

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
                _outboundAcceptSignals.TryRemove(transferId, out _);
            }
        }

        public Task DisconnectAsync()
        {
            _cts?.Cancel();
            try { _stream?.Dispose(); } catch { }
            try { _client?.Close(); } catch { }
            _stream = null;
            _client = null;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _ = DisconnectAsync();
            _cts?.Dispose();
            _sendLock.Dispose();
        }
    }
}
