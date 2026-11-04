using System;
using System.Text.Json;
using LanScreenShare.Core.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests
{
    [TestClass]
    public class TransmissionPresetTests
    {
        [TestMethod]
        public void AppSettings_DefaultPreset_IsBalanced()
        {
            var settings = new AppSettings();
            Assert.AreEqual("balanced", settings.TransmissionPreset);
            Assert.IsFalse(settings.ShowAdvancedSettings);
        }

        [TestMethod]
        public void AppSettings_Serialization_PreservesPresetAndAdvancedVisibility()
        {
            var original = new AppSettings
            {
                TransmissionPreset = "quality",
                ShowAdvancedSettings = true,
                BitrateMbps = 15,
                Fps = 60
            };

            string json = JsonSerializer.Serialize(original);
            var deserialized = JsonSerializer.Deserialize<AppSettings>(json);

            Assert.IsNotNull(deserialized);
            Assert.AreEqual("quality", deserialized.TransmissionPreset);
            Assert.IsTrue(deserialized.ShowAdvancedSettings);
            Assert.AreEqual(15, deserialized.BitrateMbps);
            Assert.AreEqual(60, deserialized.Fps);
        }

        [TestMethod]
        public void AppSettings_SpeedPreset_SerializationRoundTrip()
        {
            var original = new AppSettings
            {
                TransmissionPreset = "speed",
                ShowAdvancedSettings = false,
                BitrateMbps = 3,
                Fps = 30
            };

            string json = JsonSerializer.Serialize(original);
            var deserialized = JsonSerializer.Deserialize<AppSettings>(json);

            Assert.IsNotNull(deserialized);
            Assert.AreEqual("speed", deserialized.TransmissionPreset);
            Assert.IsFalse(deserialized.ShowAdvancedSettings);
            Assert.AreEqual(3, deserialized.BitrateMbps);
            Assert.AreEqual(30, deserialized.Fps);
        }
    }
}
