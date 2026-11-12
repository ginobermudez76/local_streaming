using System;
using System.Linq;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using LanScreenShare.Core.Security;

namespace LanScreenShare.Network.Security;

/// <summary>
/// Proporciona la configuración de mTLS 1.3 y la validación rigurosa de certificados
/// según la especificación de ADR-001 Sección 7.
/// </summary>
public static class TlsConfigurationHelper
{
    public const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    public const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

    /// <summary>
    /// Calcula la huella digital canónica SHA-256 en formato SHA256:HEX.
    /// </summary>
    public static string ComputeFingerprint(X509Certificate2 cert)
    {
        byte[] hash = SHA256.HashData(cert.RawData);
        return "SHA256:" + Convert.ToHexString(hash);
    }

    /// <summary>
    /// Crea las opciones de autenticación mTLS del servidor para el anfitrión (Host).
    /// </summary>
    public static SslServerAuthenticationOptions CreateServerOptions(
        X509Certificate2 hostCertificate,
        IPairedDeviceStore? deviceStore,
        Func<string, X509Certificate2, bool>? provisionalPairingCallback = null)
    {
        var options = new SslServerAuthenticationOptions
        {
            ServerCertificate = hostCertificate,
            ClientCertificateRequired = true,
            RemoteCertificateValidationCallback = CreateValidationCallback(ClientAuthOid, deviceStore, provisionalPairingCallback)
        };
        ApplyBaseTlsSettings(options);
        return options;
    }

    /// <summary>
    /// Crea las opciones de autenticación mTLS del cliente para el visor (Viewer).
    /// </summary>
    public static SslClientAuthenticationOptions CreateClientOptions(
        X509Certificate2 viewerCertificate,
        string targetHost,
        IPairedDeviceStore? deviceStore,
        Func<string, X509Certificate2, bool>? provisionalPairingCallback = null)
    {
        var options = new SslClientAuthenticationOptions
        {
            ClientCertificates = new X509CertificateCollection { viewerCertificate },
            TargetHost = targetHost,
            LocalCertificateSelectionCallback = (_, _, _, _, _) => viewerCertificate,
            RemoteCertificateValidationCallback = CreateValidationCallback(ServerAuthOid, deviceStore, provisionalPairingCallback)
        };
        ApplyBaseTlsSettings(options);
        return options;
    }

    private static void ApplyBaseTlsSettings(SslServerAuthenticationOptions options)
    {
        options.EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12;
        options.CertificateRevocationCheckMode = X509RevocationMode.NoCheck;
    }

    private static void ApplyBaseTlsSettings(SslClientAuthenticationOptions options)
    {
        options.EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12;
        options.CertificateRevocationCheckMode = X509RevocationMode.NoCheck;
    }

    private static RemoteCertificateValidationCallback CreateValidationCallback(
        string expectedEkuOid,
        IPairedDeviceStore? deviceStore,
        Func<string, X509Certificate2, bool>? provisionalPairingCallback)
    {
        return (_, certificate, _, _) =>
        {
            if (certificate == null) return false;
            using var cert = new X509Certificate2(certificate);
            return ValidateCertificate(cert, expectedEkuOid, deviceStore, provisionalPairingCallback);
        };
    }

    /// <summary>
    /// Ejecuta las 5 reglas de validación de ADR-001 Sección 7:
    /// 1. Vigencia: NotBefore <= UtcNow <= NotAfter.
    /// 2. Longitud de clave: Mínimo ECDSA P-256 o RSA 2048.
    /// 3. EKU requerido.
    /// 4. Huella SHA-256 contrastada contra IPairedDeviceStore o callback provisional.
    /// </summary>
    public static bool ValidateCertificate(
        X509Certificate2 cert,
        string expectedEkuOid,
        IPairedDeviceStore? deviceStore,
        Func<string, X509Certificate2, bool>? provisionalPairingCallback)
    {
        if (!IsCertificatePeriodValid(cert))
        {
            Console.WriteLine("[mTLS] Certificado rechazado: fuera del período de vigencia.");
            return false;
        }

        if (!IsKeyLengthValid(cert))
        {
            return false;
        }

        if (!HasRequiredEku(cert, expectedEkuOid))
        {
            Console.WriteLine($"[mTLS] Certificado rechazado: no contiene la extensión EKU requerida ({expectedEkuOid}).");
            return false;
        }

        string fingerprint = ComputeFingerprint(cert);
        return IsDeviceAuthorized(fingerprint, cert, deviceStore, provisionalPairingCallback);
    }

