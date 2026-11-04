using System;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Core.Interfaces
{
    public interface IAudioCaptureService : IDisposable
    {
        event EventHandler<CapturedAudioChunk>? AudioCaptured;
        bool IsCapturing { get; }
        void StartCapture();
        void StartCapture(int processId);
        void StopCapture();
    }
}

