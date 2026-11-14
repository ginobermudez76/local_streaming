using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;
using Vortice.MediaFoundation;

namespace LanScreenShare.Media
{
    [SupportedOSPlatform("windows")]
    public class MediaFoundationH264DecoderService : IVideoDecoderService
    {
        private static readonly Guid CLSID_CMSH264DecoderMFT = new("62ce7e72-4c71-4d20-b15d-452831a87d9d");
        private static readonly Guid CODECAPI_AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
        private static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
        private static readonly Guid IID_ICodecAPI = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
        private const int eAVEncH264VProfile_Base = 66;

        private IMFTransform? _decoder;
        private int _width;
        private int _height;
        private int _actualStride;
        private int _actualBufferHeight;
        private byte[]? _decodedBgraBuffer;
        private byte[]? _nv12Buffer;
        private long _sampleTimeCounter;
        private readonly object _lock = new();
        private bool _disposed;
        private bool _mftProvidesSamples;
        private int _outputBufferSize;

        public string CodecName => "h264";

        public MediaFoundationH264DecoderService()
        {
            MediaFactory.MFStartup().CheckError();
        }

        private void InitializeDecoder(int width, int height)
        {
            _decoder?.Dispose();
            _decoder = null;

            _width = width;
            _height = height;
            _actualStride = (width + 15) & ~15;
            _actualBufferHeight = (height + 15) & ~15;
            _decodedBgraBuffer = new byte[width * height * 4];
            _nv12Buffer = new byte[_actualStride * _actualBufferHeight * 3 / 2];
            _sampleTimeCounter = 0;

            _decoder = CreateDecoderTransform();

            ApplyDecoderLowLatency(_decoder);
            ConfigureDecoderMediaTypes(_decoder, width, height);
            ApplyDecoderLowLatency(_decoder);

            InspectOutputStreamInfo(width, height);

            _decoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _decoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

            Console.WriteLine($"[H264Decoder] Decodificador H.264 inicializado ({width}x{height} -> NV12 -> BGRA, ProvidesSamples: {_mftProvidesSamples})");
        }

        private static IMFTransform CreateDecoderTransform()
        {
            Type decoderType = Type.GetTypeFromCLSID(CLSID_CMSH264DecoderMFT)
                ?? throw new InvalidOperationException("No se encontró el decodificador H.264 de Windows Media Foundation.");

            object rawObj = Activator.CreateInstance(decoderType)
                ?? throw new InvalidOperationException("No se pudo crear la instancia del decodificador H.264.");

            IntPtr pUnk = Marshal.GetIUnknownForObject(rawObj);
            Guid iidTransform = HardwareMftEncoderDiscovery.IID_IMFTransform;
            int hrTransform = Marshal.QueryInterface(pUnk, in iidTransform, out IntPtr pTransform);
            Marshal.Release(pUnk);

            if (hrTransform != 0 || pTransform == IntPtr.Zero)
            {
                throw new InvalidOperationException($"No se pudo obtener la interfaz IMFTransform del decodificador H.264 (HRESULT 0x{hrTransform:X8}).");
            }

            return new IMFTransform(pTransform);
        }

        private static void ConfigureDecoderMediaTypes(IMFTransform decoder, int width, int height)
        {
            // 1. Configurar Tipo de Entrada (H.264 Baseline Profile)
            using (IMFMediaType inputType = MediaFactory.MFCreateMediaType())
            {
                inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
                inputType.Set(MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_Base);
                MediaFactory.MFSetAttributeSize(inputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
                MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.PixelAspectRatio, 1u, 1u);
                inputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
                decoder.SetInputType(0, inputType, 0);
            }

            // 2. Configurar Tipo de Salida (NV12 nativo de alta eficiencia)
            using (IMFMediaType outputType = MediaFactory.MFCreateMediaType())
            {
                outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
                outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
                MediaFactory.MFSetAttributeSize(outputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
                MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.PixelAspectRatio, 1u, 1u);
                outputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);
                decoder.SetOutputType(0, outputType, 0);
            }
        }

