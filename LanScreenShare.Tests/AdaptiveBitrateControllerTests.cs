using System;
using System.Collections.Generic;
using LanScreenShare.Core.Models;
using LanScreenShare.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests
{
    [TestClass]
    public class AdaptiveBitrateControllerTests
    {
        private long _simulatedTimeMs;

        private long TimeProvider() => _simulatedTimeMs;

        private void AdvanceTime(long ms) => _simulatedTimeMs += ms;

        [TestInitialize]
        public void Setup()
        {
            _simulatedTimeMs = 1_000_000;
        }

        [TestMethod]
        public void InitialState_ValuesAreWithinBounds()
        {
            var controller = new AdaptiveBitrateController(initialBitrateMbps: 15, baseFps: 60, TimeProvider)
            {
                MinBitrateMbps = 2,
                MaxBitrateMbps = 25
            };

            Assert.AreEqual(15, controller.CurrentBitrateMbps);
            Assert.AreEqual(60, controller.TargetFps);
            Assert.AreEqual(60, controller.BaseFps);
            Assert.AreEqual(AbrNetworkState.Optimal, controller.CurrentState);
            Assert.IsTrue(controller.IsEnabled);
        }

        [TestMethod]
        public void AdditiveIncrease_WhenNetworkIsOptimal_IncreasesBitrate()
        {
            var controller = new AdaptiveBitrateController(initialBitrateMbps: 10, baseFps: 60, TimeProvider)
            {
                MinBitrateMbps = 2,
                MaxBitrateMbps = 20,
                IncreaseCooldownMs = 1500
            };

            int notifiedBitrate = 0;
            controller.BitrateChanged += (br, reason) => notifiedBitrate = br;

            var telemetry = new StreamTelemetry
            {
                RttMs = 15, // Optimal (< 25ms)
                DroppedFrames = 0,
                HostFps = 60,
                ViewerFps = 60
            };

            // First check before cooldown expires -> should NOT increase
            AdvanceTime(500);
            controller.RegisterTelemetry(telemetry);
            Assert.AreEqual(10, controller.CurrentBitrateMbps);

            // After cooldown expires -> should increase by 1
            AdvanceTime(1600);
            controller.RegisterTelemetry(telemetry);
            Assert.AreEqual(11, controller.CurrentBitrateMbps);
            Assert.AreEqual(11, notifiedBitrate);

            // Advance again and verify it increases toward max
            AdvanceTime(1600);
            controller.RegisterTelemetry(telemetry);
            Assert.AreEqual(12, controller.CurrentBitrateMbps);
        }

        [TestMethod]
        public void AdditiveIncrease_ClampsToMaxBitrate()
        {
            var controller = new AdaptiveBitrateController(initialBitrateMbps: 19, baseFps: 60, TimeProvider)
            {
                MinBitrateMbps = 2,
                MaxBitrateMbps = 20,
                IncreaseCooldownMs = 1000
            };

            var telemetry = new StreamTelemetry
            {
                RttMs = 12,
                DroppedFrames = 0,
                HostFps = 60,
                ViewerFps = 60
            };

            AdvanceTime(1200);
            controller.RegisterTelemetry(telemetry);
            Assert.AreEqual(20, controller.CurrentBitrateMbps);

            // Should not exceed 20
            AdvanceTime(1200);
            controller.RegisterTelemetry(telemetry);
            Assert.AreEqual(20, controller.CurrentBitrateMbps);
        }

        [TestMethod]
        public void RegisterHostDrop_TriggersMultiplicativeDecreaseAndKeyframe()
        {
            var controller = new AdaptiveBitrateController(initialBitrateMbps: 20, baseFps: 60, TimeProvider)
            {
                MinBitrateMbps = 2,
                MaxBitrateMbps = 25
            };

            bool keyframeRequested = false;
            int newBitrate = 0;
            controller.KeyframeRequested += () => keyframeRequested = true;
            controller.BitrateChanged += (br, r) => newBitrate = br;

            controller.RegisterHostDrop();

            Assert.IsTrue(keyframeRequested, "Keyframe should be requested on drop to reset decoder artifacts.");
            Assert.AreEqual(AbrNetworkState.SevereCongestion, controller.CurrentState);
            // 20 * 0.65 = 13 Mbps
            Assert.AreEqual(13, controller.CurrentBitrateMbps);
            Assert.AreEqual(13, newBitrate);
        }

        [TestMethod]
        public void ReceiverDrops_InTelemetry_TriggersImmediateBackoff()
        {
            var controller = new AdaptiveBitrateController(initialBitrateMbps: 16, baseFps: 60, TimeProvider)
            {
                MinBitrateMbps = 3,
                MaxBitrateMbps = 25
            };

            bool keyframeRequested = false;
            controller.KeyframeRequested += () => keyframeRequested = true;

            var telemetryInitial = new StreamTelemetry { RttMs = 20, DroppedFrames = 0, HostFps = 60, ViewerFps = 60 };
            controller.RegisterTelemetry(telemetryInitial);
            Assert.AreEqual(16, controller.CurrentBitrateMbps);

            AdvanceTime(200);
            var telemetryWithDrop = new StreamTelemetry { RttMs = 20, DroppedFrames = 2, HostFps = 60, ViewerFps = 58 };
            controller.RegisterTelemetry(telemetryWithDrop);

            Assert.IsTrue(keyframeRequested);
            Assert.IsTrue(controller.CurrentBitrateMbps < 16);
            Assert.AreEqual(AbrNetworkState.SevereCongestion, controller.CurrentState);
        }

        [TestMethod]
        public void HighRtt_TriggersModerateCongestionStepDown()
        {
            var controller = new AdaptiveBitrateController(initialBitrateMbps: 16, baseFps: 60, TimeProvider)
            {
                MinBitrateMbps = 2,
                MaxBitrateMbps = 25,
                RttThresholdCongestionMs = 50,
                RttThresholdSevereMs = 90,
                DecreaseCooldownMs = 500
            };

            var telemetry = new StreamTelemetry
            {
                RttMs = 60, // Moderate congestion (> 50ms, < 90ms)
                DroppedFrames = 0,
                HostFps = 60,
                ViewerFps = 60
            };

            AdvanceTime(600);
            controller.RegisterTelemetry(telemetry);

            Assert.AreEqual(AbrNetworkState.Congested, controller.CurrentState);
            // 16 * 0.85 = 14 Mbps
            Assert.AreEqual(14, controller.CurrentBitrateMbps);
        }

        [TestMethod]
        public void SevereRtt_DropsAggressively()
        {
            var controller = new AdaptiveBitrateController(initialBitrateMbps: 20, baseFps: 60, TimeProvider)
            {
                MinBitrateMbps = 2,
                MaxBitrateMbps = 25,
                RttThresholdSevereMs = 90
            };

            var telemetry = new StreamTelemetry
            {
                RttMs = 120, // Severe (> 90ms)
                DroppedFrames = 0,
                HostFps = 60,
                ViewerFps = 45
            };

            AdvanceTime(800);
            controller.RegisterTelemetry(telemetry);

            Assert.AreEqual(AbrNetworkState.SevereCongestion, controller.CurrentState);
            // 20 * 0.65 = 13 Mbps
            Assert.AreEqual(13, controller.CurrentBitrateMbps);
        }

        [TestMethod]
        public void DefensiveFpsDowngrade_WhenBitrateIsAtFloor_AndCongestionContinues()
        {
            var controller = new AdaptiveBitrateController(initialBitrateMbps: 2, baseFps: 60, TimeProvider)
            {
                MinBitrateMbps = 2,
                MaxBitrateMbps = 20,
                MinFps = 30,
                DecreaseCooldownMs = 300
            };

            Assert.AreEqual(2, controller.CurrentBitrateMbps);
            Assert.AreEqual(60, controller.TargetFps);

            AdvanceTime(400);
            controller.RegisterHostDrop();

            Assert.AreEqual(2, controller.CurrentBitrateMbps, "Bitrate cannot fall below minimum floor.");
            Assert.AreEqual(45, controller.TargetFps, "FPS should decrease from 60 to 45.");

            AdvanceTime(400);
            controller.RegisterHostDrop();
            Assert.AreEqual(30, controller.TargetFps, "FPS should decrease from 45 to min floor 30.");

            AdvanceTime(400);
            controller.RegisterHostDrop();
            Assert.AreEqual(30, controller.TargetFps, "FPS should not fall below min floor 30.");
        }

        [TestMethod]
        public void Recovery_RestoresFpsFirst_ThenRampsBitrate()
        {
            var controller = new AdaptiveBitrateController(initialBitrateMbps: 2, baseFps: 60, TimeProvider)
            {
                MinBitrateMbps = 2,
                MaxBitrateMbps = 15,
                MinFps = 30,
                IncreaseCooldownMs = 1000
            };

            // Force drop to reduce FPS to 30
            controller.RegisterHostDrop();
            AdvanceTime(800);
            controller.RegisterHostDrop();
            Assert.AreEqual(30, controller.TargetFps);

            var optimalTelemetry = new StreamTelemetry
            {
                RttMs = 10,
                DroppedFrames = 0,
                HostFps = 60,
                ViewerFps = 60
            };

            // First optimal sample after cooldown -> restores FPS from 30 to 45
            AdvanceTime(1500);
            controller.RegisterTelemetry(optimalTelemetry);
            Assert.AreEqual(45, controller.TargetFps);
            Assert.AreEqual(2, controller.CurrentBitrateMbps);

            // Second optimal sample -> restores FPS from 45 to 60
            AdvanceTime(1500);
            controller.RegisterTelemetry(optimalTelemetry);
            Assert.AreEqual(60, controller.TargetFps);
            Assert.AreEqual(2, controller.CurrentBitrateMbps);

            // Third optimal sample -> now increases bitrate from 2 to 4 (step 2 when < 8)
            AdvanceTime(1500);
            controller.RegisterTelemetry(optimalTelemetry);
            Assert.AreEqual(4, controller.CurrentBitrateMbps);
        }

        [TestMethod]
        public void DisabledController_DoesNotChangeBitrate()
        {
            var controller = new AdaptiveBitrateController(initialBitrateMbps: 15, baseFps: 60, TimeProvider)
            {
                IsEnabled = false
            };

            controller.RegisterHostDrop();
            Assert.AreEqual(15, controller.CurrentBitrateMbps);

            controller.RegisterTelemetry(new StreamTelemetry { RttMs = 150, DroppedFrames = 5 });
            Assert.AreEqual(15, controller.CurrentBitrateMbps);
        }

        [TestMethod]
        public void Reset_ClearsStateAndRestoresDefaults()
        {
            var controller = new AdaptiveBitrateController(initialBitrateMbps: 20, baseFps: 60, TimeProvider);
            controller.RegisterHostDrop();
            Assert.AreEqual(AbrNetworkState.SevereCongestion, controller.CurrentState);

            controller.Reset(initialBitrateMbps: 12, baseFps: 60);

            Assert.AreEqual(AbrNetworkState.Optimal, controller.CurrentState);
            Assert.AreEqual(12, controller.CurrentBitrateMbps);
            Assert.AreEqual(60, controller.TargetFps);
        }
    }
}
