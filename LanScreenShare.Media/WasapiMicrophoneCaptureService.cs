using System;
using System.Runtime.Versioning;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

#pragma warning disable CS0618

namespace LanScreenShare.Media
{
    [SupportedOSPlatform("windows")]
    public class WasapiMicrophoneCaptureService : IAudioCaptureService
    {
        private WasapiCapture? _capture;
        private bool _disposed;

        public event EventHandler<CapturedAudioChunk>? AudioCaptured;
        public bool IsCapturing => _capture != null;

        public void StartCapture()
        {
            if (IsCapturing)
                return;

            try
            {
                // Obtener el dispositivo de captura de audio predeterminado (micrófono)
                using var enumerator = new MMDeviceEnumerator();
                MMDevice? defaultDevice = null;
                try
                {
                    defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
                                  ?? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                }
                catch
                {
                    // Fallback a constructor por defecto
                }

                _capture = defaultDevice != null ? new WasapiCapture(defaultDevice) : new WasapiCapture();
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
                        Source = AudioSourceType.Microphone,
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
                Console.WriteLine($"[WASAPI Mic] Captura de micrófono iniciada ({waveFormat.SampleRate}Hz, {waveFormat.Channels} canales, {waveFormat.BitsPerSample} bits)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WASAPI Mic] Error iniciando captura de micrófono: {ex.Message}");
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
                Console.WriteLine("[WASAPI Mic] Captura de micrófono detenida.");
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
