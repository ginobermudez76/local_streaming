using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LanScreenShare.Core.Interfaces;
using Vortice.MediaFoundation;

namespace LanScreenShare.Media
{
    [SupportedOSPlatform("windows")]
    public class MediaFoundationH264DecoderService : IVideoDecoderService
    {
        private static readonly Guid CLSID_CMSH264DecoderMFT = new("62ce7e72-4c71-4d20-b15d-452831a87d9d");
        private static readonly Guid CODECAPI_AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a0ec04");
        private static readonly Guid IID_ICodecAPI = new("901d850e-31a8-4b7d-a8d9-06e129f30171");

        private IMFTransform? _decoder;
        private int _width;
        private int _height;
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
            _decodedBgraBuffer = new byte[width * height * 4];
            _nv12Buffer = new byte[width * height * 3 / 2];
            _sampleTimeCounter = 0;

            Type decoderType = Type.GetTypeFromCLSID(CLSID_CMSH264DecoderMFT)
                ?? throw new InvalidOperationException("No se encontró el decodificador H.264 de Windows Media Foundation.");

            object rawObj = Activator.CreateInstance(decoderType)
                ?? throw new InvalidOperationException("No se pudo crear la instancia del decodificador H.264.");

            IntPtr pUnk = Marshal.GetIUnknownForObject(rawObj);
            _decoder = new IMFTransform(pUnk);

            // Desbloquear MFT asíncrono y configurar baja latencia si está disponible
            try
            {
                _decoder.Attributes.Set(CODECAPI_AVLowLatencyMode, 1);
            }
            catch { }

            int hrCodecApi = Marshal.QueryInterface(_decoder.NativePointer, in IID_ICodecAPI, out IntPtr pCodecApi);
            if (hrCodecApi == 0 && pCodecApi != IntPtr.Zero)
            {
                try
                {
                    var codecApi = (ICodecAPI)Marshal.GetObjectForIUnknown(pCodecApi);
                    object lowLatency = 1;
                    codecApi.SetValue(CODECAPI_AVLowLatencyMode, ref lowLatency);
                    Console.WriteLine("[H264Decoder] CODECAPI_AVLowLatencyMode configurado en decodificador.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[H264Decoder] Error configurando ICodecAPI: {ex.Message}");
                }
                finally
                {
                    Marshal.Release(pCodecApi);
                }
            }

            // 1. Configurar Tipo de Entrada (H.264)
            IMFMediaType inputType = MediaFactory.MFCreateMediaType();
            inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            MediaFactory.MFSetAttributeSize(inputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
            MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.PixelAspectRatio, 1u, 1u);
            inputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);

            try
            {
                _decoder.SetInputType(0, inputType, 0);
            }
            finally
            {
                inputType.Dispose();
            }

