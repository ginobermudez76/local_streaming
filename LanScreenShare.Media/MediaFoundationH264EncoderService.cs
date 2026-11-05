using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using LanScreenShare.Core.Interfaces;
using Vortice.MediaFoundation;

namespace LanScreenShare.Media
{
    [SupportedOSPlatform("windows")]
    public class MediaFoundationH264EncoderService : IVideoEncoderService
    {
        private static readonly Guid CODECAPI_AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a0ec04");
        private static readonly Guid CODECAPI_AVEncMPVGOPSize = new("95f74a0e-d934-4bc7-a7f1-2130e63f385c");
        private static readonly Guid IID_IMFMediaEventGenerator = new("2CD0BD52-BCD5-4B89-B62C-EADC0C031E7D");
        private static readonly Guid IID_ICodecAPI = new("901d850e-31a8-4b7d-a8d9-06e129f30171");

        private IMFTransform? _encoder;
        private IMFMediaEventGenerator? _eventGen;
        private bool _isAsync;
        private bool _mftProvidesSamples;
        private int _outputBufferSize = 1024 * 1024;

        private int _width;
        private int _height;
        private int _fps = 60;
        private int _bitrate = 8_000_000;
        private long _sampleTime;
        private byte[]? _nv12Buffer;
        private readonly object _lock = new();
        private bool _disposed;

        public string CodecName => "h264";
        public bool IsHardwareAccelerated { get; private set; }
        public string EncoderDescription { get; private set; } = "No inicializado";
        public MftEncoderInfo? ActiveEncoderInfo { get; private set; }

        public MediaFoundationH264EncoderService(int targetFps = 60, int bitrate = 8_000_000)
        {
            _fps = Math.Clamp(targetFps, 15, 120);
            _bitrate = bitrate;
            MediaFactory.MFStartup().CheckError();
        }

        public void UpdateBitrate(int bitrate)
        {
            if (_bitrate == bitrate) return;
            lock (_lock)
            {
                _bitrate = bitrate;
                if (_encoder != null && _width > 0 && _height > 0)
                {
                    try
                    {
                        InitializeEncoder(_width, _height);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[H264Encoder] Error actualizando bitrate: {ex.Message}");
                    }
                }
            }
        }

        public void UpdateFps(int targetFps)
        {
            int fps = Math.Clamp(targetFps, 15, 120);
            if (_fps == fps) return;
            lock (_lock)
            {
                _fps = fps;
                if (_encoder != null && _width > 0 && _height > 0)
                {
                    try
                    {
                        InitializeEncoder(_width, _height);
                    }
                    catch { }
                }
            }
        }

        private void CleanupCurrentEncoder()
        {
            _eventGen?.Dispose();
            _eventGen = null;
            _encoder?.Dispose();
            _encoder = null;
            _isAsync = false;
            _mftProvidesSamples = false;
        }

        private void InitializeEncoder(int width, int height)
        {
            CleanupCurrentEncoder();

            _width = width;
            _height = height;
            _nv12Buffer = new byte[width * height * 3 / 2];

            var candidates = HardwareMftEncoderDiscovery.EnumerateEncoders();
            bool initialized = false;

            foreach (var candidate in candidates)
            {
                try
                {
                    var (transform, _) = HardwareMftEncoderDiscovery.ActivateEncoder(candidate);
                    if (transform == null)
                        continue;

                    _encoder = transform;

                    // Desbloquear MFT asíncrono para GPU si lo admite
                    try
                    {
                        _encoder.Attributes.Set(HardwareMftEncoderDiscovery.MF_TRANSFORM_ASYNC_UNLOCK, 1);
                    }
                    catch
                    {
                        // Transformadores puramente síncronos no requieren ni soportan ASYNC_UNLOCK
                    }

                    // Configurar tipo de salida (H.264)
                    using (IMFMediaType outputType = MediaFactory.MFCreateMediaType())
                    {
                        outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                        outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                        outputType.Set(MediaTypeAttributeKeys.AvgBitrate, _bitrate);
                        MediaFactory.MFSetAttributeSize(outputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
                        MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.FrameRate, (uint)_fps, 1u);
                        MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.PixelAspectRatio, 1u, 1u);
                        outputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);

                        _encoder.SetOutputType(0, outputType, 0);
                    }

                    // Configurar tipo de entrada (NV12)
                    using (IMFMediaType inputType = MediaFactory.MFCreateMediaType())
                    {
                        inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                        inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
                        MediaFactory.MFSetAttributeSize(inputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
                        MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.FrameRate, (uint)_fps, 1u);
                        MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.PixelAspectRatio, 1u, 1u);
                        inputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);

