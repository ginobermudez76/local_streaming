using System;
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
        return new SslServerAuthenticationOptions
        {
            ServerCertificate = hostCertificate,
            ClientCertificateRequired = true,
            EnabledSslProtocols = SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
            {
                if (certificate == null) return false;
                using var clientCert = new X509Certificate2(certificate);

                return ValidateCertificate(
                    clientCert,
                    expectedEkuOid: ClientAuthOid,
                    deviceStore: deviceStore,
                    provisionalPairingCallback: provisionalPairingCallback);
            }
        };
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
        return new SslClientAuthenticationOptions
        {
            ClientCertificates = new X509CertificateCollection { viewerCertificate },
            TargetHost = targetHost,
            EnabledSslProtocols = SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
            {
                if (certificate == null) return false;
                using var hostCert = new X509Certificate2(certificate);

                return ValidateCertificate(
                    hostCert,
                    expectedEkuOid: ServerAuthOid,
                    deviceStore: deviceStore,
                    provisionalPairingCallback: provisionalPairingCallback);
            }
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
        var now = DateTime.UtcNow;

        // 1. Vigencia
        if (now < cert.NotBefore.ToUniversalTime() || now > cert.NotAfter.ToUniversalTime())
        {
            Console.WriteLine("[mTLS] Certificado rechazado: fuera del período de vigencia.");
            return false;
        }

        // 2. Longitud y tipo de clave
        if (cert.PublicKey.Oid.Value == "1.2.840.10045.2.1") // id-ecPublicKey
        {
            using var ecdsa = cert.GetECDsaPublicKey();
            if (ecdsa == null || ecdsa.KeySize < 256)
            {
                Console.WriteLine("[mTLS] Certificado rechazado: clave ECDSA menor a 256 bits.");
                return false;
            }
        }
        else if (cert.PublicKey.Oid.Value == "1.2.840.113549.1.1.1") // rsaEncryption
        {
            using var rsa = cert.GetRSAPublicKey();
            if (rsa == null || rsa.KeySize < 2048)
            {
                Console.WriteLine("[mTLS] Certificado rechazado: clave RSA menor a 2048 bits.");
                return false;
            }
        }
        else
        {
            Console.WriteLine($"[mTLS] Algoritmo de clave pública no soportado: {cert.PublicKey.Oid.Value}");
            return false;
        }

        // 3. EKU requerido
        bool hasEku = false;
        foreach (var ext in cert.Extensions)
        {
            if (ext is X509EnhancedKeyUsageExtension ekuExt)
            {
                foreach (var oid in ekuExt.EnhancedKeyUsages)
                {
                    if (oid.Value == expectedEkuOid)
                    {
                        hasEku = true;
                        break;
                    }
                }
            }
        }

        if (!hasEku)
        {
            Console.WriteLine($"[mTLS] Certificado rechazado: no contiene la extensión EKU requerida ({expectedEkuOid}).");
            return false;
        }

        // 4. Huella y autorización en IPairedDeviceStore
        string fingerprint = ComputeFingerprint(cert);

        if (deviceStore != null)
        {
            bool isPaired = deviceStore.IsDevicePairedAsync(fingerprint).GetAwaiter().GetResult();
            bool isRevoked = deviceStore.IsDeviceRevokedAsync(fingerprint).GetAwaiter().GetResult();

            if (isPaired && !isRevoked)
            {
                return true;
            }
        }

        // 5. Si no está emparejado, comprobar si se permite emparejamiento provisional
        if (provisionalPairingCallback != null && provisionalPairingCallback(fingerprint, cert))
        {
            return true;
        }

        Console.WriteLine($"[mTLS] Certificado rechazado: dispositivo no emparejado o revocado ({fingerprint}).");
        return false;
    }
}
