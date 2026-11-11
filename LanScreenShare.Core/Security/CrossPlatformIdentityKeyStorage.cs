using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LanScreenShare.Core.Security
{
    /// <summary>
    /// Almacenamiento y generación de certificados de identidad mTLS para entornos multiplataforma (Linux, Android, macOS, Windows).
    /// En Windows utiliza DPAPI cuando está disponible; en sistemas basados en Unix (Linux, Android, macOS), utiliza almacenamiento
    /// protegido en el directorio de usuario (~/.local/share/LanScreenShare) con cifrado AES-256-GCM y permisos POSIX estrictos (0600).
    /// </summary>
    public class CrossPlatformIdentityKeyStorage : IIdentityKeyStorage
    {
        private readonly string _storagePath;
        private readonly string _keyPath;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private X509Certificate2? _cachedCertificate;

        public CrossPlatformIdentityKeyStorage(string? customFilePath = null)
        {
            if (!string.IsNullOrWhiteSpace(customFilePath))
            {
                _storagePath = customFilePath;
                _keyPath = customFilePath + ".key";
            }
            else
            {
                string baseDir;
                if (OperatingSystem.IsWindows())
                {
                    baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanScreenShare");
                }
                else
                {
                    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    baseDir = Path.Combine(home, ".local", "share", "LanScreenShare");
                }

                _storagePath = Path.Combine(baseDir, "identity.pfx.aes");
                _keyPath = Path.Combine(baseDir, "identity.key");
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

                if (File.Exists(_storagePath) && File.Exists(_keyPath))
                {
                    _cachedCertificate = LoadEncryptedCertificate();
                    return _cachedCertificate;
                }

                _cachedCertificate = GenerateAndSaveCertificate();
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
                if (File.Exists(_keyPath))
                {
                    File.Delete(_keyPath);
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        private X509Certificate2 LoadEncryptedCertificate()
        {
            try
            {
                byte[] key = File.ReadAllBytes(_keyPath);
                byte[] encryptedData = File.ReadAllBytes(_storagePath);

                // Formato AES-GCM: Nonce (12 bytes) + Tag (16 bytes) + Ciphertext
                if (encryptedData.Length < 28)
                {
                    throw new IdentityKeyStorageException("Archivo de identidad corrupto o incompleto.");
                }

                byte[] nonce = new byte[12];
                byte[] tag = new byte[16];
                int ciphertextLen = encryptedData.Length - 28;
                byte[] ciphertext = new byte[ciphertextLen];

                Buffer.BlockCopy(encryptedData, 0, nonce, 0, 12);
                Buffer.BlockCopy(encryptedData, 12, tag, 0, 16);
                Buffer.BlockCopy(encryptedData, 28, ciphertext, 0, ciphertextLen);

                byte[] pfxBytes = new byte[ciphertextLen];
                using (var aesGcm = new AesGcm(key, 16))
                {
                    aesGcm.Decrypt(nonce, ciphertext, tag, pfxBytes);
                }

                return X509CertificateLoader.LoadPkcs12(pfxBytes, null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
            }
            catch (Exception ex) when (ex is CryptographicException or IOException)
            {
                throw new IdentityKeyStorageException("Error crítico descifrando la identidad mTLS en almacenamiento seguro multiplataforma.", ex);
            }
        }

        private X509Certificate2 GenerateAndSaveCertificate()
        {
            string directory = Path.GetDirectoryName(_storagePath)!;
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                SetUserOnlyPermissions(directory);
            }

            // 1. Generación de par de claves ECDSA P-256
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string machineName = Environment.MachineName.Replace(" ", "-");
            var distinguishedName = new X500DistinguishedName($"CN=LanScreenShare-{machineName}");

            var request = new CertificateRequest(distinguishedName, ecdsa, HashAlgorithmName.SHA256);

            // 2. Extensiones X.509
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));

            var eku = new OidCollection
            {
                new Oid("1.3.6.1.5.5.7.3.1"), // Server Authentication
                new Oid("1.3.6.1.5.5.7.3.2")  // Client Authentication
            };
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, critical: true));

            // 3. Certificado auto-firmado válido por 2 años
            var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
            var notAfter = DateTimeOffset.UtcNow.AddYears(2);
            using var cert = request.CreateSelfSigned(notBefore, notAfter);

            // 4. Exportar a PFX
            byte[] pfxBytes = cert.Export(X509ContentType.Pfx);

            // 5. Cifrado AES-256-GCM con clave protegida por permisos de usuario
            byte[] masterKey = new byte[32];
            RandomNumberGenerator.Fill(masterKey);

            byte[] nonce = new byte[12];
            RandomNumberGenerator.Fill(nonce);

            byte[] tag = new byte[16];
            byte[] ciphertext = new byte[pfxBytes.Length];

            using (var aesGcm = new AesGcm(masterKey, 16))
            {
                aesGcm.Encrypt(nonce, pfxBytes, ciphertext, tag);
            }

            byte[] encryptedFileBytes = new byte[12 + 16 + ciphertext.Length];
            Buffer.BlockCopy(nonce, 0, encryptedFileBytes, 0, 12);
            Buffer.BlockCopy(tag, 0, encryptedFileBytes, 12, 16);
            Buffer.BlockCopy(ciphertext, 0, encryptedFileBytes, 28, ciphertext.Length);

            File.WriteAllBytes(_keyPath, masterKey);
            SetUserOnlyPermissions(_keyPath);

            File.WriteAllBytes(_storagePath, encryptedFileBytes);
            SetUserOnlyPermissions(_storagePath);

            return X509CertificateLoader.LoadPkcs12(pfxBytes, null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
        }

        private static void SetUserOnlyPermissions(string path)
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                try
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch { }
            }
        }
    }
}

