using System;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Core.Interfaces
{
    public interface IScreenCaptureService : IDisposable
    {
        event EventHandler<CapturedFrame>? FrameCaptured;
        event EventHandler<bool>? SecureDesktopChanged
        {
            add { }
            remove { }
        }
        bool IsCapturing { get; }
        int TargetFps { get; set; }
        int Quality { get; set; }
        int BitrateMbps { get; set; }
        double ResolutionScale { get; set; }
        int TargetWidth { get; set; }
        int TargetHeight { get; set; }
        string Codec { get; set; }
        string? ActiveEncoderDescription => null;
        bool IsHardwareAccelerated => false;

        void StartCapture(CaptureProfile profile);
        void StartCapture() => StartCapture(new CaptureProfile());

        void StopCapture();

        void UpdateSettings(CaptureProfile profile);

        void RequestKeyframe() { }
    }
}
