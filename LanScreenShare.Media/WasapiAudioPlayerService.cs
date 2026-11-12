using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

#pragma warning disable CS0618 // WasapiOut deprecation in favor of builder

namespace LanScreenShare.Media
{
    [SupportedOSPlatform("windows")]
    public class WasapiAudioPlayerService : IAudioPlayerService
    {
        private WasapiOut? _wasapiOut;
        private BufferedWaveProvider? _waveProvider;
        private WaveFormat? _currentWaveFormat;
        private readonly object _lock = new();
        private bool _isMuted;
        private float _volume = 1.0f;
        private bool _disposed;

        public bool IsPlaying => _wasapiOut != null && _wasapiOut.PlaybackState == PlaybackState.Playing;

        public int CurrentBufferedDurationMs
        {
            get
            {
                lock (_lock)
                {
                    return (int)(_waveProvider?.BufferedDuration.TotalMilliseconds ?? 0);
                }
            }
        }

        public void ClearBuffer()
        {
            lock (_lock)
            {
                _waveProvider?.ClearBuffer();
            }
        }

        public bool IsMuted
        {
            get => _isMuted;
            set => _isMuted = value;
        }

        public float Volume
        {
            get => _volume;
            set => _volume = Math.Clamp(value, 0f, 1f);
        }

        public void PlayChunk(CapturedAudioChunk chunk)
        {
            if (chunk.Data == null || chunk.Data.Length == 0)
                return;

            lock (_lock)
            {
                if (NeedsReinitialization(chunk))
                {
                    InitializePlayer(chunk);
                }

                EnqueueChunk(chunk);
            }
        }

        private bool NeedsReinitialization(CapturedAudioChunk chunk) =>
            _wasapiOut == null ||
            _wasapiOut.PlaybackState != PlaybackState.Playing ||
            _waveProvider == null ||
            !MatchesCurrentFormat(chunk);

        private bool MatchesCurrentFormat(CapturedAudioChunk chunk) =>
            _currentWaveFormat != null &&
            _currentWaveFormat.SampleRate == chunk.SampleRate &&
            _currentWaveFormat.Channels == chunk.Channels &&
            _currentWaveFormat.BitsPerSample == chunk.BitsPerSample;

        private void EnqueueChunk(CapturedAudioChunk chunk)
        {
            if (_waveProvider == null || _wasapiOut == null) return;

            if (_waveProvider.BufferedDuration.TotalMilliseconds > 80)
            {
                _waveProvider.ClearBuffer();
            }

            float currentVol = _isMuted ? 0f : _volume;
            ApplyVolume(chunk.Data!, chunk.AudioFormat, chunk.BitsPerSample, currentVol);
            _waveProvider.AddSamples(chunk.Data!, 0, chunk.Data!.Length);
        }

        private static void ApplyVolume(byte[] data, int audioFormat, int bitsPerSample, float volume)
        {
            if (volume <= 0.001f)
            {
                Array.Clear(data, 0, data.Length);
                return;
            }

            if (Math.Abs(volume - 1.0f) < 0.001f)
                return;

            if (audioFormat == 3 || bitsPerSample == 32) // IEEE Float 32-bit
            {
                ApplyFloatVolume(data, volume);
            }
            else // PCM 16-bit
            {
                ApplyPcm16Volume(data, volume);
            }
        }

        private static void ApplyFloatVolume(byte[] data, float volume)
        {
            var floatSpan = MemoryMarshal.Cast<byte, float>(data.AsSpan());
            for (int i = 0; i < floatSpan.Length; i++)
            {
                floatSpan[i] *= volume;
            }
        }

        private static void ApplyPcm16Volume(byte[] data, float volume)
        {
            var shortSpan = MemoryMarshal.Cast<byte, short>(data.AsSpan());
            for (int i = 0; i < shortSpan.Length; i++)
            {
                shortSpan[i] = (short)Math.Clamp((int)Math.Round(shortSpan[i] * volume), short.MinValue, short.MaxValue);
            }
        }

        private void InitializePlayer(CapturedAudioChunk chunk)
        {
            Stop();

            WaveFormat waveFormat;
            if (chunk.AudioFormat == 3 || chunk.BitsPerSample == 32) // IEEE Float
            {
                waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(chunk.SampleRate, chunk.Channels);
            }
            else
            {
                waveFormat = new WaveFormat(chunk.SampleRate, chunk.BitsPerSample, chunk.Channels);
            }

            _currentWaveFormat = waveFormat;
            _waveProvider = new BufferedWaveProvider(waveFormat)
            {
                DiscardOnBufferOverflow = true
            };

            try
            {
                _wasapiOut = new WasapiOut(AudioClientShareMode.Shared, 25);
                _wasapiOut.Init(_waveProvider);
                _wasapiOut.Play();
                Console.WriteLine($"[WASAPI Out] Reproductor de audio iniciado ({chunk.SampleRate}Hz, {chunk.Channels}ch, {chunk.BitsPerSample}bits)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WASAPI Out] Error inicializando reproductor: {ex.Message}");
                _wasapiOut?.Dispose();
                _wasapiOut = null;
                _waveProvider = null;
                _currentWaveFormat = null;
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (_wasapiOut != null)
                {
                    try
                    {
                        _wasapiOut.Stop();
                        _wasapiOut.Dispose();
                    }
                    catch { }

                    _wasapiOut = null;
                }

                _waveProvider = null;
                _currentWaveFormat = null;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            Stop();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
