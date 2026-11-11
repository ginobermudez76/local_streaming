using System;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Services;

namespace LanScreenShare.Media
{
    /// <summary>
    /// Fábrica desacoplada para inicializar subsistemas de audio según el sistema operativo del host o viewer.
    /// </summary>
    public static class AudioServiceFactory
    {
        public static IAudioCaptureService CreateAudioCapture()
        {
            if (OperatingSystem.IsWindows())
            {
                return new WasapiAudioCaptureService();
            }
            return new NullAudioCaptureService();
        }

        public static IAudioCaptureService CreateMicCapture()
        {
            if (OperatingSystem.IsWindows())
            {
                return new WasapiMicrophoneCaptureService();
            }
            return new NullAudioCaptureService();
        }

        public static IAudioPlayerService CreateAudioPlayer()
        {
            if (OperatingSystem.IsWindows())
            {
                return new WasapiAudioPlayerService();
            }
            return new NullAudioPlayerService();
        }
    }
}

