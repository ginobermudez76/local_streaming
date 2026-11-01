using System;

namespace LanScreenShare.Core.Interfaces
{
    public interface IVideoEncoderService : IDisposable
    {
        string CodecName { get; }
        byte[] EncodeFrame(byte[] bgraData, int width, int height, bool forceKeyframe = false);
    }
}