                        _encoder.SetInputType(0, inputType, 0);
                    }

                    // Configurar parámetros de baja latencia vía ICodecAPI si está soportado
                    ConfigureCodecApi(_encoder, _fps);

                    // Mensajes de inicio de transmisión
                    _encoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
                    _encoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

                    // Inspeccionar información del flujo de salida
                    try
                    {
                        var streamInfo = _encoder.GetOutputStreamInfo(0);
                        uint flags = (uint)streamInfo.Flags;
                        _mftProvidesSamples = (flags & 0x00000100) != 0 || (flags & 0x00000200) != 0;
                        if (streamInfo.Size > 0)
                        {
                            _outputBufferSize = Math.Max(streamInfo.Size, 256 * 1024);
                        }
                    }
                    catch
                    {
                        _mftProvidesSamples = false;
                    }

                    // Consultar si implementa IMFMediaEventGenerator (MFT Asíncrono)
                    int hr = Marshal.QueryInterface(_encoder.NativePointer, in IID_IMFMediaEventGenerator, out IntPtr pEventGen);
                    if (hr == 0 && pEventGen != IntPtr.Zero)
                    {
                        _eventGen = new IMFMediaEventGenerator(pEventGen);
                        _isAsync = true;
                    }
                    else
                    {
                        _isAsync = false;
                    }

                    ActiveEncoderInfo = candidate;
                    IsHardwareAccelerated = candidate.IsHardware;
                    EncoderDescription = candidate.DisplayName;
                    initialized = true;

