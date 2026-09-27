using System;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Core.Interfaces
{
    public interface IScreenCaptureService : IDisposable
    {
        event EventHandler<CapturedFrame>? FrameCaptured;
        bool IsCapturing { get; }
        void StartCapture(int targetFps = 30, int quality = 70);
        void StopCapture();
    }
}