        private void InspectOutputStreamInfo(int width, int height)
        {
            if (_decoder == null) return;

            try
            {
                var streamInfo = _decoder.GetOutputStreamInfo(0);
                uint flags = (uint)streamInfo.Flags;
                _mftProvidesSamples = (flags & 0x00000100) != 0 || (flags & 0x00000200) != 0;
                _outputBufferSize = streamInfo.Size > 0 ? streamInfo.Size : (width * height * 3 / 2);
            }
            catch
            {
                _mftProvidesSamples = false;
                _outputBufferSize = width * height * 3 / 2;
            }
        }

        private static void ApplyDecoderLowLatency(IMFTransform decoder)
        {
            if (decoder == null) return;

            try
            {
                using var attrs = decoder.Attributes;
                attrs?.Set(CODECAPI_AVLowLatencyMode, 1);
            }
            catch { }

            int hrCodecApi = Marshal.QueryInterface(decoder.NativePointer, in IID_ICodecAPI, out IntPtr pCodecApi);
            if (hrCodecApi == 0 && pCodecApi != IntPtr.Zero)
            {
                ICodecAPI? codecApi = null;
                try
                {
                    codecApi = (ICodecAPI)Marshal.GetObjectForIUnknown(pCodecApi);
                    object lowLatencyUint = (uint)1;
                    try
                    {
                        codecApi.SetValue(CODECAPI_AVLowLatencyMode, ref lowLatencyUint);
                    }
                    catch
                    {
                        object lowLatencyBool = true;
                        try { codecApi.SetValue(CODECAPI_AVLowLatencyMode, ref lowLatencyBool); } catch { }
                    }
                }
                catch { }
                finally
                {
                    if (codecApi != null && Marshal.IsComObject(codecApi))
                    {
                        Marshal.ReleaseComObject(codecApi);
                    }
                    Marshal.Release(pCodecApi);
                }
            }
        }

        public readonly record struct VideoFrameDimensions(int Width, int Height, int Stride = 0, int BufferHeight = 0);

        private bool NeedsDecoderReinitialization(int width, int height) =>
            _decoder == null || _width != width || _height != height;

        private bool HasValidBuffers =>
            _decoder != null && _decodedBgraBuffer != null && _nv12Buffer != null;

        private bool EnsureDecoderInitialized(int width, int height)
        {
            if (NeedsDecoderReinitialization(width, height))
            {
                try
                {
                    InitializeDecoder(width, height);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[H264Decoder] Error inicializando decodificador: {ex.Message}");
                    return false;
                }
            }

            return HasValidBuffers;
        }

        private void FeedInputSample(byte[] encodedData, int width, int height)
        {
            using IMFMediaBuffer inputBuffer = MediaFactory.MFCreateMemoryBuffer(encodedData.Length);
            inputBuffer.Lock(out IntPtr pInput, out _, out _);
            try
            {
                Marshal.Copy(encodedData, 0, pInput, encodedData.Length);
                inputBuffer.CurrentLength = encodedData.Length;
            }
            finally
            {
                inputBuffer.Unlock();
            }

            using IMFSample inputSample = MediaFactory.MFCreateSample();
            inputSample.AddBuffer(inputBuffer);
            _sampleTimeCounter += 166_667; // ~16.6ms a 60fps en unidades de 100ns
            inputSample.SampleTime = _sampleTimeCounter;
            inputSample.SampleDuration = 166_667;

            try
            {
                _decoder!.ProcessInput(0, inputSample, 0);
            }
            catch (Exception ex)
            {
                // 0xC00D36B5 = MF_E_NOTACCEPTING (el decodificador tiene muestras pendientes sin drenar)
                if (ex.HResult == unchecked((int)0xC00D36B5) || ex.Message.Contains("0xC00D36B5"))
                {
                    DrainPendingOutput(width, height);
                    try
                    {
                        _decoder!.ProcessInput(0, inputSample, 0);
                    }
                    catch (Exception retryEx)
                    {
                        Console.WriteLine($"[H264Decoder] Reintento ProcessInput falló: {retryEx.Message}");
                    }
                }
                else
                {
                    Console.WriteLine($"[H264Decoder] Error en ProcessInput: {ex.Message}");
                }
            }
        }

