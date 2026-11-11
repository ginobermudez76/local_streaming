using System;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Core.Services
{
    /// <summary>
    /// Capturador de audio no-op/fallback multiplataforma para entornos sin subsistema WASAPI de Windows.
    /// </summary>
    public sealed class NullAudioCaptureService : IAudioCaptureService
    {
        public event EventHandler<CapturedAudioChunk>? AudioCaptured
        {
            add { }
            remove { }
        }
        public bool IsCapturing { get; private set; }

        public void StartCapture()
        {
            IsCapturing = true;
        }

        public void StartCapture(int processId)
        {
            IsCapturing = true;
        }

        public void StopCapture()
        {
            IsCapturing = false;
        }

        public void Dispose()
        {
            StopCapture();
        }
    }
}

