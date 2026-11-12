using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using LanScreenShare.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests
{
    [TestClass]
    [DoNotParallelize]
    [SupportedOSPlatform("windows")]
    public class MediaFoundationH264EncoderServiceTests
    {
        [TestMethod]
        [DoNotParallelize]
        public void EnumerateEncoders_FindsAvailableEncoders()
        {
            if (!OperatingSystem.IsWindows())
                return;

            var encoders = HardwareMftEncoderDiscovery.EnumerateEncoders();

            Assert.IsNotNull(encoders);
            Assert.IsTrue(encoders.Count >= 1, "Debe encontrarse al menos un codificador (software o hardware).");

            Console.WriteLine($"[Test] Encontrados {encoders.Count} codificadores H.264:");
            foreach (var enc in encoders)
            {
                Console.WriteLine($"  - {enc.DisplayName} (Hardware: {enc.IsHardware}, Asíncrono: {enc.IsAsync}, Vendor: {enc.Vendor}, Prioridad: {enc.Priority})");
            }

            var best = encoders[0];
            Assert.IsFalse(string.IsNullOrWhiteSpace(best.DisplayName));
            Assert.IsFalse(string.IsNullOrWhiteSpace(best.FriendlyName));
        }

        [TestMethod]
        [DoNotParallelize]
        public void MediaFoundationH264EncoderService_InitializesAndEncodesFrames()
        {
            if (!OperatingSystem.IsWindows())
                return;

            using var encoder = new MediaFoundationH264EncoderService(targetFps: 60, bitrate: 4_000_000);

            Assert.AreEqual("h264", encoder.CodecName);

            int width = 1280;
            int height = 720;
            byte[] dummyBgra = new byte[width * height * 4];

            // Rellenar con un gradiente de color para probar conversión realista
            for (int i = 0; i < dummyBgra.Length; i += 4)
            {
                dummyBgra[i] = (byte)(i & 0xFF);         // B
                dummyBgra[i + 1] = (byte)((i >> 2) & 0xFF); // G
                dummyBgra[i + 2] = (byte)((i >> 4) & 0xFF); // R
                dummyBgra[i + 3] = 255;                   // A
            }

            bool hasProducedBytes = false;
            int totalBytes = 0;

            // Codificar una ráfaga de 10 fotogramas para asegurar que la cola de la GPU/CPU drene paquetes
            for (int frame = 0; frame < 10; frame++)
            {
                byte[] encoded = encoder.EncodeFrame(dummyBgra, width, height, forceKeyframe: frame == 0);
                if (encoded.Length > 0)
                {
                    hasProducedBytes = true;
                    totalBytes += encoded.Length;

                    // Verificar presencia de prefijo de NAL unit H.264 (00 00 00 01 o 00 00 01)
                    bool validNalPrefix = (encoded.Length >= 4 && encoded[0] == 0 && encoded[1] == 0 && encoded[2] == 0 && encoded[3] == 1) ||
                                          (encoded.Length >= 3 && encoded[0] == 0 && encoded[1] == 0 && encoded[2] == 1);
                    Assert.IsTrue(validNalPrefix, "El flujo H.264 debe comenzar con prefijo de NAL unit.");
                }
            }

            Assert.IsTrue(hasProducedBytes, "El codificador debe producir paquetes de datos H.264.");
            Assert.IsTrue(totalBytes > 1000, "El flujo generado debe tener tamaño representativo de video H.264.");

            Console.WriteLine($"[Test] Codificador activo: {encoder.EncoderDescription} (Acelerado por HW: {encoder.IsHardwareAccelerated}), Total bytes producidos: {totalBytes}");
        }

        [TestMethod]
        public void BgraToNv12_ParallelConversion_ProducesCorrectDimensionsAndColors()
        {
            int width = 640;
            int height = 480;
            byte[] bgra = new byte[width * height * 4];
            byte[] nv12 = new byte[width * height * 3 / 2];

            // Probar píxel blanco (R=255, G=255, B=255)
            Array.Fill(bgra, (byte)255);

            MediaFoundationH264EncoderService.BgraToNv12(bgra, nv12, width, height);

            int frameSize = width * height;
            // Para blanco puro en Rec.601, Y ~ 235, U ~ 128, V ~ 128
            byte sampleY = nv12[0];
            byte sampleU = nv12[frameSize];
            byte sampleV = nv12[frameSize + 1];

            Assert.IsTrue(sampleY >= 230 && sampleY <= 240, $"Y para blanco esperado ~235, obtenido {sampleY}");
            Assert.IsTrue(sampleU >= 125 && sampleU <= 135, $"U para blanco esperado ~128, obtenido {sampleU}");
            Assert.IsTrue(sampleV >= 125 && sampleV <= 135, $"V para blanco esperado ~128, obtenido {sampleV}");

            // Probar píxel negro (R=0, G=0, B=0, A=255)
            Array.Clear(bgra, 0, bgra.Length);
            for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255;

            MediaFoundationH264EncoderService.BgraToNv12(bgra, nv12, width, height);

            // Para negro puro en Rec.601, Y ~ 16, U ~ 128, V ~ 128
            sampleY = nv12[0];
            sampleU = nv12[frameSize];
            sampleV = nv12[frameSize + 1];

            Assert.IsTrue(sampleY >= 14 && sampleY <= 18, $"Y para negro esperado ~16, obtenido {sampleY}");
            Assert.IsTrue(sampleU >= 125 && sampleU <= 135, $"U para negro esperado ~128, obtenido {sampleU}");
            Assert.IsTrue(sampleV >= 125 && sampleV <= 135, $"V para negro esperado ~128, obtenido {sampleV}");
        }

        [TestMethod]
        public void BgraToNv12_ParallelPerformance_Benchmark()
        {
            int width = 1920;
            int height = 1080;
            byte[] bgra = new byte[width * height * 4];
            byte[] nv12 = new byte[width * height * 3 / 2];

            new Random(42).NextBytes(bgra);

            // Calentamiento
            MediaFoundationH264EncoderService.BgraToNv12(bgra, nv12, width, height);

            var sw = Stopwatch.StartNew();
            int iterations = 10;
            for (int i = 0; i < iterations; i++)
            {
                MediaFoundationH264EncoderService.BgraToNv12(bgra, nv12, width, height);
            }
            sw.Stop();

            double avgMs = sw.Elapsed.TotalMilliseconds / iterations;
            Console.WriteLine($"[Test] Conversión paralela BGRA->NV12 (1080p): {avgMs:F2} ms promedio por fotograma");

            // En cualquier CPU moderna multinúcleo debe procesar 1080p con buen rendimiento
            Assert.IsTrue(avgMs < 50.0, $"La conversión a NV12 debe ser rápida (< 50 ms para 1080p en depuración), tardó {avgMs:F2} ms");
        }

        [TestMethod]
        [DoNotParallelize]
        public void DynamicReconfiguration_UpdateBitrateAndFps_WorksWithoutError()
        {
            if (!OperatingSystem.IsWindows())
                return;

            using var encoder = new MediaFoundationH264EncoderService(targetFps: 30, bitrate: 2_000_000);

            int width = 1280;
            int height = 720;
            byte[] dummyBgra = new byte[width * height * 4];

            encoder.EncodeFrame(dummyBgra, width, height);

            // Cambiar dinámicamente a 60 fps y 6 Mbps
            encoder.UpdateFps(60);
            encoder.UpdateBitrate(6_000_000);

            // Seguir codificando sin errores
            byte[] encoded = encoder.EncodeFrame(dummyBgra, width, height);
            Assert.IsNotNull(encoded);
        }

        [TestMethod]
        [DoNotParallelize]
        public void MediaFoundationH264DecoderService_DecodesEncoderOutput_960x540()
        {
            if (!OperatingSystem.IsWindows())
                return;

            using var encoder = new MediaFoundationH264EncoderService(targetFps: 60, bitrate: 4_000_000);
            using var decoder = new MediaFoundationH264DecoderService();

            int width = 960;
            int height = 540;
            byte[] dummyBgra = new byte[width * height * 4];
            Array.Fill(dummyBgra, (byte)128);

            int decodedCount = 0;
            int firstDecodedFrameIndex = -1;
            for (int i = 0; i < 90; i++)
            {
                byte[] encoded = encoder.EncodeFrame(dummyBgra, width, height, forceKeyframe: i == 0);
                if (encoded.Length > 0)
                {
                    bool ok = decoder.TryDecodeFrame(encoded, width, height, out var decodedFrame);
                    if (ok)
                    {
                        if (firstDecodedFrameIndex < 0) firstDecodedFrameIndex = i;
                        decodedCount++;
                    }
                }
            }

            Console.WriteLine($"[Test] Primer fotograma decodificado en índice: {firstDecodedFrameIndex}, Total decodificados: {decodedCount}/90");
            Assert.IsTrue(firstDecodedFrameIndex >= 0 && firstDecodedFrameIndex <= 2, $"El primer fotograma debe decodificarse inmediatamente en baja latencia (índice <= 2), pero tardó hasta el índice {firstDecodedFrameIndex}.");
            Assert.IsTrue(decodedCount > 80, $"El decodificador H.264 debió decodificar casi todos los fotogramas continuos (>80/90), pero solo decodificó {decodedCount}.");
        }
    }
}
