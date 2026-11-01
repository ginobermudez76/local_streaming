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

        private IMFTransform? _decoder;
        private int _width;
        private int _height;
        private byte[]? _decodedBuffer;
        private readonly object _lock = new();
        private bool _disposed;

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
            _decodedBuffer = new byte[width * height * 4];

            Type decoderType = Type.GetTypeFromCLSID(CLSID_CMSH264DecoderMFT)
                ?? throw new InvalidOperationException("No se encontró el decodificador H.264 de Windows Media Foundation.");

            _decoder = (IMFTransform)Activator.CreateInstance(decoderType)!;

            // 1. Configurar Tipo de Entrada (H.264)
            IMFMediaType inputType = MediaFactory.MFCreateMediaType();
            inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            MediaFactory.MFSetAttributeSize(inputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
            MediaFactory.MFSetAttributeRatio(inputType, MediaTypeAttributeKeys.PixelAspectRatio, 1u, 1u);
            inputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);

            _decoder.SetInputType(0, inputType, 0);
            inputType.Dispose();

            // 2. Configurar Tipo de Salida (RGB32 / BGRA)
            IMFMediaType outputType = MediaFactory.MFCreateMediaType();
            outputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            outputType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
            MediaFactory.MFSetAttributeSize(outputType, MediaTypeAttributeKeys.FrameSize, (uint)width, (uint)height);
            MediaFactory.MFSetAttributeRatio(outputType, MediaTypeAttributeKeys.PixelAspectRatio, 1u, 1u);
            outputType.Set(MediaTypeAttributeKeys.InterlaceMode, (int)VideoInterlaceMode.Progressive);

            _decoder.SetOutputType(0, outputType, 0);
            outputType.Dispose();

            _decoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _decoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

            Console.WriteLine($"[H264Decoder] Decodificador H.264 inicializado ({width}x{height} -> RGB32)");
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

                if (_decoder == null || _decodedBuffer == null)
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

                _decoder.ProcessInput(0, inputSample, 0);
                inputBuffer.Dispose();
                inputSample.Dispose();

                // 2. Extraer fotograma decodificado RGB32
                IMFSample outputSample = MediaFactory.MFCreateSample();
                IMFMediaBuffer outputBuffer = MediaFactory.MFCreateMemoryBuffer(_decodedBuffer.Length);
                outputSample.AddBuffer(outputBuffer);

                var outBuffer = new OutputDataBuffer
                {
                    StreamID = 0,
                    Sample = outputSample,
                    Status = 0
                };

                var result = _decoder.ProcessOutput(ProcessOutputFlags.None, 1, ref outBuffer, out _);
                if (result.Success && outputBuffer.CurrentLength > 0)
                {
                    outputBuffer.Lock(out IntPtr pOutput, out _, out _);
                    try
                    {
                        int copyLen = Math.Min(outputBuffer.CurrentLength, _decodedBuffer.Length);
                        Marshal.Copy(pOutput, _decodedBuffer, 0, copyLen);
                        bgraPixels = _decodedBuffer;
                        outputBuffer.Unlock();

                        outputBuffer.Dispose();
                        outputSample.Dispose();
                        return true;
                    }
                    catch
                    {
                        outputBuffer.Unlock();
                    }
                }

                outputBuffer.Dispose();
                outputSample.Dispose();
                return false;
            }
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
