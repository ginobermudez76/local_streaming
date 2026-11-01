using System;
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

        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                _isMuted = value;
                lock (_lock)
                {
                    if (_wasapiOut != null)
                    {
                        _wasapiOut.Volume = _isMuted ? 0f : _volume;
                    }
                }
            }
        }

        public float Volume
        {
            get => _volume;
            set
            {
                _volume = Math.Clamp(value, 0f, 1f);
                lock (_lock)
                {
                    if (_wasapiOut != null && !_isMuted)
                    {
                        _wasapiOut.Volume = _volume;
                    }
                }
            }
        }

        public void PlayChunk(CapturedAudioChunk chunk)
        {
            if (chunk.Data == null || chunk.Data.Length == 0)
                return;

            lock (_lock)
            {
                if (_waveProvider == null || _currentWaveFormat == null ||
                    _currentWaveFormat.SampleRate != chunk.SampleRate ||
                    _currentWaveFormat.Channels != chunk.Channels)
                {
                    InitializePlayer(chunk);
                }

                if (_waveProvider != null)
                {
                    _waveProvider.AddSamples(chunk.Data, 0, chunk.Data.Length);
                }
            }
        }

        private void InitializePlayer(CapturedAudioChunk chunk)
        {
            Stop();

            WaveFormat waveFormat;
            if (chunk.AudioFormat == 3) // IEEE Float
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
                _wasapiOut.Volume = _isMuted ? 0f : _volume;
                _wasapiOut.Play();
                Console.WriteLine($"[WASAPI Out] Reproductor de audio iniciado ({chunk.SampleRate}Hz, {chunk.Channels}ch, {chunk.BitsPerSample}bits)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WASAPI Out] Error inicializando reproductor: {ex.Message}");
                _wasapiOut?.Dispose();
                _wasapiOut = null;
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
