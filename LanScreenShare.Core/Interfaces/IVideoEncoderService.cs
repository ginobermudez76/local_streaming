using System;

namespace LanScreenShare.Core.Interfaces
{
    public interface IVideoEncoderService : IDisposable
    {
        string CodecName { get; }
        bool IsHardwareAccelerated => false;
        string EncoderDescription => "Software";
        byte[] EncodeFrame(byte[] bgraData, int width, int height, bool forceKeyframe = false);
    }
}
