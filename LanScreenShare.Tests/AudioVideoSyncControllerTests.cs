using System;
using LanScreenShare.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests
{
    [TestClass]
    public class AudioVideoSyncControllerTests
    {
        [TestMethod]
        public void InitialState_HasZeroDriftAndZeroTimestamps()
        {
            var controller = new AudioVideoSyncController();
            Assert.AreEqual(0, controller.CurrentDriftMs);
            Assert.AreEqual(0, controller.LatestVideoTimestamp);
            Assert.AreEqual(0, controller.LatestAudioTimestamp);
        }

        [TestMethod]
        public void ProcessAudioChunk_BeforeAnyVideo_PlaysImmediately()
        {
            var controller = new AudioVideoSyncController();
            var action = controller.ProcessAudioChunk(1000L, out int delayMs);

            Assert.AreEqual(AvSyncAction.PlayImmediately, action);
            Assert.AreEqual(0, delayMs);
            Assert.AreEqual(1000L, controller.LatestAudioTimestamp);
        }

        [TestMethod]
        public void ProcessAudioChunk_WhenInSync_PlaysImmediately()
        {
            var controller = new AudioVideoSyncController();
            controller.OnVideoFrameReceived(2000L);

            // Audio con 15ms de diferencia (dentro de tolerancia normal)
            var action = controller.ProcessAudioChunk(2015L, out int delayMs);

            Assert.AreEqual(AvSyncAction.PlayImmediately, action);
            Assert.AreEqual(0, delayMs);
            Assert.IsTrue(controller.CurrentDriftMs >= 10 && controller.CurrentDriftMs <= 20);
        }

        [TestMethod]
        public void ProcessAudioChunk_WhenAudioLagsBehindVideo_DropsStaleAudio()
        {
            var controller = new AudioVideoSyncController
            {
                MaxLagThresholdMs = 80
            };
            controller.OnVideoFrameReceived(5000L);

            // Audio con 100ms de retraso respecto al video en pantalla
            var action = controller.ProcessAudioChunk(4900L, out int delayMs);

            Assert.AreEqual(AvSyncAction.DropStaleAudio, action);
            Assert.AreEqual(0, delayMs);
        }

        [TestMethod]
        public void ProcessAudioChunk_WhenAudioLeadsVideo_RequestsMicroDelay()
        {
            var controller = new AudioVideoSyncController
            {
                MaxLeadThresholdMs = 60
            };
            controller.OnVideoFrameReceived(1000L);

            // Audio 90ms por delante del video
            var action = controller.ProcessAudioChunk(1090L, out int delayMs);

            Assert.AreEqual(AvSyncAction.DelayAudio, action);
            Assert.IsTrue(delayMs > 0 && delayMs <= 100);
        }

        [TestMethod]
        public void Reset_ClearsAllState()
        {
            var controller = new AudioVideoSyncController();
            controller.OnVideoFrameReceived(3000L);
            controller.ProcessAudioChunk(3020L, out _);

            Assert.IsTrue(controller.LatestVideoTimestamp > 0);
            Assert.IsTrue(controller.LatestAudioTimestamp > 0);

            controller.Reset();

            Assert.AreEqual(0, controller.LatestVideoTimestamp);
            Assert.AreEqual(0, controller.LatestAudioTimestamp);
            Assert.AreEqual(0, controller.CurrentDriftMs);
        }
    }
}
