using System;
using LanScreenShare.Core.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests
{
    [TestClass]
    public class StreamTelemetryPacketTests
    {
        [TestMethod]
        public void PacketSize_IsExactly32Bytes()
        {
            Assert.AreEqual(32, StreamTelemetryPacket.PacketSize);
        }

        [TestMethod]
        public void MagicLsst_MatchesAsciiBytes()
        {
            uint magic = StreamTelemetryPacket.MagicLsst;
            // 0x4C535354 = "LSST"
            Assert.AreEqual((byte)'L', (byte)((magic >> 24) & 0xFF));
            Assert.AreEqual((byte)'S', (byte)((magic >> 16) & 0xFF));
            Assert.AreEqual((byte)'S', (byte)((magic >> 8) & 0xFF));
            Assert.AreEqual((byte)'T', (byte)(magic & 0xFF));
        }

        [TestMethod]
        public void SerializeAndDeserialize_PreservesAllFields()
        {
            var original = new StreamTelemetryPacket
            {
                EchoTimestamp = 1730000000123L,
                ClientTimestamp = 1730000000150L,
                ViewerFps = 60,
                ViewerDroppedFrames = 2,
                AvDriftMs = -12
            };

            byte[] buffer = original.ToByteArray();
            Assert.AreEqual(StreamTelemetryPacket.PacketSize, buffer.Length);

            bool success = StreamTelemetryPacket.TryReadFrom(buffer, out var parsed);
            Assert.IsTrue(success);
            Assert.IsNotNull(parsed);

            Assert.AreEqual(original.EchoTimestamp, parsed.EchoTimestamp);
            Assert.AreEqual(original.ClientTimestamp, parsed.ClientTimestamp);
            Assert.AreEqual(original.ViewerFps, parsed.ViewerFps);
            Assert.AreEqual(original.ViewerDroppedFrames, parsed.ViewerDroppedFrames);
            Assert.AreEqual(original.AvDriftMs, parsed.AvDriftMs);
        }

        [TestMethod]
        public void TryReadFrom_ReturnsFalse_WhenBufferTooShort()
        {
            byte[] shortBuffer = new byte[31];
            bool success = StreamTelemetryPacket.TryReadFrom(shortBuffer, out var packet);
            Assert.IsFalse(success);
            Assert.IsNull(packet);
        }

        [TestMethod]
        public void TryReadFrom_ReturnsFalse_WhenMagicIsInvalid()
        {
            var original = new StreamTelemetryPacket
            {
                EchoTimestamp = 1000L,
                ClientTimestamp = 2000L
            };
            byte[] buffer = original.ToByteArray();

            // Corromper el magic en los primeros 4 bytes
            buffer[0] = 0xFF;

            bool success = StreamTelemetryPacket.TryReadFrom(buffer, out var packet);
            Assert.IsFalse(success);
            Assert.IsNull(packet);
        }

        [TestMethod]
        public void QualityEvaluation_CategorizesCorrectly()
        {
            var telemetry = new StreamTelemetry
            {
                RttMs = 12,
                HostFps = 60,
                ViewerFps = 60,
                DroppedFrames = 0
            };
            Assert.AreEqual(NetworkQuality.Excellent, telemetry.Quality);
            Assert.AreEqual("#10B981", telemetry.QualityColorHex);
            Assert.AreEqual("Excelente", telemetry.QualityText);

            telemetry.RttMs = 38;
            Assert.AreEqual(NetworkQuality.Good, telemetry.Quality);
            Assert.AreEqual("#3B82F6", telemetry.QualityColorHex);

            telemetry.RttMs = 75;
            Assert.AreEqual(NetworkQuality.Fair, telemetry.Quality);
            Assert.AreEqual("#F59E0B", telemetry.QualityColorHex);

            telemetry.RttMs = 120;
            Assert.AreEqual(NetworkQuality.Poor, telemetry.Quality);
            Assert.AreEqual("#EF4444", telemetry.QualityColorHex);

            // Incluso con RTT bajo, si hay pérdida severa de frames debe ser Inestable
            telemetry.RttMs = 15;
            telemetry.DroppedFrames = 30;
            Assert.AreEqual(NetworkQuality.Poor, telemetry.Quality);
        }
    }
}