    private static bool IsCertificatePeriodValid(X509Certificate2 cert)
    {
        var now = DateTime.UtcNow;
        return now >= cert.NotBefore.ToUniversalTime() && now <= cert.NotAfter.ToUniversalTime();
    }

    private static bool IsKeyLengthValid(X509Certificate2 cert)
    {
        return (cert.PublicKey.Oid?.Value) switch
        {
            "1.2.840.10045.2.1" => IsEcdsaValid(cert),
            "1.2.840.113549.1.1.1" => IsRsaValid(cert),
            _ => RejectUnsupportedKey(cert.PublicKey.Oid?.Value)
        };
    }

    private static bool IsEcdsaValid(X509Certificate2 cert)
    {
        using var ecdsa = cert.GetECDsaPublicKey();
        if (ecdsa != null && ecdsa.KeySize >= 256) return true;
        Console.WriteLine("[mTLS] Certificado rechazado: clave ECDSA menor a 256 bits.");
        return false;
    }

    private static bool IsRsaValid(X509Certificate2 cert)
    {
        using var rsa = cert.GetRSAPublicKey();
        if (rsa != null && rsa.KeySize >= 2048) return true;
        Console.WriteLine("[mTLS] Certificado rechazado: clave RSA menor a 2048 bits.");
        return false;
    }

    private static bool RejectUnsupportedKey(string? oid)
    {
        Console.WriteLine($"[mTLS] Algoritmo de clave pública no soportado: {oid}");
        return false;
    }

    private static bool HasRequiredEku(X509Certificate2 cert, string expectedEkuOid) =>
        cert.Extensions.Cast<X509Extension>().Any(ext => MatchesEkuExtension(ext, expectedEkuOid));

    private static bool MatchesEkuExtension(X509Extension ext, string expectedEkuOid)
    {
        if (ext is X509EnhancedKeyUsageExtension ekuExt)
        {
            return ContainsEkuOid(ekuExt.EnhancedKeyUsages, expectedEkuOid);
        }

        if (ext.Oid?.Value == "2.5.29.37")
        {
            var decodedEku = new X509EnhancedKeyUsageExtension(ext, ext.Critical);
            return ContainsEkuOid(decodedEku.EnhancedKeyUsages, expectedEkuOid);
        }

        return false;
    }

    private static bool ContainsEkuOid(OidCollection usages, string expectedEkuOid) =>
        usages.Cast<Oid>().Any(oid => oid.Value == expectedEkuOid);

    private static bool IsDeviceAuthorized(
        string fingerprint,
        X509Certificate2 cert,
        IPairedDeviceStore? deviceStore,
        Func<string, X509Certificate2, bool>? provisionalPairingCallback)
    {
        if (deviceStore != null)
        {
            bool isRevoked = deviceStore.IsDeviceRevokedAsync(fingerprint).GetAwaiter().GetResult();
            if (isRevoked)
            {
                Console.WriteLine($"[mTLS] Certificado rechazado: dispositivo revocado ({fingerprint}).");
                return false;
            }

            bool isPaired = deviceStore.IsDevicePairedAsync(fingerprint).GetAwaiter().GetResult();
            if (isPaired)
            {
                return true;
            }
        }

        if (provisionalPairingCallback != null && provisionalPairingCallback(fingerprint, cert))
        {
            return true;
        }

        Console.WriteLine($"[mTLS] Certificado rechazado: dispositivo no emparejado o revocado ({fingerprint}).");
        return false;
    }
}

