using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace LanScreenShare.Core.Security;

/// <summary>
/// Proporciona el cálculo canónico y determinista del código SAS (Short Authentication String)
/// de 6 dígitos para el emparejamiento provisional, según la especificación de ADR-001.
/// </summary>
public static class CanonicalSasCalculator
{
    private static readonly byte[] DomainTag = Encoding.UTF8.GetBytes("LanScreenShare-Pairing-v1");
    public const uint CurrentProtocolVersion = 1;
    public const byte HostRoleByte = 0x01;
    public const byte ViewerRoleByte = 0x02;
    public const int NonceLength = 16;

    /// <summary>
    /// Construye la transcripción canónica delimitada por longitudes en formato Big-Endian.
    /// </summary>
    public static byte[] BuildCanonicalTranscript(
        uint protocolVersion,
        ReadOnlySpan<byte> hostCertDer,
        ReadOnlySpan<byte> viewerCertDer,
        ReadOnlySpan<byte> hostNonce,
        ReadOnlySpan<byte> viewerNonce)
    {
        if (hostNonce.Length != NonceLength)
            throw new ArgumentException($"Host nonce debe ser exactamente de {NonceLength} bytes.", nameof(hostNonce));
        if (viewerNonce.Length != NonceLength)
            throw new ArgumentException($"Viewer nonce debe ser exactamente de {NonceLength} bytes.", nameof(viewerNonce));
        if (hostCertDer.IsEmpty)
            throw new ArgumentException("El certificado DER del anfitrión no puede estar vacío.", nameof(hostCertDer));
        if (viewerCertDer.IsEmpty)
            throw new ArgumentException("El certificado DER del visor no puede estar vacío.", nameof(viewerCertDer));

        // Longitud total:
        // DomainTag.Length + 4 (Version) + 1 (HostRole) + 4 (HostCertLen) + HostCertDer.Length
        // + 1 (ViewerRole) + 4 (ViewerCertLen) + ViewerCertDer.Length + 16 (HostNonce) + 16 (ViewerNonce)
        int totalLength = DomainTag.Length + 4 + 1 + 4 + hostCertDer.Length + 1 + 4 + viewerCertDer.Length + NonceLength + NonceLength;
        byte[] buffer = new byte[totalLength];
        Span<byte> span = buffer.AsSpan();

        int offset = 0;

        // 1. DomainTag
        DomainTag.CopyTo(span.Slice(offset, DomainTag.Length));
        offset += DomainTag.Length;

        // 2. ProtocolVersion (uint32 BE)
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(offset, 4), protocolVersion);
        offset += 4;

        // 3. HostRole (0x01)
        span[offset++] = HostRoleByte;

        // 4. HostCertLen (uint32 BE)
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(offset, 4), (uint)hostCertDer.Length);
        offset += 4;

        // 5. HostCertDer
        hostCertDer.CopyTo(span.Slice(offset, hostCertDer.Length));
        offset += hostCertDer.Length;

        // 6. ViewerRole (0x02)
        span[offset++] = ViewerRoleByte;

        // 7. ViewerCertLen (uint32 BE)
        BinaryPrimitives.WriteUInt32BigEndian(span.Slice(offset, 4), (uint)viewerCertDer.Length);
        offset += 4;

        // 8. ViewerCertDer
        viewerCertDer.CopyTo(span.Slice(offset, viewerCertDer.Length));
        offset += viewerCertDer.Length;

        // 9. HostNonce
        hostNonce.CopyTo(span.Slice(offset, NonceLength));
        offset += NonceLength;

        // 10. ViewerNonce
        viewerNonce.CopyTo(span.Slice(offset, NonceLength));
        offset += NonceLength;

        return buffer;
    }

    /// <summary>
    /// Calcula el código SAS de 6 dígitos numérico y con formato XXX-XXX a partir de las credenciales y nonces.
    /// </summary>
    public static (uint Code, string FormattedCode) ComputeSas(
        uint protocolVersion,
        ReadOnlySpan<byte> hostCertDer,
        ReadOnlySpan<byte> viewerCertDer,
        ReadOnlySpan<byte> hostNonce,
        ReadOnlySpan<byte> viewerNonce)
    {
        byte[] transcript = BuildCanonicalTranscript(protocolVersion, hostCertDer, viewerCertDer, hostNonce, viewerNonce);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(transcript, hash);

        uint rawVal = BinaryPrimitives.ReadUInt32BigEndian(hash.Slice(0, 4)) & 0x7FFFFFFF;
        uint sasCode = rawVal % 1_000_000;
        string sasFormatted = $"{sasCode / 1000:D3}-{sasCode % 1000:D3}";

        return (sasCode, sasFormatted);
    }
}

