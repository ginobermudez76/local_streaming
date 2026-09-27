using System;

namespace LanScreenShare.Core.Models
{
    public class CapturedFrame
    {
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public int Width { get; set; }
        public int Height { get; set; }
        public long Timestamp { get; set; }
        public string Format { get; set; } = "jpeg";
    }
}
