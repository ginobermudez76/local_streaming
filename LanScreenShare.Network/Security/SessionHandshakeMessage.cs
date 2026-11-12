using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LanScreenShare.Network.Security;

/// <summary>
/// Mensaje de señalización e intercambio de tokens de sesión sobre el canal primario (5001).
/// Especificación: ADR-001 Sección 4 y Sección 11 (máximo 16 KB por trama JSON).
/// </summary>
public sealed class SessionHandshakeMessage
{
    public const int MaxSignalingPayloadBytes = 16 * 1024; // 16 KB

    public string Type { get; set; } = "SESSION_OK"; // "SESSION_OK", "PROVISIONAL_PAIRING", "ERROR"
    public string SessionId { get; set; } = string.Empty;
    public string AudioToken { get; set; } = string.Empty;
    public string ControlToken { get; set; } = string.Empty;
    public string FileTransferToken { get; set; } = string.Empty;
    public string HostFingerprint { get; set; } = string.Empty;
    public string ErrorMessage { get; set; } = string.Empty;

    public static async Task WriteAsync(Stream stream, SessionHandshakeMessage message, CancellationToken ct)
    {
        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(message);
        if (jsonBytes.Length > MaxSignalingPayloadBytes)
        {
            throw new InvalidOperationException($"La trama de señalización excede el límite de {MaxSignalingPayloadBytes} bytes.");
        }

        byte[] lengthPrefix = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lengthPrefix, (uint)jsonBytes.Length);

        await stream.WriteAsync(lengthPrefix, ct);
        await stream.WriteAsync(jsonBytes, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<SessionHandshakeMessage> ReadAsync(Stream stream, CancellationToken ct)
    {
        byte[] lengthPrefix = new byte[4];
        await ReadExactBytesAsync(stream, lengthPrefix, ct);

        uint length = BinaryPrimitives.ReadUInt32BigEndian(lengthPrefix);
        if (length == 0 || length > MaxSignalingPayloadBytes)
        {
            throw new InvalidDataException($"Longitud de señalización inválida ({length} bytes). Límite: {MaxSignalingPayloadBytes} bytes.");
        }

        byte[] payload = new byte[length];
        await ReadExactBytesAsync(stream, payload, ct);

        var result = JsonSerializer.Deserialize<SessionHandshakeMessage>(payload);
        return result ?? throw new InvalidDataException("No se pudo deserializar el mensaje de señalización JSON.");
    }

    private static async Task ReadExactBytesAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.Slice(total), ct);
            if (read == 0)
            {
                throw new EndOfStreamException("Flujo cerrado antes de completar la lectura del mensaje de señalización.");
            }
            total += read;
        }
    }
}