        public bool TryDecodeFrame(byte[] encodedData, int width, int height, out DecodedVideoFrame decodedFrame)
        {
            decodedFrame = default;

            if (encodedData.Length == 0)
                return false;

            lock (_lock)
            {
                if (_disposed || !EnsureDecoderInitialized(width, height))
                    return false;

                FeedInputSample(encodedData, width, height);

                if (DrainPendingOutput(width, height))
                {
                    decodedFrame = new DecodedVideoFrame(_decodedBgraBuffer!, width, height);
                    return true;
                }

                return false;
            }
        }

        private SharpGen.Runtime.Result HandleStreamChange(
            ref OutputDataBuffer outBuffer,
            ref IMFSample? outputSample,
            ref IMFMediaBuffer? outputBuffer)
        {
            try
            {
                using IMFMediaType newOut = _decoder!.GetOutputAvailableType(0, 0);
                MediaFactory.MFGetAttributeSize(newOut, MediaTypeAttributeKeys.FrameSize, out uint outW, out uint outH);
                if (outW > 0) _actualStride = (int)outW;
                if (outH > 0) _actualBufferHeight = (int)outH;

                _decoder.SetOutputType(0, newOut, 0);

                var streamInfo = _decoder.GetOutputStreamInfo(0);
                uint flags = (uint)streamInfo.Flags;
                _mftProvidesSamples = (flags & 0x00000100) != 0 || (flags & 0x00000200) != 0;
                _outputBufferSize = streamInfo.Size > 0 ? streamInfo.Size : (_actualStride * _actualBufferHeight * 3 / 2);

                if (!_mftProvidesSamples)
                {
                    outputBuffer?.Dispose();
                    outputSample?.Dispose();

                    outputSample = MediaFactory.MFCreateSample();
                    outputBuffer = MediaFactory.MFCreateMemoryBuffer(_outputBufferSize);
                    outputSample.AddBuffer(outputBuffer);
                    outBuffer.Sample = outputSample;
                }

                return _decoder.ProcessOutput(ProcessOutputFlags.None, 1, ref outBuffer, out _);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[H264Decoder] Error en reconfiguración de salida: {ex.Message}");
                return unchecked((int)0xC00D36B3);
            }
        }

        private bool TryConvertSampleToBgra(IMFSample resolvedSample, int width, int height)
        {
            IMFMediaBuffer? sampleBuffer = null;
            try
            {
                sampleBuffer = resolvedSample.ConvertToContiguousBuffer();
                sampleBuffer.Lock(out IntPtr pOutput, out _, out int curLen);
                try
                {
                    int stride = _actualStride > 0 ? _actualStride : ((width + 15) & ~15);
                    int bufH = _actualBufferHeight > 0 ? _actualBufferHeight : ((height + 15) & ~15);
                    int neededSize = Math.Max(curLen, stride * bufH * 3 / 2);

                    if (_nv12Buffer == null || _nv12Buffer.Length < neededSize)
                    {
                        _nv12Buffer = new byte[neededSize];
                    }

                    int copyLen = Math.Min(curLen, _nv12Buffer.Length);
                    Marshal.Copy(pOutput, _nv12Buffer, 0, copyLen);
                    sampleBuffer.Unlock();

                    Nv12ToBgra(_nv12Buffer, _decodedBgraBuffer!, new VideoFrameDimensions(width, height, stride, bufH));
                    return true;
                }
                catch
                {
                    sampleBuffer.Unlock();
                    return false;
                }
            }
            finally
            {
                sampleBuffer?.Dispose();
            }
        }

        private OutputDataBuffer CreateOutputDataBuffer(out IMFSample? outputSample, out IMFMediaBuffer? outputBuffer)
        {
            outputSample = null;
            outputBuffer = null;

            if (!_mftProvidesSamples)
            {
                outputSample = MediaFactory.MFCreateSample();
                outputBuffer = MediaFactory.MFCreateMemoryBuffer(_outputBufferSize);
                outputSample.AddBuffer(outputBuffer);
            }

            return new OutputDataBuffer
            {
                StreamID = 0,
                Sample = outputSample,
                Status = 0
            };
        }

