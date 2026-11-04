using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using LanScreenShare.Core.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests;

[TestClass]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class WindowsDpapiIdentityKeyStorageTests
{
    private string _tempFilePath = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempFilePath = Path.Combine(Path.GetTempPath(), $"identity_test_{Guid.NewGuid():N}.pfx.enc");
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (File.Exists(_tempFilePath))
        {
            try { File.Delete(_tempFilePath); } catch { }
        }
    }

    [TestMethod]
    public async Task GetOrCreateIdentityCertificateAsync_CreatesValidEcdsaCertificate()
    {
        if (!OperatingSystem.IsWindows()) return;

        var storage = new WindowsDpapiIdentityKeyStorage(_tempFilePath);
        using var cert = await storage.GetOrCreateIdentityCertificateAsync();

        Assert.IsNotNull(cert);
        Assert.IsTrue(cert.HasPrivateKey);
        Assert.IsTrue(cert.Subject.Contains("LanScreenShare-"));
        Assert.IsTrue(File.Exists(_tempFilePath));

        // Verificar ECDSA P-256
        using var ecdsa = cert.GetECDsaPrivateKey();
        Assert.IsNotNull(ecdsa);

        // Verificar validez (aproximadamente 2 años)
        Assert.IsTrue(cert.NotAfter > DateTime.UtcNow.AddYears(1));
    }

    [TestMethod]
    public async Task GetOrCreateIdentityCertificateAsync_PersistsAndReloadsAcrossInstances()
    {
        if (!OperatingSystem.IsWindows()) return;

        string thumbprint1;
        {
            var storage1 = new WindowsDpapiIdentityKeyStorage(_tempFilePath);
            using var cert1 = await storage1.GetOrCreateIdentityCertificateAsync();
            thumbprint1 = cert1.Thumbprint;
        }

        // Nueva instancia leyendo del mismo archivo cifrado
        {
            var storage2 = new WindowsDpapiIdentityKeyStorage(_tempFilePath);
            using var cert2 = await storage2.GetOrCreateIdentityCertificateAsync();
            Assert.AreEqual(thumbprint1, cert2.Thumbprint);
            Assert.IsTrue(cert2.HasPrivateKey);
        }
    }

    [TestMethod]
    public async Task GetOrCreateIdentityCertificateAsync_CorruptFile_ThrowsIdentityKeyStorageException()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Escribir datos corruptos directamente al archivo
        File.WriteAllBytes(_tempFilePath, new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 });

        var storage = new WindowsDpapiIdentityKeyStorage(_tempFilePath);

        // Debe lanzar IdentityKeyStorageException y NO regenerar silenciosamente
        await Assert.ThrowsExceptionAsync<IdentityKeyStorageException>(async () =>
        {
            await storage.GetOrCreateIdentityCertificateAsync();
        });
    }

    [TestMethod]
    public async Task ResetIdentityAsync_DeletesFile()
    {
        if (!OperatingSystem.IsWindows()) return;

        var storage = new WindowsDpapiIdentityKeyStorage(_tempFilePath);
        using (await storage.GetOrCreateIdentityCertificateAsync()) { }

        Assert.IsTrue(File.Exists(_tempFilePath));

        await storage.ResetIdentityAsync();
        Assert.IsFalse(File.Exists(_tempFilePath));
    }
}
