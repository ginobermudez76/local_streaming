using System;
using System.Runtime.Versioning;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;
using NAudio.Wave;

#pragma warning disable CS0618 // WasapiLoopbackCapture deprecation in favor of builder

namespace LanScreenShare.Media
{
    [SupportedOSPlatform("windows")]
    public class WasapiAudioCaptureService : IAudioCaptureService
    {
        private WasapiLoopbackCapture? _capture;
        private bool _disposed;

        public event EventHandler<CapturedAudioChunk>? AudioCaptured;
        public bool IsCapturing => _capture != null;

        public void StartCapture()
        {
            if (IsCapturing)
                return;

            try
            {
                _capture = new WasapiLoopbackCapture();
                var waveFormat = _capture.WaveFormat;
                short formatEncoding = (short)(waveFormat.BitsPerSample == 32 ? 3 : (short)waveFormat.Encoding);

                _capture.DataAvailable += (sender, e) =>
                {
                    if (e.BytesRecorded <= 0)
                        return;

                    byte[] buffer = new byte[e.BytesRecorded];
                    Buffer.BlockCopy(e.Buffer, 0, buffer, 0, e.BytesRecorded);

                    var chunk = new CapturedAudioChunk
                    {
                        Data = buffer,
                        SampleRate = waveFormat.SampleRate,
                        Channels = waveFormat.Channels,
                        BitsPerSample = waveFormat.BitsPerSample,
                        AudioFormat = formatEncoding,
                        Source = AudioSourceType.Desktop,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };

                    AudioCaptured?.Invoke(this, chunk);
                };

                _capture.RecordingStopped += (sender, e) =>
                {
                    _capture?.Dispose();
                    _capture = null;
                };

                _capture.StartRecording();
                Console.WriteLine($"[WASAPI Loopback] Captura de audio iniciada ({waveFormat.SampleRate}Hz, {waveFormat.Channels} canales, {waveFormat.BitsPerSample} bits)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WASAPI Loopback] Error iniciando captura de audio: {ex.Message}");
                _capture?.Dispose();
                _capture = null;
            }
        }

        public void StopCapture()
        {
            if (_capture != null)
            {
                try
                {
                    _capture.StopRecording();
                }
                catch { }

                try
                {
                    _capture.Dispose();
                }
                catch { }

                _capture = null;
                Console.WriteLine("[WASAPI Loopback] Captura de audio detenida.");
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            StopCapture();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
