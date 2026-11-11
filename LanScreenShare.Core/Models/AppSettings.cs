namespace LanScreenShare.Core.Models
{
    public class AppSettings
    {
        public string Engine { get; set; } = "dxgi";
        public string Codec { get; set; } = "h264";
        public int Fps { get; set; } = 60;
        public int Quality { get; set; } = 85;
        public int BitrateMbps { get; set; } = 15;
        public string ResolutionTag { get; set; } = "";
        public string TransmissionPreset { get; set; } = "balanced";
        public bool ShowAdvancedSettings { get; set; } = false;
        public string CaptureSourceId { get; set; } = "screen:0";
        public string CaptureSourceName { get; set; } = "Pantalla 1";
        public bool ShareAudio { get; set; } = true;
        public bool ShareMicrophone { get; set; } = true;
        public bool AllowRemoteControl { get; set; } = true;
        public string StreamAccessMode { get; set; } = "auto";
        public string ControlAccessMode { get; set; } = "auto";
        public float AudioVolume { get; set; } = 1.0f;
        public bool AudioMuted { get; set; } = false;
        public float MicVolume { get; set; } = 1.0f;
        public bool MicMuted { get; set; } = false;
        public bool ViewerVoiceChatEnabled { get; set; } = true;
        public bool ClipboardSyncEnabled { get; set; } = true;
        public string StretchMode { get; set; } = "Uniform";
        public bool EnableAdaptiveBitrate { get; set; } = true;
        public int MinAdaptiveBitrateMbps { get; set; } = 2;
        public int MaxAdaptiveBitrateMbps { get; set; } = 25;
    }
}
