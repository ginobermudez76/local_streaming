namespace LanScreenShare.Core.Models
{
    public readonly record struct DecodedVideoFrame(byte[] Pixels, int Width, int Height);
}

