using System;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Core.Interfaces
{
    public interface IScreenCaptureService : IDisposable
    {
        event EventHandler<CapturedFrame>? FrameCaptured;
        bool IsCapturing { get; }
        int TargetFps { get; set; }
        int Quality { get; set; }
        double ResolutionScale { get; set; }
        int TargetWidth { get; set; }
        int TargetHeight { get; set; }
        string Codec { get; set; }
        void StartCapture(int targetFps = 30, int quality = 70, double resolutionScale = 1.0, string codec = "jpeg", int targetWidth = 0, int targetHeight = 0);
        void StopCapture();
        void UpdateSettings(int targetFps, int quality, double resolutionScale = 1.0, string codec = "jpeg", int targetWidth = 0, int targetHeight = 0);
    }
}
