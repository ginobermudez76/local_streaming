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
    public sealed class ProcessLoopbackAudioCapture : IDisposable
    {
        public static Action<string>? OnLog;

        private static void Log(string msg)
        {
            Console.WriteLine(msg);
            OnLog?.Invoke(msg);
        }
        private const string VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK = @"VAD\Process_Loopback";
        private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        private const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
        private const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
        private const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;

        public event EventHandler<CapturedAudioChunk>? AudioCaptured;
        public bool IsCapturing { get; private set; }

        private CancellationTokenSource? _cts;
        private Task? _captureTask;
        private IAudioClient? _audioClient;
        private IAudioCaptureClient? _captureClient;
        private AutoResetEvent? _bufferEvent;
        private bool _disposed;

        private static (IntPtr pProp, IntPtr pParams) CreateActivationPropVariant(uint targetPid, bool includeProcess)
        {
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
            return (pProp, pParams);
        }

        private async Task<IAudioClient?> ActivateProcessLoopbackClientAsync(uint targetPid, bool includeProcess, CancellationToken cancellationToken)
        {
            var handler = new AudioInterfaceCompletionHandler();
            var (pProp, pParams) = CreateActivationPropVariant(targetPid, includeProcess);

            IActivateAudioInterfaceAsyncOperation? asyncOp = null;
            try
            {
                int hr = ActivateAudioInterfaceAsync(
                    VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK,
                    typeof(IAudioClient).GUID,
                    pProp,
                    handler,
                    out asyncOp);

                if (hr < 0)
                {
                    Log($"[ProcessAudio] ActivateAudioInterfaceAsync falló con HRESULT: 0x{hr:X8}");
                    return null;
                }

                using var timeoutCts = new CancellationTokenSource(2500);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                try
                {
                    var rawClient = await handler.Task.WaitAsync(linkedCts.Token);
                    return rawClient as IAudioClient;
                }
                catch (Exception ex)
                {
                    Log($"[ProcessAudio] Timeout o error esperando activación COM (targetPid={targetPid}): {ex.Message}");
                    return null;
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
        }

        private static WAVEFORMATEXTENSIBLE CreateWaveFormatExtensible(int sampleRate) => new()
        {
            Format = new WAVEFORMATEX
            {
                wFormatTag = 0xFFFE, // WAVE_FORMAT_EXTENSIBLE
                nChannels = 2,
                nSamplesPerSec = (uint)sampleRate,
                nAvgBytesPerSec = (uint)(sampleRate * 8),
                nBlockAlign = 8,
                wBitsPerSample = 32,
                cbSize = 22
            },
            wValidBitsPerSample = 32,
            dwChannelMask = 0x0003, // SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT
            SubFormat = new Guid("00000003-0000-0010-8000-00aa00389b71") // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
        };

        private static int TryInitializeAtRate(IAudioClient audioClient, int sampleRate, long bufferDurationHns, IntPtr pFormat)
        {
            var wfExt = CreateWaveFormatExtensible(sampleRate);
            Marshal.StructureToPtr(wfExt, pFormat, false);

            uint streamFlags = AUDCLNT_STREAMFLAGS_LOOPBACK |
                               AUDCLNT_STREAMFLAGS_EVENTCALLBACK |
                               AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
                               AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;

            int hr = audioClient.Initialize(0, streamFlags, bufferDurationHns, 0, pFormat, IntPtr.Zero);
            if (hr < 0)
            {
                streamFlags = AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK;
                hr = audioClient.Initialize(0, streamFlags, bufferDurationHns, 0, pFormat, IntPtr.Zero);
            }

            return hr;
        }

        private static int InitializeAudioClientFormat(IAudioClient audioClient, out WaveFormat waveFormat)
        {
            IntPtr pFormat = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEXTENSIBLE>());
            long bufferDurationHns = 10000000; // 1 segundo (en unidades de 100ns)
            try
            {
                int hr = TryInitializeAtRate(audioClient, 48000, bufferDurationHns, pFormat);
                if (hr >= 0)
                {
                    waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
                    return hr;
                }

                Console.WriteLine($"[ProcessAudio] Initialize 48kHz falló con 0x{hr:X8}, reintentando 44.1kHz...");
                hr = TryInitializeAtRate(audioClient, 44100, bufferDurationHns, pFormat);
                waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
                return hr;
            }
            finally
            {
                Marshal.FreeHGlobal(pFormat);
            }
        }

        public Task<bool> StartAsync(int processId, CancellationToken cancellationToken = default)
        {
            if (IsCapturing)
                return Task.FromResult(false);

            return Task.Run(async () =>
            {
                bool includeProcess = processId > 0;
                uint targetPid = includeProcess ? (uint)processId : (uint)Environment.ProcessId;

                try
                {
                    _audioClient = await ActivateProcessLoopbackClientAsync(targetPid, includeProcess, cancellationToken);
                    if (_audioClient == null)
                    {
                        Log("[ProcessAudio] El objeto activado no implementa IAudioClient");
                        return false;
                    }

                    if (!TryInitializeClientPipeline(_audioClient, out var waveFormat, out var captureClient))
                    {
                        Cleanup();
                        return false;
                    }

                    _captureClient = captureClient;
                    _cts = new CancellationTokenSource();
                    IsCapturing = true;
                    _captureTask = Task.Run(() => CaptureLoop(waveFormat, _cts.Token));
                    string modeDesc = includeProcess ? $"PID {processId} (aislado)" : $"Escritorio completo (excluyendo PID {targetPid})";
                    Log($"[ProcessAudio] Captura iniciada: {modeDesc} ({waveFormat.SampleRate}Hz, {waveFormat.Channels}ch, {waveFormat.BitsPerSample}bits)");
                    return true;
                }
                catch (Exception ex)
                {
                    Log($"[ProcessAudio] Error general iniciando captura de proceso: {ex}");
                    Cleanup();
                    return false;
                }
            }, cancellationToken);
        }

        private bool TryInitializeClientPipeline(
            IAudioClient client,
            out WaveFormat waveFormat,
            out IAudioCaptureClient captureClient)
        {
            captureClient = null!;
            int hr = InitializeAudioClientFormat(client, out waveFormat);
            if (hr < 0)
            {
                Log($"[ProcessAudio] IAudioClient.Initialize falló con HRESULT: 0x{hr:X8}");
                return false;
            }

            _bufferEvent = new AutoResetEvent(false);
            hr = client.SetEventHandle(_bufferEvent.SafeWaitHandle.DangerousGetHandle());
            if (hr < 0)
            {
                Log($"[ProcessAudio] SetEventHandle falló con HRESULT: 0x{hr:X8}");
                return false;
            }

            hr = client.GetService(typeof(IAudioCaptureClient).GUID, out object rawCaptureClient);
            if (hr < 0 || rawCaptureClient == null)
            {
                Log($"[ProcessAudio] GetService(IAudioCaptureClient) falló con HRESULT: 0x{hr:X8}");
                return false;
            }

            captureClient = (IAudioCaptureClient)rawCaptureClient;
            hr = client.Start();
            if (hr < 0)
            {
                Log($"[ProcessAudio] IAudioClient.Start falló con HRESULT: 0x{hr:X8}");
                return false;
            }

            return true;
        }

        private static bool IsValidBuffer(int hrGet, IntPtr pData, uint numFrames) =>
            hrGet >= 0 && pData != IntPtr.Zero && numFrames > 0;

        private bool TryReadAndDispatchPacket(WaveFormat waveFormat, short formatEncoding)
        {
            int hrNext = _captureClient!.GetNextPacketSize(out uint packetSize);
            if (hrNext < 0 || packetSize == 0)
                return false;

            int hrGet = _captureClient.GetBuffer(out IntPtr pData, out uint numFrames, out uint flags, out _, out _);
            if (!IsValidBuffer(hrGet, pData, numFrames))
                return false;

            int byteCount = (int)(numFrames * waveFormat.BlockAlign);
            byte[] buffer = new byte[byteCount];

            if ((flags & 2) == 0) // AUDCLNT_BUFFERFLAGS_SILENT
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
            return true;
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
                if (index == 1)
                    break;

                DrainPackets(waveFormat, formatEncoding, token);
            }
        }

        private void DrainPackets(WaveFormat waveFormat, short formatEncoding, CancellationToken token)
        {
            while (!token.IsCancellationRequested && TryReadAndDispatchPacket(waveFormat, formatEncoding))
            {
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

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WAVEFORMATEXTENSIBLE
        {
            public WAVEFORMATEX Format;
            public ushort wValidBitsPerSample;
            public uint dwChannelMask;
            public Guid SubFormat;
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
            [PreserveSig]
            int GetActivateResult(out int hrResult, [MarshalAs(UnmanagedType.IUnknown)] out object ppUnk);
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
                IntPtr pFormat,
                IntPtr AudioSessionGuid);

            [PreserveSig]
            int GetBufferSize(out uint pNumBufferFrames);

            [PreserveSig]
            int GetStreamLatency(out long phnsLatency);

            [PreserveSig]
            int GetCurrentPadding(out uint pNumPaddingFrames);

            [PreserveSig]
            int IsFormatSupported(int ShareMode, IntPtr pFormat, out IntPtr ppClosestMatch);

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

            private static bool IsActivationSuccessful(int hrMethod, int hrActivate, object? unk) =>
                hrMethod >= 0 && hrActivate >= 0 && unk != null;

            public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
            {
                try
                {
                    int hrMethod = activateOperation.GetActivateResult(out int hrActivate, out object ppUnk);
                    if (IsActivationSuccessful(hrMethod, hrActivate, ppUnk))
                    {
                        _tcs.TrySetResult(ppUnk);
                    }
                    else
                    {
                        int failHr = hrMethod < 0 ? hrMethod : hrActivate;
                        _tcs.TrySetException(new COMException($"Error activando interfaz de audio: hrMethod=0x{hrMethod:X8}, hrActivate=0x{hrActivate:X8}", failHr));
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