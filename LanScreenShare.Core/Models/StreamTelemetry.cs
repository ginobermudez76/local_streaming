using System;

namespace LanScreenShare.Core.Models
{
    public enum NetworkQuality
    {
        Unknown = 0,
        Excellent = 1, // RTT < 25ms
        Good = 2,      // RTT 25ms - 50ms
        Fair = 3,      // RTT 50ms - 90ms
        Poor = 4       // RTT > 90ms o congestión persistente
    }

    public class StreamTelemetry
    {
        public int RttMs { get; set; }
        public int HostFps { get; set; }
        public int ViewerFps { get; set; }
        public int HostFrameTimeMs { get; set; }
        public double BitrateMbps { get; set; }
        public long DroppedFrames { get; set; }
        public int AvDriftMs { get; set; }
        public int AudioBufferedMs { get; set; }
        public NetworkQuality Quality => EvaluateQuality();

        public string QualityText => Quality switch
        {
            NetworkQuality.Excellent => "Excelente",
            NetworkQuality.Good => "Buena",
            NetworkQuality.Fair => "Moderada",
            NetworkQuality.Poor => "Inestable",
            _ => "Calculando..."
        };

        public string QualityColorHex => Quality switch
        {
            NetworkQuality.Excellent => "#10B981", // Verde esmeralda
            NetworkQuality.Good => "#3B82F6",      // Azul suave
            NetworkQuality.Fair => "#F59E0B",      // Ámbar
            NetworkQuality.Poor => "#EF4444",      // Rojo
            _ => "#94A3B8"                         // Gris
        };

        private NetworkQuality EvaluateQuality()
        {
            if (!IsTelemetryActive())
                return NetworkQuality.Unknown;

            return CategorizeQuality();
        }

        private bool IsTelemetryActive() => RttMs > 0 || ViewerFps > 0 || HostFps > 0;

        private NetworkQuality CategorizeQuality()
        {
            if (RttMs < 25 && DroppedFrames == 0) return NetworkQuality.Excellent;
            if (RttMs < 50 && DroppedFrames < 5) return NetworkQuality.Good;
            if (RttMs < 90 && DroppedFrames < 20) return NetworkQuality.Fair;
            return NetworkQuality.Poor;
        }
    }
}