            // 2. Configurar Tipo de Salida (NV12 nativo de alta eficiencia)
            IMFMediaType outputType = MediaFactory.MFCreateMediaType();
            outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
            MediaFactory.MFSetAttributeSize(outputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
            MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.PixelAspectRatio, 1u, 1u);
            outputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);

            try
            {
                _decoder.SetOutputType(0, outputType, 0);
            }
            finally
            {
                outputType.Dispose();
            }

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

            _decoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _decoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

            Console.WriteLine($"[H264Decoder] Decodificador H.264 inicializado ({width}x{height} -> NV12 -> BGRA, ProvidesSamples: {_mftProvidesSamples})");
        }

        public bool TryDecodeFrame(byte[] encodedData, int width, int height, out byte[] bgraPixels, out int decodedWidth, out int decodedHeight)
        {
            bgraPixels = Array.Empty<byte>();
            decodedWidth = width;
            decodedHeight = height;

            if (encodedData.Length == 0)
                return false;

            lock (_lock)
            {
                if (_disposed)
                    return false;

                if (_decoder == null || _width != width || _height != height)
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

                if (_decoder == null || _decodedBgraBuffer == null || _nv12Buffer == null)
                    return false;

                // 1. Enviar paquete NAL H.264 a decodificador
                IMFMediaBuffer inputBuffer = MediaFactory.MFCreateMemoryBuffer(encodedData.Length);
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

                IMFSample inputSample = MediaFactory.MFCreateSample();
                inputSample.AddBuffer(inputBuffer);
                _sampleTimeCounter += 166_667; // ~16.6ms a 60fps en unidades de 100ns
                inputSample.SampleTime = _sampleTimeCounter;
                inputSample.SampleDuration = 166_667;

                try
                {
                    _decoder.ProcessInput(0, inputSample, 0);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[H264Decoder] Error en ProcessInput: {ex.Message}");
                    return false;
                }
                finally
                {
                    inputBuffer.Dispose();
                    inputSample.Dispose();
                }

                // 2. Extraer fotograma decodificado NV12
                IMFSample? outputSample = null;
                IMFMediaBuffer? outputBuffer = null;

                if (!_mftProvidesSamples)
                {
                    outputSample = MediaFactory.MFCreateSample();
                    outputBuffer = MediaFactory.MFCreateMemoryBuffer(_outputBufferSize);
                    outputSample.AddBuffer(outputBuffer);
                }

                var outBuffer = new OutputDataBuffer
                {
                    StreamID = 0,
                    Sample = outputSample,
                    Status = 0
                };

                var result = _decoder.ProcessOutput(ProcessOutputFlags.None, 1, ref outBuffer, out _);

                // 0xC00D6D61 = MF_E_TRANSFORM_STREAM_CHANGE
                if (result.Code == unchecked((int)0xC00D6D61))
                {
                    try
                    {
                        IMFMediaType newOut = _decoder.GetOutputAvailableType(0, 0);
                        _decoder.SetOutputType(0, newOut, 0);
                        newOut.Dispose();

                        var streamInfo = _decoder.GetOutputStreamInfo(0);
                        uint flags = (uint)streamInfo.Flags;
                        _mftProvidesSamples = (flags & 0x00000100) != 0 || (flags & 0x00000200) != 0;
                        _outputBufferSize = streamInfo.Size > 0 ? streamInfo.Size : (width * height * 3 / 2);

                        if (!_mftProvidesSamples)
                        {
                            outputBuffer?.Dispose();
                            outputSample?.Dispose();

                            outputSample = MediaFactory.MFCreateSample();
                            outputBuffer = MediaFactory.MFCreateMemoryBuffer(_outputBufferSize);
                            outputSample.AddBuffer(outputBuffer);
                            outBuffer.Sample = outputSample;
                        }

                        result = _decoder.ProcessOutput(ProcessOutputFlags.None, 1, ref outBuffer, out _);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[H264Decoder] Error en reconfiguración de salida: {ex.Message}");
                    }
                }

                IMFSample? resolvedSample = _mftProvidesSamples ? outBuffer.Sample : outputSample;

                if (result.Success && resolvedSample != null)
                {
                    IMFMediaBuffer? sampleBuffer = null;
                    try
                    {
                        sampleBuffer = resolvedSample.ConvertToContiguousBuffer();
                        sampleBuffer.Lock(out IntPtr pOutput, out _, out int curLen);
                        try
                        {
                            int copyLen = Math.Min(curLen, _nv12Buffer.Length);
                            Marshal.Copy(pOutput, _nv12Buffer, 0, copyLen);
                            sampleBuffer.Unlock();

                            // 3. Conversión paralela de alto rendimiento NV12 -> BGRA
                            Nv12ToBgra(_nv12Buffer, _decodedBgraBuffer, width, height);
                            bgraPixels = _decodedBgraBuffer;
                            return true;
                        }
                        catch
                        {
                            sampleBuffer.Unlock();
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[H264Decoder] Error extrayendo buffer NV12: {ex.Message}");
                    }
                    finally
                    {
                        sampleBuffer?.Dispose();
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
                }
                else
                {
                    if (!_mftProvidesSamples)
                    {
                        outputBuffer?.Dispose();
                        outputSample?.Dispose();
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// Convierte fotogramas NV12 (YUV 4:2:0 biplanar) a BGRA32 usando aritmética fija optimizada y paralelismo por filas.
        /// </summary>
        public static void Nv12ToBgra(byte[] nv12, byte[] bgra, int width, int height)
        {
            int frameSize = width * height;

            Parallel.For(0, height, y =>
            {
                int yLineOffset = y * width;
                int uvLineOffset = frameSize + (y >> 1) * width;
                int bgraLineOffset = yLineOffset * 4;

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
