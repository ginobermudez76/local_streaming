using System;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using LanScreenShare.Core.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests;

[TestClass]
public class CanonicalSasCalculatorTests
{
    private byte[] _dummyHostCert = null!;
    private byte[] _dummyViewerCert = null!;
    private byte[] _dummyHostNonce = null!;
    private byte[] _dummyViewerNonce = null!;

    [TestInitialize]
    public void Setup()
    {
        _dummyHostCert = new byte[] { 0x30, 0x82, 0x01, 0x0A, 0x01, 0x02, 0x03, 0x04 };
        _dummyViewerCert = new byte[] { 0x30, 0x82, 0x01, 0x14, 0x05, 0x06, 0x07, 0x08 };
        _dummyHostNonce = new byte[16];
        _dummyViewerNonce = new byte[16];
        RandomNumberGenerator.Fill(_dummyHostNonce);
        RandomNumberGenerator.Fill(_dummyViewerNonce);
    }

    [TestMethod]
    public void ComputeSas_DeterministicResult()
    {
        var (code1, str1) = CanonicalSasCalculator.ComputeSas(
            CanonicalSasCalculator.CurrentProtocolVersion,
            _dummyHostCert,
            _dummyViewerCert,
            _dummyHostNonce,
            _dummyViewerNonce);

        var (code2, str2) = CanonicalSasCalculator.ComputeSas(
            CanonicalSasCalculator.CurrentProtocolVersion,
            _dummyHostCert,
            _dummyViewerCert,
            _dummyHostNonce,
            _dummyViewerNonce);

        Assert.AreEqual(code1, code2);
        Assert.AreEqual(str1, str2);
        Assert.IsTrue(code1 < 1_000_000);
        Assert.IsTrue(Regex.IsMatch(str1, @"^\d{3}-\d{3}$"));
    }

    [TestMethod]
    public void ComputeSas_DifferentNonces_ProduceDifferentCodes()
    {
        var (code1, _) = CanonicalSasCalculator.ComputeSas(
            CanonicalSasCalculator.CurrentProtocolVersion,
            _dummyHostCert,
            _dummyViewerCert,
            _dummyHostNonce,
            _dummyViewerNonce);

        byte[] modifiedViewerNonce = (byte[])_dummyViewerNonce.Clone();
        modifiedViewerNonce[0] ^= 0xFF;

        var (code2, _) = CanonicalSasCalculator.ComputeSas(
            CanonicalSasCalculator.CurrentProtocolVersion,
            _dummyHostCert,
            _dummyViewerCert,
            _dummyHostNonce,
            modifiedViewerNonce);

        // Very high probability they are different
        Assert.AreNotEqual(code1, code2);
    }

    [TestMethod]
    public void ComputeSas_DifferentCerts_ProduceDifferentCodes()
    {
        var (code1, _) = CanonicalSasCalculator.ComputeSas(
            CanonicalSasCalculator.CurrentProtocolVersion,
            _dummyHostCert,
            _dummyViewerCert,
            _dummyHostNonce,
            _dummyViewerNonce);

        byte[] modifiedHostCert = (byte[])_dummyHostCert.Clone();
        modifiedHostCert[0] = 0x31;

        var (code2, _) = CanonicalSasCalculator.ComputeSas(
            CanonicalSasCalculator.CurrentProtocolVersion,
            modifiedHostCert,
            _dummyViewerCert,
            _dummyHostNonce,
            _dummyViewerNonce);

        Assert.AreNotEqual(code1, code2);
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void ComputeSas_InvalidHostNonceLength_ThrowsArgumentException()
    {
        byte[] invalidNonce = new byte[15];
        CanonicalSasCalculator.ComputeSas(
            CanonicalSasCalculator.CurrentProtocolVersion,
            _dummyHostCert,
            _dummyViewerCert,
            invalidNonce,
            _dummyViewerNonce);
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void ComputeSas_EmptyCertificate_ThrowsArgumentException()
    {
        CanonicalSasCalculator.ComputeSas(
            CanonicalSasCalculator.CurrentProtocolVersion,
            Array.Empty<byte>(),
            _dummyViewerCert,
            _dummyHostNonce,
            _dummyViewerNonce);
    }
}

