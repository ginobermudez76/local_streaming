using System;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Core.Interfaces
{
    public interface IAudioPlayerService : IDisposable
    {
        bool IsPlaying { get; }
        bool IsMuted { get; set; }
        float Volume { get; set; }
        void PlayChunk(CapturedAudioChunk chunk);
        void Stop();
    }
}

