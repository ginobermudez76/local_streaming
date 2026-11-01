using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LanScreenShare.Core.Interfaces;
using Vortice.MediaFoundation;

namespace LanScreenShare.Media
{
    [SupportedOSPlatform("windows")]
    public class MediaFoundationH264EncoderService : IVideoEncoderService
    {
        private static readonly Guid CLSID_CMSH264EncoderMFT = new("6ca510d0-568d-4e22-8a2d-97254c7c59d8");
        private static readonly Guid CODECAPI_AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a0ec04");
        private static readonly Guid CODECAPI_AVEncMPVGOPSize = new("95f74a0e-d934-4bc7-a7f1-2130e63f385c");

        private IMFTransform? _encoder;
        private int _width;
        private int _height;
        private int _fps = 60;
        private int _bitrate = 8_000_000;
        private long _sampleTime;
        private byte[]? _nv12Buffer;
        private readonly object _lock = new();
        private bool _disposed;

        public string CodecName => "h264";

        public MediaFoundationH264EncoderService(int targetFps = 60, int bitrate = 8_000_000)
        {
            _fps = Math.Clamp(targetFps, 15, 120);
            _bitrate = bitrate;
            MediaFactory.MFStartup().CheckError();
        }

        private void InitializeEncoder(int width, int height)
        {
            _encoder?.Dispose();
            _encoder = null;

            _width = width;
            _height = height;
            _nv12Buffer = new byte[width * height * 3 / 2];

            Type encoderType = Type.GetTypeFromCLSID(CLSID_CMSH264EncoderMFT)
                ?? throw new InvalidOperationException("No se encontró el codificador de video H.264 de Windows Media Foundation.");
            
            _encoder = (IMFTransform)Activator.CreateInstance(encoderType)!;

            if (_encoder is ICodecAPI codecApi)
            {
                try
                {
                    object lowLatency = 1;
                    codecApi.SetValue(CODECAPI_AVLowLatencyMode, ref lowLatency);

                    object gopSize = (uint)_fps;
                    codecApi.SetValue(CODECAPI_AVEncMPVGOPSize, ref gopSize);
                }
                catch
                {
                }
            }

            // 1. Configurar Tipo de Salida (H.264)
            IMFMediaType outputType = MediaFactory.MFCreateMediaType();
            outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            outputType.Set(MediaTypeAttributeKeys.AvgBitrate, _bitrate);
            MediaFactory.MFSetAttributeSize(outputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
            MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.FrameRate, (uint)_fps, 1u);
            MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.PixelAspectRatio, 1u, 1u);
            outputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);

            _encoder.SetOutputType(0, outputType, 0);
            outputType.Dispose();

            // 2. Configurar Tipo de Entrada (NV12)
            IMFMediaType inputType = MediaFactory.MFCreateMediaType();
            inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            MediaFactory.MFSetAttributeSize(inputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
            MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.FrameRate, (uint)_fps, 1u);
            MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.PixelAspectRatio, 1u, 1u);
            inputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);

            _encoder.SetInputType(0, inputType, 0);
            inputType.Dispose();

            _encoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _encoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

            Console.WriteLine($"[H264Encoder] Inicializado codificador H.264 ({width}x{height} @ {_fps} FPS, {_bitrate / 1_000_000} Mbps)");
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

                BgraToNv12(bgraData, _nv12Buffer, width, height);

                IMFMediaBuffer inputBuffer = MediaFactory.MFCreateMemoryBuffer(_nv12Buffer.Length);
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

                IMFSample inputSample = MediaFactory.MFCreateSample();
                inputSample.AddBuffer(inputBuffer);
                inputSample.SampleTime = _sampleTime;
                inputSample.SampleDuration = 10_000_000 / _fps;
                _sampleTime += inputSample.SampleDuration;

                _encoder.ProcessInput(0, inputSample, 0);
                inputBuffer.Dispose();
                inputSample.Dispose();

                return DrainOutput();
            }
        }

        private byte[] DrainOutput()
        {
            if (_encoder == null)
                return Array.Empty<byte>();

            using var outputMs = new System.IO.MemoryStream();

            while (true)
            {
                IMFSample outputSample = MediaFactory.MFCreateSample();
                IMFMediaBuffer outputBuffer = MediaFactory.MFCreateMemoryBuffer(1024 * 1024);
                outputSample.AddBuffer(outputBuffer);

                var outBuffer = new OutputDataBuffer
                {
                    StreamID = 0,
                    Sample = outputSample,
                    Status = 0
                };

                var result = _encoder.ProcessOutput(ProcessOutputFlags.None, 1, ref outBuffer, out var status);
                if (result.Failure)
                {
                    outputBuffer.Dispose();
                    outputSample.Dispose();
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

                outputBuffer.Dispose();
                outputSample.Dispose();
            }

            return outputMs.ToArray();
        }

        public static void BgraToNv12(byte[] bgra, byte[] nv12, int width, int height)
        {
            int frameSize = width * height;
            int yIndex = 0;
            int uvIndex = frameSize;

            for (int j = 0; j < height; j++)
            {
                int rowOffset = j * width * 4;
                bool isEvenRow = (j & 1) == 0;

                for (int i = 0; i < width; i++)
                {
                    int bgraIndex = rowOffset + (i << 2);
                    int b = bgra[bgraIndex];
                    int g = bgra[bgraIndex + 1];
                    int r = bgra[bgraIndex + 2];

                    int y = ((66 * r + 129 * g + 25 * b + 128) >> 8) + 16;
                    nv12[yIndex++] = (byte)Math.Clamp(y, 0, 255);

                    if (isEvenRow && (i & 1) == 0)
                    {
                        int u = ((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128;
                        int v = ((112 * r - 94 * g - 18 * b + 128) >> 8) + 128;

                        nv12[uvIndex++] = (byte)Math.Clamp(u, 0, 255);
                        nv12[uvIndex++] = (byte)Math.Clamp(v, 0, 255);
                    }
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _encoder?.Dispose();
                _encoder = null;

                try { MediaFactory.MFShutdown(); } catch { }
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