        private void CleanupOutputBuffers(in OutputDataBuffer outBuffer, IMFSample? outputSample, IMFMediaBuffer? outputBuffer)
        {
            if (_mftProvidesSamples && outBuffer.Sample != null)
            {
                outBuffer.Sample.Dispose();
            }
            if (!_mftProvidesSamples)
            {
                outputBuffer?.Dispose();
                outputSample?.Dispose();
            }
        }

        private bool TryProcessSingleOutput(int width, int height)
        {
            var outBuffer = CreateOutputDataBuffer(out IMFSample? outputSample, out IMFMediaBuffer? outputBuffer);

            try
            {
                var result = _decoder!.ProcessOutput(ProcessOutputFlags.None, 1, ref outBuffer, out _);

                if (result.Code == unchecked((int)0xC00D6D61))
                {
                    result = HandleStreamChange(ref outBuffer, ref outputSample, ref outputBuffer);
                }

                if (result.Code == unchecked((int)0xC00D36B3) || !result.Success)
                {
                    return false;
                }

                IMFSample? resolvedSample = _mftProvidesSamples ? outBuffer.Sample : outputSample;
                return resolvedSample != null && TryConvertSampleToBgra(resolvedSample, width, height);
            }
            finally
            {
                CleanupOutputBuffers(outBuffer, outputSample, outputBuffer);
            }
        }

        private bool DrainPendingOutput(int width, int height)
        {
            if (_decoder == null || _decodedBgraBuffer == null)
                return false;

            bool frameDecoded = false;
            for (int drainAttempt = 0; drainAttempt < 4; drainAttempt++)
            {
                if (!TryProcessSingleOutput(width, height))
                    break;

                frameDecoded = true;
            }

            return frameDecoded;
        }

        public static void Nv12ToBgra(byte[] nv12, byte[] bgra, in VideoFrameDimensions dims)
        {
            int width = dims.Width;
            int height = dims.Height;
            int stride = dims.Stride > 0 ? dims.Stride : ((width + 15) & ~15);
            int bufferHeight = dims.BufferHeight > 0 ? dims.BufferHeight : ((height + 15) & ~15);

            int yPlaneSize = stride * bufferHeight;

            Parallel.For(0, height, y =>
            {
                int yLineOffset = y * stride;
                int uvLineOffset = yPlaneSize + (y >> 1) * stride;
                int bgraLineOffset = y * width * 4;

                for (int x = 0; x < width; x++)
                {
                    int yVal = nv12[yLineOffset + x];
                    int uvIndex = uvLineOffset + (x & ~1);
                    int uVal = nv12[uvIndex];
                    int vVal = nv12[uvIndex + 1];

                    int c = yVal - 16;
                    int d = uVal - 128;
                    int e = vVal - 128;

                    int r = Math.Clamp((298 * c + 409 * e + 128) >> 8, 0, 255);
                    int g = Math.Clamp((298 * c - 100 * d - 208 * e + 128) >> 8, 0, 255);
                    int b = Math.Clamp((298 * c + 516 * d + 128) >> 8, 0, 255);

                    int pixelOffset = bgraLineOffset + (x * 4);
                    bgra[pixelOffset] = (byte)b;
                    bgra[pixelOffset + 1] = (byte)g;
                    bgra[pixelOffset + 2] = (byte)r;
                    bgra[pixelOffset + 3] = 255;
                }
            });
        }

        public static void Nv12ToBgra(byte[] nv12, byte[] bgra, int width, int height) =>
            Nv12ToBgra(nv12, bgra, new VideoFrameDimensions(width, height));

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                    return;

                _disposed = true;
                _decoder?.Dispose();
                _decoder = null;

                try { MediaFactory.MFShutdown(); } catch { }
                GC.SuppressFinalize(this);
            }
        }
    }
}
