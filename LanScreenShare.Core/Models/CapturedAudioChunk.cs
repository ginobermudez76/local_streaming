using System;

namespace LanScreenShare.Core.Models
{
    public enum AudioSourceType : short
    {
        Desktop = 0,
        Microphone = 1
    }

    public class CapturedAudioChunk
    {
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public int SampleRate { get; set; } = 48000;
        public int Channels { get; set; } = 2;
        public int BitsPerSample { get; set; } = 32;
        public short AudioFormat { get; set; } = 3; // 1 = PCM integer, 3 = IEEE Float
        public AudioSourceType Source { get; set; } = AudioSourceType.Desktop;
        public long Timestamp { get; set; }
    }
}

