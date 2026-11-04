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
        private ProcessLoopbackAudioCapture? _processCapture;
        private int _currentProcessId;
        private bool _disposed;

        public event EventHandler<CapturedAudioChunk>? AudioCaptured;
        public bool IsCapturing => _capture != null || (_processCapture != null && _processCapture.IsCapturing);

        public void StartCapture() => StartCapture(0);

        public void StartCapture(int processId)
        {
            StopCapture();
            _currentProcessId = processId;

            if (processId > 0)
            {
                try
                {
                    var procCap = new ProcessLoopbackAudioCapture();
                    procCap.AudioCaptured += (s, chunk) => AudioCaptured?.Invoke(this, chunk);
                    bool started = procCap.StartAsync(processId).GetAwaiter().GetResult();
                    if (started)
                    {
                        _processCapture = procCap;
                        Console.WriteLine($"[WASAPI Loopback] Captura de audio aislada para PID {processId} iniciada.");
                        return;
                    }
                    procCap.Dispose();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WASAPI Loopback] Fallo captura de proceso PID {processId}, recurriendo a loopback global: {ex.Message}");
                }
            }

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
                Console.WriteLine($"[WASAPI Loopback] Captura de audio de escritorio iniciada ({waveFormat.SampleRate}Hz, {waveFormat.Channels} canales, {waveFormat.BitsPerSample} bits)");
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
            if (_processCapture != null)
            {
                try
                {
                    _processCapture.Stop();
                    _processCapture.Dispose();
                }
                catch { }
                _processCapture = null;
            }

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
