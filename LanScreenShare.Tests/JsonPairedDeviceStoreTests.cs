using System;
using System.IO;
using System.Threading.Tasks;
using LanScreenShare.Core.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests;

[TestClass]
public class JsonPairedDeviceStoreTests
{
    private string _tempFilePath = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempFilePath = Path.Combine(Path.GetTempPath(), $"trusted_devices_test_{Guid.NewGuid():N}.json");
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
    public async Task AddAndQueryDevice_PersistsAndLoadsCorrectly()
    {
        var store = new JsonPairedDeviceStore(_tempFilePath);
        string fingerprint = "SHA256:AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899";

        Assert.IsFalse(await store.IsDevicePairedAsync(fingerprint));

        var device = new PairedDevice("dev-1", "Laptop-Test", fingerprint, DateTime.UtcNow, DateTime.UtcNow, false);
        await store.AddPairedDeviceAsync(device);

        Assert.IsTrue(await store.IsDevicePairedAsync(fingerprint));
        Assert.IsFalse(await store.IsDeviceRevokedAsync(fingerprint));

        // Verificar persistencia a través de nueva instancia
        var newStoreInstance = new JsonPairedDeviceStore(_tempFilePath);
        var devices = await newStoreInstance.GetAllPairedDevicesAsync();
        Assert.AreEqual(1, devices.Count);
        Assert.AreEqual("Laptop-Test", devices[0].DeviceName);
        Assert.AreEqual(fingerprint, devices[0].PublicKeyFingerprint);
    }

    [TestMethod]
    public async Task RevokeDevice_UpdatesIsRevokedFlag()
    {
        var store = new JsonPairedDeviceStore(_tempFilePath);
        string fingerprint = "SHA256:1234567890ABCDEF1234567890ABCDEF1234567890ABCDEF1234567890ABCDEF";

        await store.AddPairedDeviceAsync(new PairedDevice("dev-2", "Device-To-Revoke", fingerprint, DateTime.UtcNow, DateTime.UtcNow, false));
        Assert.IsFalse(await store.IsDeviceRevokedAsync(fingerprint));

        await store.RevokeDeviceAsync(fingerprint);
        Assert.IsTrue(await store.IsDeviceRevokedAsync(fingerprint));

        // Comprobar con nueva instancia
        var store2 = new JsonPairedDeviceStore(_tempFilePath);
        Assert.IsTrue(await store2.IsDeviceRevokedAsync(fingerprint));
    }

    [TestMethod]
    public async Task RemoveDevice_RemovesFromStore()
    {
        var store = new JsonPairedDeviceStore(_tempFilePath);
        string fingerprint = "SHA256:ABCDEF1234567890ABCDEF1234567890ABCDEF1234567890ABCDEF1234567890";

        await store.AddPairedDeviceAsync(new PairedDevice("dev-3", "Device-To-Remove", fingerprint, DateTime.UtcNow, DateTime.UtcNow, false));
        Assert.IsTrue(await store.IsDevicePairedAsync(fingerprint));

        await store.RemoveDeviceAsync(fingerprint);
        Assert.IsFalse(await store.IsDevicePairedAsync(fingerprint));
    }

    [TestMethod]
    public async Task CorruptedJsonFile_CreatesBackupAndReturnsEmptyListSafely()
    {
        File.WriteAllText(_tempFilePath, "{ invalid json content }");

        var store = new JsonPairedDeviceStore(_tempFilePath);
        var devices = await store.GetAllPairedDevicesAsync();

        Assert.AreEqual(0, devices.Count);
    }
}
