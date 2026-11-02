using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;
using SkiaSharp;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LanScreenShare.Media
{
    [SupportedOSPlatform("windows")]
    public class DxgiScreenCaptureService : IScreenCaptureService
    {
        private CancellationTokenSource? _cts;
        private Task? _captureTask;
        private bool _disposed;

        private volatile int _targetFps = 60;
        private volatile int _quality = 75;
        private volatile int _scalePercent = 100;
        private volatile int _targetWidth = 0;
        private volatile int _targetHeight = 0;
        private volatile string _codec = "h264";
        private MediaFoundationH264EncoderService? _h264Encoder;

        public event EventHandler<CapturedFrame>? FrameCaptured;
        public bool IsCapturing => _cts != null && !_cts.IsCancellationRequested;

        public int TargetFps
        {
            get => _targetFps;
            set => _targetFps = Math.Clamp(value, 1, 120);
        }

        public int Quality
        {
            get => _quality;
            set => _quality = Math.Clamp(value, 10, 100);
        }

        public double ResolutionScale
        {
            get => _scalePercent / 100.0;
            set => _scalePercent = (int)Math.Round(Math.Clamp(value, 0.25, 1.0) * 100);
        }

        public int TargetWidth
        {
            get => _targetWidth;
            set => _targetWidth = Math.Max(0, value);
        }

        public int TargetHeight
        {
            get => _targetHeight;
            set => _targetHeight = Math.Max(0, value);
        }

        public string Codec
        {
            get => _codec;
            set => _codec = value.ToLowerInvariant() == "h264" ? "h264" : "jpeg";
        }

        public void UpdateSettings(int targetFps, int quality, double resolutionScale = 1.0, string codec = "h264", int targetWidth = 0, int targetHeight = 0)
        {
            TargetFps = targetFps;
            Quality = quality;
            ResolutionScale = resolutionScale;
            Codec = codec;
            TargetWidth = targetWidth;
            TargetHeight = targetHeight;
        }

        public void StartCapture(int targetFps = 60, int quality = 75, double resolutionScale = 1.0, string codec = "h264", int targetWidth = 0, int targetHeight = 0)
        {
            if (IsCapturing)
                return;

            _targetFps = Math.Clamp(targetFps, 1, 120);
            _quality = Math.Clamp(quality, 10, 100);
            ResolutionScale = resolutionScale;
            Codec = codec;
            TargetWidth = targetWidth;
            TargetHeight = targetHeight;

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _captureTask = Task.Run(() => CaptureLoop(token), token);
        }

        public void StopCapture()
        {
            _cts?.Cancel();
            try
            {
                _captureTask?.Wait(1000);
            }
            catch
            {
                // Ignorar cancelación
            }
            _cts?.Dispose();
            _cts = null;
            _captureTask = null;
        }

        private void CaptureLoop(CancellationToken token)
        {
            var stopwatch = new Stopwatch();

            while (!token.IsCancellationRequested)
            {
                IDXGIFactory1? factory = null;
                IDXGIAdapter1? adapter = null;
                IDXGIOutput? output = null;
                IDXGIOutput1? output1 = null;
                ID3D11Device? device = null;
                ID3D11DeviceContext? context = null;
                IDXGIOutputDuplication? duplication = null;
                ID3D11Texture2D? stagingTexture = null;

                try
                {
                    if (DXGI.CreateDXGIFactory1(out factory).Failure || factory == null)
                    {
                        throw new InvalidOperationException("No se pudo crear IDXGIFactory1");
                    }

                    if (factory.EnumAdapters1(0, out adapter).Failure || adapter == null)
                    {
                        throw new InvalidOperationException("No se pudo obtener el adaptador gráfico primario (GPU 0)");
                    }

                    var featureLevels = new[]
                    {
                        FeatureLevel.Level_11_1,
                        FeatureLevel.Level_11_0,
                        FeatureLevel.Level_10_1,
                        FeatureLevel.Level_10_0
                    };

                    var d3dResult = D3D11.D3D11CreateDevice(
                        adapter,
                        DriverType.Unknown,
                        DeviceCreationFlags.BgraSupport,
                        featureLevels,
                        out device,
                        out context);

                    if (d3dResult.Failure || device == null || context == null)
                    {
                        throw new InvalidOperationException($"No se pudo crear el dispositivo Direct3D 11: {d3dResult}");
                    }

                    if (adapter.EnumOutputs(0, out output).Failure || output == null)
                    {
                        throw new InvalidOperationException("No se pudo obtener la salida de pantalla principal (Monitor 0)");
                    }

                    output1 = output.QueryInterface<IDXGIOutput1>();

                    var bounds = output.Description.DesktopCoordinates;
                    int screenWidth = bounds.Right - bounds.Left;
                    int screenHeight = bounds.Bottom - bounds.Top;
                    int screenLeft = bounds.Left;
                    int screenTop = bounds.Top;

                    duplication = output1.DuplicateOutput(device);

                    var stagingDesc = new Texture2DDescription
                    {
                        Width = (uint)screenWidth,
                        Height = (uint)screenHeight,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Format.B8G8R8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Staging,
                        BindFlags = BindFlags.None,
                        CPUAccessFlags = CpuAccessFlags.Read,
                        MiscFlags = ResourceOptionFlags.None
                    };

                    stagingTexture = device.CreateTexture2D(stagingDesc);

                    while (!token.IsCancellationRequested)
                    {
                        stopwatch.Restart();

                        int currentFps = _targetFps;
                        int frameIntervalMs = Math.Max(5, 1000 / Math.Max(1, currentFps));

                        // Adquirir el siguiente frame de la GPU
                        var acquireResult = duplication.AcquireNextFrame((uint)Math.Max(10, frameIntervalMs), out _, out IDXGIResource? desktopResource);

                        if (acquireResult.Success && desktopResource != null)
                        {
                            try
                            {
                                using var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>();
                                context.CopyResource(stagingTexture, desktopTexture);
                            }
                            finally
                            {
                                duplication.ReleaseFrame();
                                desktopResource.Dispose();
                            }

                            // Leer pixels mapeados desde CPU
                            var mapped = context.Map(stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                            try
                            {
                                var frame = EncodeFrameFromPointer(
                                    mapped.DataPointer,
                                    screenWidth,
                                    screenHeight,
                                    (int)mapped.RowPitch,
                                    _quality,
                                    ResolutionScale,
                                    screenLeft,
                                    screenTop,
                                    _targetWidth,
                                    _targetHeight);

                                if (frame != null)
                                {
                                    FrameCaptured?.Invoke(this, frame);
                                }
                            }
                            finally
                            {
                                context.Unmap(stagingTexture, 0);
                            }
                        }
                        else if (acquireResult.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code)
                        {
                            // La pantalla no cambió en este intervalo; normal en DXGI.
                        }
                        else if (acquireResult.Code == Vortice.DXGI.ResultCode.AccessLost.Code ||
                                 acquireResult.Code == Vortice.DXGI.ResultCode.AccessDenied.Code)
                        {
                            Console.WriteLine("[DXGI] Acceso perdido o cambio de escritorio. Reinicializando...");
                            break; // Romper bucle interno para reinicializar
                        }

                        int elapsed = (int)stopwatch.ElapsedMilliseconds;
                        int delay = frameIntervalMs - elapsed;
                        if (delay > 0)
                        {
                            Thread.Sleep(delay);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DXGI Capture] Error en bucle DXGI: {ex.Message}");
                    Thread.Sleep(500); // Pausa antes de reintentar
                }
                finally
                {
                    stagingTexture?.Dispose();
                    duplication?.Dispose();
                    output1?.Dispose();
                    output?.Dispose();
                    context?.Dispose();
                    device?.Dispose();
                    adapter?.Dispose();
                    factory?.Dispose();
                }
            }
        }

        private CapturedFrame? EncodeFrameFromPointer(
            IntPtr pBits,
            int width,
            int height,
            int rowPitch,
            int quality,
            double scale,
            int screenLeft,
            int screenTop,
            int customTargetW = 0,
            int customTargetH = 0)
        {
            int finalWidth = width;
            int finalHeight = height;

            try
            {
                var imageInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var skBitmap = new SKBitmap();
                skBitmap.InstallPixels(imageInfo, pBits, rowPitch);

                // Dibujar cursor del mouse sobre el fotograma antes de codificar
                DrawCursorOnBitmap(skBitmap, screenLeft, screenTop);

                SKBitmap bitmapToEncode = skBitmap;
                SKBitmap? resizedBitmap = null;

                int targetW = customTargetW > 0 ? customTargetW : (scale > 0.2 && scale < 0.99 ? (int)(width * scale) : 0);
                int targetH = customTargetH > 0 ? customTargetH : (scale > 0.2 && scale < 0.99 ? (int)(height * scale) : 0);

                if (targetW > 0 && targetH > 0 && (targetW != width || targetH != height))
                {
                    // Remuestreo cúbico CatmullRom para máxima nitidez en textos y detalles finos sin pixelación
                    resizedBitmap = skBitmap.Resize(new SKImageInfo(targetW, targetH), new SKSamplingOptions(SKCubicResampler.CatmullRom));
                    if (resizedBitmap != null)
                    {
                        bitmapToEncode = resizedBitmap;
                        finalWidth = targetW;
                        finalHeight = targetH;
                    }
                }

                byte[]? encodedBytes = null;
                string currentCodec = _codec;

                try
                {
                    if (currentCodec == "h264")
                    {
                        try
                        {
                            _h264Encoder ??= new MediaFoundationH264EncoderService(_targetFps);
                            byte[] bgraBytes = new byte[finalWidth * finalHeight * 4];
                            Marshal.Copy(bitmapToEncode.GetPixels(), bgraBytes, 0, bgraBytes.Length);
                            encodedBytes = _h264Encoder.EncodeFrame(bgraBytes, finalWidth, finalHeight);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[DXGI] H.264 error, fallback automático a JPEG: {ex.Message}");
                            encodedBytes = null;
                        }
                    }

                    if (encodedBytes == null || encodedBytes.Length == 0)
                    {
                        currentCodec = "jpeg";
                        using var image = SKImage.FromBitmap(bitmapToEncode);
                        if (image != null)
                        {
                            using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
                            encodedBytes = data.ToArray();
                        }
                    }
                }
                finally
                {
                    resizedBitmap?.Dispose();
                }

                if (encodedBytes == null || encodedBytes.Length == 0)
                    return null;

                return new CapturedFrame
                {
                    Data = encodedBytes,
                    Width = finalWidth,
                    Height = finalHeight,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Format = currentCodec
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DXGI] Error codificando fotograma: {ex.Message}");
                return null;
            }
        }

        private static void DrawCursorOnBitmap(SKBitmap bitmap, int screenLeft, int screenTop)
        {
            try
            {
                CURSORINFO cursorInfo = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
                if (GetCursorInfo(out cursorInfo) && cursorInfo.flags == CURSOR_SHOWING)
                {
                    float x = cursorInfo.ptScreenPos.x - screenLeft;
                    float y = cursorInfo.ptScreenPos.y - screenTop;

                    if (x >= 0 && x < bitmap.Width && y >= 0 && y < bitmap.Height)
                    {
                        using var canvas = new SKCanvas(bitmap);
                        using var fillPaint = new SKPaint
                        {
                            Color = SKColors.White,
                            Style = SKPaintStyle.Fill,
                            IsAntialias = true
                        };
                        using var strokePaint = new SKPaint
                        {
                            Color = SKColors.Black,
                            Style = SKPaintStyle.Stroke,
                            StrokeWidth = 1.5f,
                            IsAntialias = true
                        };

                        using var builder = new SKPathBuilder();
                        builder.MoveTo(x, y);
                        builder.LineTo(x, y + 17);
                        builder.LineTo(x + 4.5f, y + 13);
                        builder.LineTo(x + 8.5f, y + 21);
                        builder.LineTo(x + 11.5f, y + 19.5f);
                        builder.LineTo(x + 7.5f, y + 11.5f);
                        builder.LineTo(x + 13, y + 11.5f);
                        builder.Close();

                        using var path = builder.Detach();
                        canvas.DrawPath(path, fillPaint);
                        canvas.DrawPath(path, strokePaint);
                    }
                }
            }
            catch
            {
                // Ignorar error de cursor
            }
        }

        #region Win32 Cursor Structs & Imports

        private const int CURSOR_SHOWING = 0x00000001;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetCursorInfo(out CURSORINFO pci);

        #endregion

        public void Dispose()
        {
            if (_disposed)
                return;

            StopCapture();
            _h264Encoder?.Dispose();
            _h264Encoder = null;
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
