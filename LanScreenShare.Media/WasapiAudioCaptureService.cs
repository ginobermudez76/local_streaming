using System;
using System.Runtime.Versioning;
using System.Threading.Tasks;
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

            if (TryStartProcessLoopback(processId))
            {
                return;
            }

            if (processId > 0)
            {
                Console.WriteLine($"[WASAPI Loopback] No se pudo iniciar la captura aislada para PID {processId}; se omite fallback global para no mezclar audio del escritorio.");
                return;
            }

            StartGlobalLoopback();
        }

        private bool TryStartProcessLoopback(int processId)
        {
            try
            {
                var procCap = new ProcessLoopbackAudioCapture();
                procCap.AudioCaptured += (s, chunk) => AudioCaptured?.Invoke(this, chunk);
                bool started = procCap.StartAsync(processId).GetAwaiter().GetResult();
                if (started)
                {
                    _processCapture = procCap;
                    LogLoopbackStarted(processId);
                    return true;
                }
                procCap.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WASAPI Loopback] Fallo ProcessLoopback (PID {processId}), recurriendo a WasapiLoopbackCapture: {ex.Message}");
            }

            return false;
        }

        private static void LogLoopbackStarted(int processId)
        {
            if (processId > 0)
            {
                Console.WriteLine($"[WASAPI Loopback] Captura de audio aislada para PID {processId} iniciada.");
            }
            else
            {
                Console.WriteLine("[WASAPI Loopback] Captura de audio de sistema completo iniciada (ProcessLoopback, excluyendo propio proceso).");
            }
        }

        private void StartGlobalLoopback()
        {
            try
            {
                Task.Run(() =>
                {
                    var captureInstance = new WasapiLoopbackCapture();
                    var waveFormat = captureInstance.WaveFormat;
                    short formatEncoding = (short)(waveFormat.BitsPerSample == 32 ? 3 : (short)waveFormat.Encoding);

                    captureInstance.DataAvailable += (sender, e) => HandleDataAvailable(e, waveFormat, formatEncoding);
                    captureInstance.RecordingStopped += (sender, e) => HandleRecordingStopped(captureInstance);

                    _capture = captureInstance;
                    captureInstance.StartRecording();
                    Console.WriteLine($"[WASAPI Loopback] Captura de audio de escritorio iniciada ({waveFormat.SampleRate}Hz, {waveFormat.Channels} canales, {waveFormat.BitsPerSample} bits)");
                }).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WASAPI Loopback] Error iniciando captura de audio: {ex.Message}");
                _capture?.Dispose();
                _capture = null;
            }
        }

        private void HandleDataAvailable(WaveInEventArgs e, WaveFormat waveFormat, short formatEncoding)
        {
            if (e.BytesRecorded <= 0) return;

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
        }

        private void HandleRecordingStopped(WasapiLoopbackCapture captureInstance)
        {
            try { captureInstance.Dispose(); } catch { }
            if (ReferenceEquals(_capture, captureInstance))
            {
                _capture = null;
            }
        }

        public void StopCapture()
        {
            if (_processCapture != null)
            {
                var procCap = _processCapture;
                _processCapture = null;
                try
                {
                    procCap.Stop();
                    procCap.Dispose();
                }
                catch { }
            }

            if (_capture != null)
            {
                var captureInstance = _capture;
                _capture = null;

                try
                {
                    captureInstance.StopRecording();
                }
                catch { }

                try
                {
                    captureInstance.Dispose();
                }
                catch { }

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
