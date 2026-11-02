namespace LanScreenShare.Core.Models
{
    public class AppSettings
    {
        public string Engine { get; set; } = "dxgi";
        public string Codec { get; set; } = "jpeg";
        public int Fps { get; set; } = 30;
        public int Quality { get; set; } = 75;
        public string ResolutionTag { get; set; } = "";
        public string CaptureSourceId { get; set; } = "screen:0";
        public string CaptureSourceName { get; set; } = "Pantalla 1";
        public bool ShareAudio { get; set; } = true;
        public bool AllowRemoteControl { get; set; } = true;
        public string StreamAccessMode { get; set; } = "auto";
        public string ControlAccessMode { get; set; } = "auto";
        public float AudioVolume { get; set; } = 1.0f;
        public bool AudioMuted { get; set; } = false;
        public string StretchMode { get; set; } = "Uniform";
    }
}
