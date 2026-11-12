using System;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Core.Interfaces
{
    public interface IVideoDecoderService : IDisposable
    {
        string CodecName { get; }
        bool TryDecodeFrame(byte[] encodedData, int width, int height, out DecodedVideoFrame decodedFrame);
    }
}
