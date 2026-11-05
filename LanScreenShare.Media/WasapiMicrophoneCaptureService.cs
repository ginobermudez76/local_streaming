using System;
using System.Runtime.Versioning;
using System.Threading.Tasks;
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

        public void StartCapture(int processId) => StartCapture();

        public void StartCapture()
        {
            if (IsCapturing)
                return;

            try
            {
                Task.Run(() =>
                {
                    // Obtener el dispositivo de captura de audio predeterminado (micrófono)
                    // Preferir Role.Multimedia para evitar que Windows active "Communications Ducking" (atenuación del audio del sistema)
                    using var enumerator = new MMDeviceEnumerator();
                    MMDevice? defaultDevice = null;
                    try
                    {
                        defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)
                                      ?? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                    }
                    catch
                    {
                        // Fallback a constructor por defecto
                    }

                    var captureInstance = defaultDevice != null ? new WasapiCapture(defaultDevice) : new WasapiCapture();
                    var waveFormat = captureInstance.WaveFormat;
                    short formatEncoding = (short)(waveFormat.BitsPerSample == 32 ? 3 : (short)waveFormat.Encoding);

                    captureInstance.DataAvailable += (sender, e) =>
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

                    captureInstance.RecordingStopped += (sender, e) =>
                    {
                        try { captureInstance.Dispose(); } catch { }
                        if (ReferenceEquals(_capture, captureInstance))
                        {
                            _capture = null;
                        }
                    };

                    _capture = captureInstance;
                    captureInstance.StartRecording();
                    Console.WriteLine($"[WASAPI Mic] Captura de micrófono iniciada ({waveFormat.SampleRate}Hz, {waveFormat.Channels} canales, {waveFormat.BitsPerSample} bits)");
                }).GetAwaiter().GetResult();
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
