using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using LanScreenShare.Core.Security;
using LanScreenShare.Network.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests;

[TestClass]
public class TlsConfigurationHelperTests
{
    private static X509Certificate2 CreateTestCertificate(string ekuOid, bool isExpired = false)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=TestPeer", ecdsa, HashAlgorithmName.SHA256);

        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));

        var eku = new OidCollection { new Oid(ekuOid) };
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, critical: true));

        var notBefore = isExpired ? DateTimeOffset.UtcNow.AddYears(-3) : DateTimeOffset.UtcNow.AddMinutes(-5);
        var notAfter = isExpired ? DateTimeOffset.UtcNow.AddYears(-1) : DateTimeOffset.UtcNow.AddYears(1);

        return req.CreateSelfSigned(notBefore, notAfter);
    }

    [TestMethod]
    public void ComputeFingerprint_ReturnsSha256HexFormat()
    {
        using var cert = CreateTestCertificate(TlsConfigurationHelper.ClientAuthOid);
        string fingerprint = TlsConfigurationHelper.ComputeFingerprint(cert);

        Assert.IsTrue(fingerprint.StartsWith("SHA256:"));
        Assert.AreEqual(7 + 64, fingerprint.Length); // "SHA256:" (7) + 64 hex characters
    }

    [TestMethod]
    public async Task ValidateCertificate_TrustedPairedDevice_ReturnsTrue()
    {
        using var cert = CreateTestCertificate(TlsConfigurationHelper.ClientAuthOid);
        string fingerprint = TlsConfigurationHelper.ComputeFingerprint(cert);

        var store = new InMemoryPairedDeviceStore();
        await store.AddPairedDeviceAsync(new PairedDevice("d1", "Test", fingerprint, DateTime.UtcNow, DateTime.UtcNow, false));

        bool isValid = TlsConfigurationHelper.ValidateCertificate(
            cert,
            expectedEkuOid: TlsConfigurationHelper.ClientAuthOid,
            deviceStore: store,
            provisionalPairingCallback: null);

        Assert.IsTrue(isValid);
    }

    [TestMethod]
    public void ValidateCertificate_ExpiredCertificate_ReturnsFalse()
    {
        using var cert = CreateTestCertificate(TlsConfigurationHelper.ClientAuthOid, isExpired: true);
        string fingerprint = TlsConfigurationHelper.ComputeFingerprint(cert);

        var store = new InMemoryPairedDeviceStore();

        bool isValid = TlsConfigurationHelper.ValidateCertificate(
            cert,
            expectedEkuOid: TlsConfigurationHelper.ClientAuthOid,
            deviceStore: store,
            provisionalPairingCallback: null);

        Assert.IsFalse(isValid);
    }

    [TestMethod]
    public async Task ValidateCertificate_MismatchedEku_ReturnsFalse()
    {
        // Certificado generado con ServerAuth, pero validado esperando ClientAuth
        using var cert = CreateTestCertificate(TlsConfigurationHelper.ServerAuthOid);
        string fingerprint = TlsConfigurationHelper.ComputeFingerprint(cert);

        var store = new InMemoryPairedDeviceStore();
        await store.AddPairedDeviceAsync(new PairedDevice("d1", "Test", fingerprint, DateTime.UtcNow, DateTime.UtcNow, false));

        bool isValid = TlsConfigurationHelper.ValidateCertificate(
            cert,
            expectedEkuOid: TlsConfigurationHelper.ClientAuthOid,
            deviceStore: store,
            provisionalPairingCallback: null);

        Assert.IsFalse(isValid);
    }

    [TestMethod]
    public void ValidateCertificate_ProvisionalPairingCallback_InvokedForUnpairedDevice()
    {
        using var cert = CreateTestCertificate(TlsConfigurationHelper.ClientAuthOid);
        string fingerprint = TlsConfigurationHelper.ComputeFingerprint(cert);

        var store = new InMemoryPairedDeviceStore(); // Vacío

        bool callbackInvoked = false;
        bool isValid = TlsConfigurationHelper.ValidateCertificate(
            cert,
            expectedEkuOid: TlsConfigurationHelper.ClientAuthOid,
            deviceStore: store,
            provisionalPairingCallback: (fp, c) =>
            {
                callbackInvoked = true;
                return fp == fingerprint;
            });

        Assert.IsTrue(callbackInvoked);
        Assert.IsTrue(isValid);
    }
}

