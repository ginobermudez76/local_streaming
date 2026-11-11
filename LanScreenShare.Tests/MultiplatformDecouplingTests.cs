using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;
using LanScreenShare.Core.Security;
using LanScreenShare.Core.Services;
using LanScreenShare.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests
{
    [TestClass]
    public class MultiplatformDecouplingTests
    {
        private string _tempDir = string.Empty;

        [TestInitialize]
        public void Setup()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "LanScreenShare_MultiplatformTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try
            {
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, true);
                }
            }
            catch { }
        }

        [TestMethod]
        public async Task CrossPlatformIdentityKeyStorage_GenerateAndLoadCertificate_WorksWithAesGcm()
        {
            string storagePath = Path.Combine(_tempDir, "test_identity.aes");
            var storage = new CrossPlatformIdentityKeyStorage(storagePath);

            // 1. Crear certificado
            using var cert1 = await storage.GetOrCreateIdentityCertificateAsync();
            Assert.IsNotNull(cert1);
            Assert.IsTrue(cert1.HasPrivateKey);
            Assert.IsTrue(cert1.Subject.Contains("LanScreenShare"));
            string thumbprint1 = cert1.Thumbprint;

            // 2. Cargar certificado existente (debe ser idéntico)
            var storageReload = new CrossPlatformIdentityKeyStorage(storagePath);
            using var cert2 = await storageReload.GetOrCreateIdentityCertificateAsync();
            Assert.IsNotNull(cert2);
            Assert.IsTrue(cert2.HasPrivateKey);
            Assert.AreEqual(thumbprint1, cert2.Thumbprint);

            // 3. Resetear identidad
            await storageReload.ResetIdentityAsync();
            Assert.IsFalse(File.Exists(storagePath));
        }

        [TestMethod]
        public void IdentityKeyStorageFactory_CreatesValidStorageInstance()
        {
            var storage = IdentityKeyStorageFactory.Create();
            Assert.IsNotNull(storage);
            Assert.IsTrue(storage is IIdentityKeyStorage);
        }

        [TestMethod]
        public void NullInputSimulator_HandlesEventsWithoutCrashing()
        {
            using var simulator = new NullInputSimulatorService();
            simulator.SetTargetSource(new CaptureSource { Id = "test:0", Name = "Display" });
            Assert.AreEqual("test:0", simulator.CurrentTargetSource?.Id);

            var input = new RemoteInputMessage
            {
                Type = InputEventType.MouseMove,
                NormalizedX = 0.5f,
                NormalizedY = 0.5f
            };

            simulator.SimulateInput(input);
            Assert.AreEqual(1, simulator.SimulatedInputsCount);

            simulator.ReleaseInjectedInputs();
        }

        [TestMethod]
        public void NullAudioServices_HandleLifecycleGracefully()
        {
            using var capture = new NullAudioCaptureService();
            Assert.IsFalse(capture.IsCapturing);
            capture.StartCapture();
            Assert.IsTrue(capture.IsCapturing);
            capture.StopCapture();
            Assert.IsFalse(capture.IsCapturing);

            using var player = new NullAudioPlayerService();
            Assert.AreEqual(1.0f, player.Volume);
            Assert.IsFalse(player.IsMuted);

            player.Volume = 0.5f;
            player.IsMuted = true;
            Assert.AreEqual(0.5f, player.Volume);
            Assert.IsTrue(player.IsMuted);

            player.PlayChunk(new CapturedAudioChunk { Data = new byte[100] });
            player.ClearBuffer();
            player.Stop();
        }

        [TestMethod]
        public void AudioServiceFactory_ReturnsFunctionalInstances()
        {
            using var capture = AudioServiceFactory.CreateAudioCapture();
            using var mic = AudioServiceFactory.CreateMicCapture();
            using var player = AudioServiceFactory.CreateAudioPlayer();

            Assert.IsNotNull(capture);
            Assert.IsNotNull(mic);
            Assert.IsNotNull(player);
        }

        [TestMethod]
        public async Task CrossPlatformScreenCaptureService_CapturesFramesSmoothly()
        {
            using var service = new CrossPlatformScreenCaptureService();
            int framesCaptured = 0;
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            service.FrameCaptured += (s, frame) =>
            {
                if (Interlocked.Increment(ref framesCaptured) >= 2)
                {
                    tcs.TrySetResult(true);
                }
            };

            service.StartCapture(targetFps: 60, targetWidth: 640, targetHeight: 480);
            Assert.IsTrue(service.IsCapturing);

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(5000));
            Assert.AreEqual(tcs.Task, completed, "CrossPlatformScreenCaptureService debe generar fotogramas sintéticos continuos.");

            service.StopCapture();
            Assert.IsFalse(service.IsCapturing);
        }

        [TestMethod]
        public void CaptureSourceDiscoveryService_ReturnsAtLeastOneSourceOnAnyPlatform()
        {
            var screens = CaptureSourceDiscoveryService.GetScreenSources();
            Assert.IsNotNull(screens);
            Assert.IsTrue(screens.Count >= 1, "Debe retornar al menos la pantalla principal en cualquier plataforma.");
            Assert.IsFalse(string.IsNullOrEmpty(screens[0].Name));
        }

        [TestMethod]
        public void CaptureServiceFactory_CreatesAppropriateCaptureService()
        {
            var source = new CaptureSource { Type = CaptureSourceType.Screen, Id = "screen:0", Name = "Pantalla 1" };
            using var service = CaptureServiceFactory.Create(source, "dxgi");
            Assert.IsNotNull(service);
            Assert.IsTrue(service is IScreenCaptureService);
        }
    }
}

