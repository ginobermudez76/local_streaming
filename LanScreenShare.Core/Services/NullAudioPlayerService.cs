using System;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Core.Services
{
    /// <summary>
    /// Reproductor de audio no-op/fallback multiplataforma para entornos sin subsistema WASAPI de Windows.
    /// </summary>
    public sealed class NullAudioPlayerService : IAudioPlayerService
    {
        public bool IsPlaying { get; private set; }
        public bool IsMuted { get; set; }
        public float Volume { get; set; } = 1.0f;
        public int CurrentBufferedDurationMs => 0;

        public void PlayChunk(CapturedAudioChunk chunk)
        {
            if (chunk == null || IsMuted) return;
            IsPlaying = true;
        }

        public void ClearBuffer()
        {
        }

        public void Stop()
        {
            IsPlaying = false;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}

