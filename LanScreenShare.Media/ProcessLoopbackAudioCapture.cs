using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Models;
using NAudio.Wave;

namespace LanScreenShare.Media
{
    [SupportedOSPlatform("windows")]
    internal sealed class ProcessLoopbackAudioCapture : IDisposable
    {
        private const string VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK = "VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK";
        private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        private const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;

        public event EventHandler<CapturedAudioChunk>? AudioCaptured;
        public bool IsCapturing { get; private set; }

        private CancellationTokenSource? _cts;
        private Task? _captureTask;
        private IAudioClient? _audioClient;
        private IAudioCaptureClient? _captureClient;
        private AutoResetEvent? _bufferEvent;
        private bool _disposed;

        public Task<bool> StartAsync(int processId, CancellationToken cancellationToken = default)
        {
            if (IsCapturing)
                return Task.FromResult(false);

            // Ejecutar la activación e inicialización COM siempre en un hilo MTA del ThreadPool
            // para evitar bloqueos o problemas de marshaling con el hilo STA de la interfaz gráfica.
            return Task.Run(async () =>
            {
                bool includeProcess = processId > 0;
                uint targetPid = includeProcess ? (uint)processId : (uint)Environment.ProcessId;

                try
                {
                    var handler = new AudioInterfaceCompletionHandler();
                    var activationParams = new AUDIOCLIENT_ACTIVATION_PARAMS
                    {
                        ActivationType = AUDIOCLIENT_ACTIVATION_TYPE.ProcessLoopback,
                        ProcessLoopbackParams = new AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
                        {
                            TargetProcessId = targetPid,
                            ProcessLoopbackMode = includeProcess
                                ? PROCESS_LOOPBACK_MODE.IncludeTargetProcessTree
                                : PROCESS_LOOPBACK_MODE.ExcludeTargetProcessTree
                        }
                    };

                IntPtr pParams = Marshal.AllocHGlobal(Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>());
                Marshal.StructureToPtr(activationParams, pParams, false);

                var propVariant = new PROPVARIANT
                {
                    vt = 65, // VT_BLOB
                    blob = new BLOB
                    {
                        cbSize = Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>(),
                        pBlobData = pParams
                    }
                };

                IntPtr pProp = Marshal.AllocHGlobal(Marshal.SizeOf<PROPVARIANT>());
                Marshal.StructureToPtr(propVariant, pProp, false);

                int hr;
                object rawClient;
                IActivateAudioInterfaceAsyncOperation? asyncOp = null;
                try
                {
                    hr = ActivateAudioInterfaceAsync(
                        VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
                        typeof(IAudioClient).GUID,
                        pProp,
                        handler,
                        out asyncOp);

                    if (hr < 0)
                    {
                        Console.WriteLine($"[ProcessAudio] ActivateAudioInterfaceAsync falló con HRESULT: 0x{hr:X8}");
                        return false;
                    }

                    using var timeoutCts = new CancellationTokenSource(2500);
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                    try
                    {
                        rawClient = await handler.Task.WaitAsync(linkedCts.Token);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ProcessAudio] Timeout o error esperando activación COM: {ex.Message}");
                        return false;
                    }
                }
                finally
                {
                    GC.KeepAlive(handler);
                    if (asyncOp != null)
                    {
                        try { Marshal.ReleaseComObject(asyncOp); } catch { }
                    }
                    Marshal.FreeHGlobal(pProp);
                    Marshal.FreeHGlobal(pParams);
                }

                _audioClient = rawClient as IAudioClient;
                if (_audioClient == null)
                {
                    Console.WriteLine("[ProcessAudio] El objeto activado no implementa IAudioClient");
                    return false;
                }

                WaveFormat waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
                var wfx = new WAVEFORMATEX
                {
                    wFormatTag = 3, // WAVE_FORMAT_IEEE_FLOAT
                    nChannels = 2,
                    nSamplesPerSec = 48000,
                    wBitsPerSample = 32,
                    nBlockAlign = 8,
                    nAvgBytesPerSec = 48000 * 8,
                    cbSize = 0
                };

                long bufferDurationHns = 10000000; // 1 segundo (en unidades de 100ns)
                hr = _audioClient.Initialize(
                    0, // AudioClientShareMode.Shared
                    AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                    bufferDurationHns,
                    0,
                    ref wfx,
                    IntPtr.Zero);

                if (hr < 0)
                {
                    Console.WriteLine($"[ProcessAudio] IAudioClient.Initialize falló con HRESULT: 0x{hr:X8}");
                    Cleanup();
                    return false;
                }

                _bufferEvent = new AutoResetEvent(false);
                hr = _audioClient.SetEventHandle(_bufferEvent.SafeWaitHandle.DangerousGetHandle());
                if (hr < 0)
                {
                    Console.WriteLine($"[ProcessAudio] SetEventHandle falló con HRESULT: 0x{hr:X8}");
                    Cleanup();
                    return false;
                }

                hr = _audioClient.GetService(typeof(IAudioCaptureClient).GUID, out object rawCaptureClient);
                if (hr < 0 || rawCaptureClient == null)
                {
                    Console.WriteLine($"[ProcessAudio] GetService(IAudioCaptureClient) falló con HRESULT: 0x{hr:X8}");
                    Cleanup();
                    return false;
                }

                _captureClient = (IAudioCaptureClient)rawCaptureClient;
                hr = _audioClient.Start();
                if (hr < 0)
                {
                    Console.WriteLine($"[ProcessAudio] IAudioClient.Start falló con HRESULT: 0x{hr:X8}");
                    Cleanup();
                    return false;
                }

                _cts = new CancellationTokenSource();
                IsCapturing = true;
                _captureTask = Task.Run(() => CaptureLoop(waveFormat, _cts.Token));
                string modeDesc = includeProcess ? $"PID {processId} (aislado)" : $"Escritorio completo (excluyendo PID {targetPid})";
                Console.WriteLine($"[ProcessAudio] Captura iniciada: {modeDesc} ({waveFormat.SampleRate}Hz, {waveFormat.Channels}ch, {waveFormat.BitsPerSample}bits)");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ProcessAudio] Error general iniciando captura de proceso: {ex.Message}");
                Cleanup();
                return false;
            }
            }, cancellationToken);
        }

        private void CaptureLoop(WaveFormat waveFormat, CancellationToken token)
        {
            if (_bufferEvent == null || _captureClient == null)
                return;

            short formatEncoding = (short)(waveFormat.BitsPerSample == 32 ? 3 : (short)waveFormat.Encoding);
            var waitHandles = new WaitHandle[] { _bufferEvent, token.WaitHandle };

            while (!token.IsCancellationRequested)
            {
                int index = WaitHandle.WaitAny(waitHandles, 20);
                if (index == 1 || token.IsCancellationRequested)
                    break;

                while (!token.IsCancellationRequested)
                {
                    int hrNext = _captureClient.GetNextPacketSize(out uint packetSize);
                    if (hrNext < 0 || packetSize == 0)
                        break;

                    int hrGet = _captureClient.GetBuffer(out IntPtr pData, out uint numFrames, out uint flags, out _, out _);
                    if (hrGet < 0 || pData == IntPtr.Zero || numFrames == 0)
                        break;

                    int byteCount = (int)(numFrames * waveFormat.BlockAlign);
                    byte[] buffer = new byte[byteCount];

                    if ((flags & 1) != 0) // AUDCLNT_BUFFERFLAGS_SILENT
                    {
                        // Buffer ya en ceros
                    }
                    else
                    {
                        Marshal.Copy(pData, buffer, 0, byteCount);
                    }

                    _captureClient.ReleaseBuffer(numFrames);

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
            }
        }

        public void Stop()
        {
            if (!IsCapturing)
                return;

            IsCapturing = false;
            _cts?.Cancel();

            try
            {
                _audioClient?.Stop();
            }
            catch { }

            try
            {
                _captureTask?.Wait(200);
            }
            catch { }

            Cleanup();
            Console.WriteLine("[ProcessAudio] Captura de audio de proceso detenida.");
        }

        private void Cleanup()
        {
            IsCapturing = false;
            _cts?.Dispose();
            _cts = null;
            _captureTask = null;

            if (_audioClient != null)
            {
                try { Marshal.ReleaseComObject(_audioClient); } catch { }
                _audioClient = null;
            }

            if (_captureClient != null)
            {
                try { Marshal.ReleaseComObject(_captureClient); } catch { }
                _captureClient = null;
            }

            _bufferEvent?.Dispose();
            _bufferEvent = null;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            Stop();
            _disposed = true;
        }

        #region Native COM Definitions

        private enum AUDIOCLIENT_ACTIVATION_TYPE
        {
            Default = 0,
            ProcessLoopback = 1
        }

        private enum PROCESS_LOOPBACK_MODE
        {
            IncludeTargetProcessTree = 0,
            ExcludeTargetProcessTree = 1
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
        {
            public uint TargetProcessId;
            public PROCESS_LOOPBACK_MODE ProcessLoopbackMode;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AUDIOCLIENT_ACTIVATION_PARAMS
        {
            public AUDIOCLIENT_ACTIVATION_TYPE ActivationType;
            public AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BLOB
        {
            public int cbSize;
            public IntPtr pBlobData;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct PROPVARIANT
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(2)] public ushort wReserved1;
            [FieldOffset(4)] public ushort wReserved2;
            [FieldOffset(6)] public ushort wReserved3;
            [FieldOffset(8)] public BLOB blob;
        }

        [ComImport]
        [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IActivateAudioInterfaceCompletionHandler
        {
            void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
        }

        [ComImport]
        [Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAgileObject
        {
        }

        [ComImport]
        [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IActivateAudioInterfaceAsyncOperation
        {
            void GetActivateResult(out int hrResult, [MarshalAs(UnmanagedType.IUnknown)] out object ppUnk);
        }

        [ComImport]
        [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            [PreserveSig]
            int Initialize(
                int ShareMode,
                uint StreamFlags,
                long hnsBufferDuration,
                long hnsPeriodicity,
                [In] ref WAVEFORMATEX pFormat,
                [In] IntPtr AudioSessionGuid);

            [PreserveSig]
            int GetBufferSize(out uint pNumBufferFrames);

            [PreserveSig]
            int GetStreamLatency(out long phnsLatency);

            [PreserveSig]
            int GetCurrentPadding(out uint pNumPaddingFrames);

            [PreserveSig]
            int IsFormatSupported(int ShareMode, [In] ref WAVEFORMATEX pFormat, out IntPtr ppClosestMatch);

            [PreserveSig]
            int GetMixFormat(out IntPtr ppDeviceFormat);

            [PreserveSig]
            int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);

            [PreserveSig]
            int Start();

            [PreserveSig]
            int Stop();

            [PreserveSig]
            int Reset();

            [PreserveSig]
            int SetEventHandle(IntPtr eventHandle);

            [PreserveSig]
            int GetService([In, MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppv);
        }

        [ComImport]
        [Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            [PreserveSig]
            int GetBuffer(
                out IntPtr ppData,
                out uint pNumFramesToRead,
                out uint pdwFlags,
                out ulong pu64DevicePosition,
                out ulong pu64QPCPosition);

            [PreserveSig]
            int ReleaseBuffer(uint NumFramesRead);

            [PreserveSig]
            int GetNextPacketSize(out uint pNumFramesInNextPacket);
        }

        [DllImport("mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int ActivateAudioInterfaceAsync(
            [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            IntPtr activationParams,
            IActivateAudioInterfaceCompletionHandler completionHandler,
            out IActivateAudioInterfaceAsyncOperation activationOperation);

        private sealed class AudioInterfaceCompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
        {
            private readonly TaskCompletionSource<object> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task<object> Task => _tcs.Task;

            public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
            {
                try
                {
                    activateOperation.GetActivateResult(out int hr, out object ppUnk);
                    if (hr >= 0 && ppUnk != null)
                    {
                        _tcs.TrySetResult(ppUnk);
                    }
                    else
                    {
                        _tcs.TrySetException(new COMException("Error activando interfaz de audio por proceso", hr));
                    }
                }
                catch (Exception ex)
                {
                    _tcs.TrySetException(ex);
                }
            }
        }

        #endregion
    }
}