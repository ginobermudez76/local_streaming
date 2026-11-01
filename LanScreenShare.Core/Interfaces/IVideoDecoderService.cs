using System;

namespace LanScreenShare.Core.Interfaces
{
    public interface IVideoDecoderService : IDisposable
    {
        string CodecName { get; }
        bool TryDecodeFrame(byte[] encodedData, int width, int height, out byte[] bgraPixels, out int decodedWidth, out int decodedHeight);
    }
}

