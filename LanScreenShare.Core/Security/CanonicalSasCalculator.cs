using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace LanScreenShare.Core.Security;

public readonly ref struct PairingCredentials
{
    public ReadOnlySpan<byte> HostCertDer { get; }
    public ReadOnlySpan<byte> ViewerCertDer { get; }
    public ReadOnlySpan<byte> HostNonce { get; }
    public ReadOnlySpan<byte> ViewerNonce { get; }

    public PairingCredentials(
        ReadOnlySpan<byte> hostCertDer,
        ReadOnlySpan<byte> viewerCertDer,
        ReadOnlySpan<byte> hostNonce,
        ReadOnlySpan<byte> viewerNonce)
    {
        HostCertDer = hostCertDer;
        ViewerCertDer = viewerCertDer;
        HostNonce = hostNonce;
        ViewerNonce = viewerNonce;
    }
}

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
    public static byte[] BuildCanonicalTranscript(uint protocolVersion, in PairingCredentials credentials)
    {
        ValidateCredentials(credentials);

        int totalLength = DomainTag.Length + 4 + 1 + 4 + credentials.HostCertDer.Length + 1 + 4 + credentials.ViewerCertDer.Length + (NonceLength * 2);
        byte[] buffer = new byte[totalLength];
        Span<byte> span = buffer.AsSpan();

        int offset = 0;
        offset = AppendHeader(span, offset, protocolVersion);
        offset = AppendRoleAndCert(span, offset, HostRoleByte, credentials.HostCertDer);
        offset = AppendRoleAndCert(span, offset, ViewerRoleByte, credentials.ViewerCertDer);
        offset = AppendNonce(span, offset, credentials.HostNonce);
        AppendNonce(span, offset, credentials.ViewerNonce);

        return buffer;
    }

    private static void ValidateCredentials(in PairingCredentials creds)
    {
        if (creds.HostNonce.Length != NonceLength)
            throw new ArgumentException($"Host nonce debe ser exactamente de {NonceLength} bytes.", nameof(creds));
        if (creds.ViewerNonce.Length != NonceLength)
            throw new ArgumentException($"Viewer nonce debe ser exactamente de {NonceLength} bytes.", nameof(creds));
        if (creds.HostCertDer.IsEmpty)
            throw new ArgumentException("El certificado DER del anfitrión no puede estar vacío.", nameof(creds));
        if (creds.ViewerCertDer.IsEmpty)
            throw new ArgumentException("El certificado DER del visor no puede estar vacío.", nameof(creds));
    }

    private static int AppendHeader(Span<byte> destination, int offset, uint protocolVersion)
    {
        DomainTag.CopyTo(destination.Slice(offset, DomainTag.Length));
        offset += DomainTag.Length;

        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(offset, 4), protocolVersion);
        return offset + 4;
    }

    private static int AppendRoleAndCert(Span<byte> destination, int offset, byte roleByte, ReadOnlySpan<byte> certDer)
    {
        destination[offset++] = roleByte;
        BinaryPrimitives.WriteUInt32BigEndian(destination.Slice(offset, 4), (uint)certDer.Length);
        offset += 4;

        certDer.CopyTo(destination.Slice(offset, certDer.Length));
        return offset + certDer.Length;
    }

    private static int AppendNonce(Span<byte> destination, int offset, ReadOnlySpan<byte> nonce)
    {
        nonce.CopyTo(destination.Slice(offset, NonceLength));
        return offset + NonceLength;
    }

    /// <summary>
    /// Calcula el código SAS de 6 dígitos numérico y con formato XXX-XXX a partir de las credenciales y nonces.
    /// </summary>
    public static (uint Code, string FormattedCode) ComputeSas(uint protocolVersion, in PairingCredentials credentials)
    {
        byte[] transcript = BuildCanonicalTranscript(protocolVersion, in credentials);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(transcript, hash);

        uint rawVal = BinaryPrimitives.ReadUInt32BigEndian(hash.Slice(0, 4)) & 0x7FFFFFFF;
        uint sasCode = rawVal % 1_000_000;
        string sasFormatted = $"{sasCode / 1000:D3}-{sasCode % 1000:D3}";

        return (sasCode, sasFormatted);
    }
}
