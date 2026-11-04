using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace LanScreenShare.Core.Security;

public class IdentityKeyStorageException : Exception
{
    public IdentityKeyStorageException(string message) : base(message) { }
    public IdentityKeyStorageException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Implementación de almacenamiento y generación de certificados de identidad para Windows
/// protegidos en reposo con DPAPI (CurrentUser) en formato PKCS#12 (PFX).
/// Cumple con la especificación ADR-001.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class WindowsDpapiIdentityKeyStorage : IIdentityKeyStorage
{
    private readonly string _storagePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private X509Certificate2? _cachedCertificate;

    public WindowsDpapiIdentityKeyStorage(string? customFilePath = null)
    {
        if (!string.IsNullOrWhiteSpace(customFilePath))
        {
            _storagePath = customFilePath;
        }
        else
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string appFolder = Path.Combine(appData, "LanScreenShare");
            _storagePath = Path.Combine(appFolder, "identity.pfx.enc");
        }
    }

    public async Task<X509Certificate2> GetOrCreateIdentityCertificateAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_cachedCertificate != null)
            {
                return _cachedCertificate;
            }

            if (File.Exists(_storagePath))
            {
                _cachedCertificate = LoadEncryptedCertificate(_storagePath);
                return _cachedCertificate;
            }

            _cachedCertificate = GenerateAndSaveIdentityCertificate(_storagePath);
            return _cachedCertificate;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ResetIdentityAsync()
    {
        await _lock.WaitAsync();
        try
        {
            _cachedCertificate?.Dispose();
            _cachedCertificate = null;

            if (File.Exists(_storagePath))
            {
                File.Delete(_storagePath);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private static X509Certificate2 LoadEncryptedCertificate(string path)
    {
        try
        {
            byte[] encryptedBytes = File.ReadAllBytes(path);
            byte[] pfxBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
            return X509CertificateLoader.LoadPkcs12(pfxBytes, null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException)
        {
            // ADR-001: No regenerar silenciosamente ante fallo de descifrado o corrupción.
            throw new IdentityKeyStorageException(
                "Error crítico al acceder a la clave de identidad protegida por DPAPI. " +
                "El archivo puede estar corrupto o las credenciales del usuario cambiaron.",
                ex);
        }
    }

    private static X509Certificate2 GenerateAndSaveIdentityCertificate(string path)
    {
        string directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 1. Generación de clave ECDSA P-256
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string machineName = Environment.MachineName.Replace(" ", "-");
        var distinguishedName = new X500DistinguishedName($"CN=LanScreenShare-{machineName}");

        var request = new CertificateRequest(distinguishedName, ecdsa, HashAlgorithmName.SHA256);

        // 2. Extensiones X.509
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));

        var eku = new OidCollection
        {
            new Oid("1.3.6.1.5.5.7.3.1"), // Server Authentication
            new Oid("1.3.6.1.5.5.7.3.2")  // Client Authentication
        };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, critical: true));

        // 3. Auto-firmado con vigencia de 2 años
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5); // Mitigar desviación de reloj
        var notAfter = DateTimeOffset.UtcNow.AddYears(2);
        using var cert = request.CreateSelfSigned(notBefore, notAfter);

        // 4. Exportar a PFX
        byte[] pfxBytes = cert.Export(X509ContentType.Pfx);

        // 5. Cifrar con Windows DPAPI (CurrentUser)
        byte[] encryptedBytes = ProtectedData.Protect(pfxBytes, null, DataProtectionScope.CurrentUser);

        // 6. Escritura atómica a disco (archivo temporal + reemplazo)
        string tempPath = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(tempPath, encryptedBytes);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        // Retornar instancia cargada
        return X509CertificateLoader.LoadPkcs12(pfxBytes, null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }
}