                    Console.WriteLine($"[H264Encoder] Inicializado exitosamente codificador: {EncoderDescription} (Hardware: {IsHardwareAccelerated}, Asíncrono: {_isAsync}, {width}x{height} @ {_fps} FPS, {_bitrate / 1_000_000} Mbps)");
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[H264Encoder] Aviso: Falló inicialización de '{candidate.DisplayName}': {ex.Message}. Intentando siguiente...");
                    CleanupCurrentEncoder();
                }
            }

            if (!initialized || _encoder == null)
            {
                throw new InvalidOperationException("No se pudo inicializar ningún codificador H.264 (hardware ni software).");
            }
        }

        private static void ConfigureCodecApi(IMFTransform encoder, int fps)
        {
            int hr = Marshal.QueryInterface(encoder.NativePointer, in IID_ICodecAPI, out IntPtr pCodecApi);
            if (hr == 0 && pCodecApi != IntPtr.Zero)
            {
                try
                {
                    var codecApi = (ICodecAPI)Marshal.GetObjectForIUnknown(pCodecApi);
                    object lowLatency = 1;
                    codecApi.SetValue(CODECAPI_AVLowLatencyMode, ref lowLatency);

                    object gopSize = (uint)fps;
                    codecApi.SetValue(CODECAPI_AVEncMPVGOPSize, ref gopSize);
                }
                catch { }
                finally
                {
                    Marshal.Release(pCodecApi);
                }
            }
        }

        public byte[] EncodeFrame(byte[] bgraData, int width, int height, bool forceKeyframe = false)
        {
            lock (_lock)
            {
                if (_disposed)
                    return Array.Empty<byte>();

                if (_encoder == null || _width != width || _height != height)
                {
                    InitializeEncoder(width, height);
                }

                if (_nv12Buffer == null || _encoder == null)
                    return Array.Empty<byte>();

                try
                {
                    // Conversión paralela BGRA -> NV12 optimizada para SIMD y núcleos múltiples
                    BgraToNv12(bgraData, _nv12Buffer, width, height);

                    using IMFMediaBuffer inputBuffer = MediaFactory.MFCreateMemoryBuffer(_nv12Buffer.Length);
                    inputBuffer.Lock(out IntPtr pBuffer, out _, out _);
                    try
                    {
                        Marshal.Copy(_nv12Buffer, 0, pBuffer, _nv12Buffer.Length);
                        inputBuffer.CurrentLength = _nv12Buffer.Length;
                    }
                    finally
                    {
                        inputBuffer.Unlock();
                    }

                    using IMFSample inputSample = MediaFactory.MFCreateSample();
                    inputSample.AddBuffer(inputBuffer);
                    inputSample.SampleTime = _sampleTime;
                    inputSample.SampleDuration = 10_000_000 / _fps;
                    _sampleTime += inputSample.SampleDuration;

                    if (forceKeyframe)
                    {
                        inputSample.Set(SampleAttributeKeys.CleanPoint, 1);
                    }

                    if (_isAsync && _eventGen != null)
                    {
                        return EncodeFrameAsync(inputSample);
                    }
                    else
                    {
                        return EncodeFrameSync(inputSample);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[H264Encoder] Error en codificación ({ex.Message}). Realizando fallback a software CPU...");
                    try
                    {
                        FallbackToSoftwareCpu(width, height);
                        return EncodeFrame(bgraData, width, height, forceKeyframe);
                    }
                    catch (Exception fbEx)
                    {
                        Console.WriteLine($"[H264Encoder] Fallback también falló: {fbEx.Message}");
                        return Array.Empty<byte>();
                    }
                }
            }
        }

        private byte[] EncodeFrameSync(IMFSample inputSample)
        {
            if (_encoder == null) return Array.Empty<byte>();

            _encoder.ProcessInput(0, inputSample, 0);

            using var outputMs = new MemoryStream();

            while (true)
            {
                using IMFSample outputSample = MediaFactory.MFCreateSample();
                using IMFMediaBuffer outputBuffer = MediaFactory.MFCreateMemoryBuffer(_outputBufferSize);
                outputSample.AddBuffer(outputBuffer);

                var outBuffer = new OutputDataBuffer
                {
                    StreamID = 0,
                    Sample = outputSample,
                    Status = 0
                };

                var result = _encoder.ProcessOutput(ProcessOutputFlags.None, 1, ref outBuffer, out _);
                if (result.Failure)
                {
                    break;
                }

                int currentLength = outputBuffer.CurrentLength;
                if (currentLength > 0)
                {
                    outputBuffer.Lock(out IntPtr pData, out _, out _);
                    try
                    {
                        byte[] chunk = new byte[currentLength];
                        Marshal.Copy(pData, chunk, 0, currentLength);
                        outputMs.Write(chunk, 0, chunk.Length);
                    }
                    finally
                    {
                        outputBuffer.Unlock();
                    }
                }
            }

            return outputMs.ToArray();
        }

        private byte[] EncodeFrameAsync(IMFSample inputSample)
        {
            if (_encoder == null || _eventGen == null) return Array.Empty<byte>();

            using var outputMs = new MemoryStream();

            // 1. Drenar cualquier salida lista antes del nuevo fotograma
            DrainAsyncEvents(outputMs);

            // 2. Enviar el nuevo fotograma de entrada
            _encoder.ProcessInput(0, inputSample, 0);

            // 3. Drenar eventos generados con breve margen de espera para la GPU
            for (int retry = 0; retry < 8; retry++)
            {
                DrainAsyncEvents(outputMs);
                if (outputMs.Length > 0)
                    break;

                System.Threading.Thread.Sleep(2);
            }

            return outputMs.ToArray();
        }

        private void DrainAsyncEvents(MemoryStream outputMs)
        {
            if (_encoder == null || _eventGen == null) return;

            for (int attempt = 0; attempt < 8; attempt++)
            {
                IMFMediaEvent? ev = null;
                try
                {
                    ev = _eventGen.GetEvent(1); // MF_EVENT_FLAG_NO_WAIT = 1
                }
                catch
                {
                    break;
                }

                if (ev == null) break;

                using (ev)
                {
                    if ((int)ev.EventType == 602) // METransformHaveOutput
                    {
                        IMFSample? callerSample = null;
                        IMFMediaBuffer? callerBuf = null;

                        if (!_mftProvidesSamples)
                        {
                            callerSample = MediaFactory.MFCreateSample();
                            callerBuf = MediaFactory.MFCreateMemoryBuffer(_outputBufferSize);
                            callerSample.AddBuffer(callerBuf);
                        }

                        var outData = new OutputDataBuffer
                        {
                            StreamID = 0,
                            Sample = callerSample,
                            Status = 0
                        };

                        try
                        {
                            var res = _encoder.ProcessOutput(ProcessOutputFlags.None, 1, ref outData, out _);
                            if (res.Success)
                            {
                                var produced = outData.Sample ?? callerSample;
                                if (produced != null)
                                {
                                    using var contiguous = produced.ConvertToContiguousBuffer();
                                    int len = contiguous.CurrentLength;
                                    if (len > 0)
                                    {
                                        contiguous.Lock(out IntPtr pData, out _, out _);
                                        try
                                        {
                                            byte[] chunk = new byte[len];
                                            Marshal.Copy(pData, chunk, 0, len);
                                            outputMs.Write(chunk, 0, len);
                                        }
                                        finally
                                        {
                                            contiguous.Unlock();
                                        }
                                    }
                                }
                            }
                        }
                        finally
                        {
                            if (outData.Sample != null && outData.Sample != callerSample)
                            {
                                outData.Sample.Dispose();
                            }
                            callerBuf?.Dispose();
                            callerSample?.Dispose();
                        }
                    }
                }
            }
        }

        private void FallbackToSoftwareCpu(int width, int height)
        {
            CleanupCurrentEncoder();

            var swInfo = new MftEncoderInfo(
                FriendlyName: "H264 Encoder MFT",
                DisplayName: "CPU (Software Microsoft)",
                IsHardware: false,
                IsAsync: false,
                Vendor: EncoderGpuVendor.Software,
                Clsid: HardwareMftEncoderDiscovery.CLSID_CMSH264EncoderMFT,
                Priority: 0
            );

            var (transform, _) = HardwareMftEncoderDiscovery.ActivateEncoder(swInfo);
            if (transform == null)
                throw new InvalidOperationException("No se pudo activar el codificador por software como respaldo.");

            _encoder = transform;
            _isAsync = false;
            _mftProvidesSamples = false;

            using (IMFMediaType outputType = MediaFactory.MFCreateMediaType())
            {
                outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                outputType.Set(MediaTypeAttributeKeys.AvgBitrate, _bitrate);
                MediaFactory.MFSetAttributeSize(outputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
                MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.FrameRate, (uint)_fps, 1u);
                MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.PixelAspectRatio, 1u, 1u);
                outputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);

                _encoder.SetOutputType(0, outputType, 0);
            }

            using (IMFMediaType inputType = MediaFactory.MFCreateMediaType())
            {
                inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
                MediaFactory.MFSetAttributeSize(inputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
                MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.FrameRate, (uint)_fps, 1u);
                MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.PixelAspectRatio, 1u, 1u);
                inputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);

                _encoder.SetInputType(0, inputType, 0);
            }

            ConfigureCodecApi(_encoder, _fps);

            _encoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _encoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

            ActiveEncoderInfo = swInfo;
            IsHardwareAccelerated = false;
            EncoderDescription = swInfo.DisplayName;

            Console.WriteLine("[H264Encoder] Fallback a CPU (Software Microsoft) completado exitosamente.");
        }

        public static void BgraToNv12(byte[] bgra, byte[] nv12, int width, int height)
        {
            int frameSize = width * height;
            int halfHeight = height / 2;

            Parallel.For(0, halfHeight, k =>
            {
                int y0 = 2 * k;
                int y1 = y0 + 1;

                int row0BgraOffset = y0 * width * 4;
                int row1BgraOffset = y1 * width * 4;

                int row0YOffset = y0 * width;
                int row1YOffset = y1 * width;

                int uvRowOffset = frameSize + k * width;

                for (int x = 0; x < width; x += 2)
                {
                    int x1 = x + 1;

                    // Pixel (x, y0)
                    int idx00 = row0BgraOffset + (x << 2);
                    int b00 = bgra[idx00];
                    int g00 = bgra[idx00 + 1];
                    int r00 = bgra[idx00 + 2];
                    nv12[row0YOffset + x] = (byte)Math.Clamp(((66 * r00 + 129 * g00 + 25 * b00 + 128) >> 8) + 16, 0, 255);

                    // Pixel (x+1, y0)
                    int idx01 = row0BgraOffset + (x1 << 2);
                    int b01 = bgra[idx01];
                    int g01 = bgra[idx01 + 1];
                    int r01 = bgra[idx01 + 2];
                    nv12[row0YOffset + x1] = (byte)Math.Clamp(((66 * r01 + 129 * g01 + 25 * b01 + 128) >> 8) + 16, 0, 255);

                    // Pixel (x, y1)
                    int idx10 = row1BgraOffset + (x << 2);
                    int b10 = bgra[idx10];
                    int g10 = bgra[idx10 + 1];
                    int r10 = bgra[idx10 + 2];
                    nv12[row1YOffset + x] = (byte)Math.Clamp(((66 * r10 + 129 * g10 + 25 * b10 + 128) >> 8) + 16, 0, 255);

                    // Pixel (x+1, y1)
                    int idx11 = row1BgraOffset + (x1 << 2);
                    int b11 = bgra[idx11];
                    int g11 = bgra[idx11 + 1];
                    int r11 = bgra[idx11 + 2];
                    nv12[row1YOffset + x1] = (byte)Math.Clamp(((66 * r11 + 129 * g11 + 25 * b11 + 128) >> 8) + 16, 0, 255);

                    // UV promedio 2x2
                    int avgR = (r00 + r01 + r10 + r11 + 2) >> 2;
                    int avgG = (g00 + g01 + g10 + g11 + 2) >> 2;
                    int avgB = (b00 + b01 + b10 + b11 + 2) >> 2;

                    int u = ((-38 * avgR - 74 * avgG + 112 * avgB + 128) >> 8) + 128;
                    int v = ((112 * avgR - 94 * avgG - 18 * avgB + 128) >> 8) + 128;

                    nv12[uvRowOffset + x] = (byte)Math.Clamp(u, 0, 255);
                    nv12[uvRowOffset + x + 1] = (byte)Math.Clamp(v, 0, 255);
                }
            });
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                    return;

                _disposed = true;
                CleanupCurrentEncoder();
                GC.SuppressFinalize(this);
            }
        }
    }

    [ComImport]
    [Guid("901d850e-31a8-4b7d-a8d9-06e129f30171")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ICodecAPI
    {
        void IsSupported([In, MarshalAs(UnmanagedType.LPStruct)] Guid api);
        void IsModifiable([In, MarshalAs(UnmanagedType.LPStruct)] Guid api);
        void GetParameterRange([In, MarshalAs(UnmanagedType.LPStruct)] Guid api, out object valueMin, out object valueMax, out object delta);
        void GetValue([In, MarshalAs(UnmanagedType.LPStruct)] Guid api, out object value);
        void SetValue([In, MarshalAs(UnmanagedType.LPStruct)] Guid api, [In] ref object value);
        void RegisterForEvent([In, MarshalAs(UnmanagedType.LPStruct)] Guid api, IntPtr userData);
        void UnregisterForEvent([In, MarshalAs(UnmanagedType.LPStruct)] Guid api);
        void SetAllDefaults();
        void SetValueWithLargeData([In, MarshalAs(UnmanagedType.LPStruct)] Guid api, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] data, uint length);
        void GetValueWithLargeData([In, MarshalAs(UnmanagedType.LPStruct)] Guid api, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] data, out uint length);
        void GetAllSettings(IntPtr stream);
        void SetAllSettings(IntPtr stream);
        void SetAllSettingsWithNotify(IntPtr stream, out Guid changedParamGuid, out uint changedParamCount);
    }
}
