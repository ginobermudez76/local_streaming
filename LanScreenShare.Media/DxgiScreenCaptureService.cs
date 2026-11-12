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
        private volatile int _monitorIndex = 0;
        private volatile bool _forceKeyframeRequested = true;
        private byte[]? _reusableBgraBytes;
        private MediaFoundationH264EncoderService? _h264Encoder;

        public void RequestKeyframe()
        {
            _forceKeyframeRequested = true;
        }

        private volatile string? _deviceName;
        private volatile int _targetX = 0;
        private volatile int _targetY = 0;
        private volatile int _targetSourceWidth = 0;
        private volatile int _targetSourceHeight = 0;

        public event EventHandler<CapturedFrame>? FrameCaptured;
        public event EventHandler<Exception>? CaptureFailed;
        public bool IsCapturing => _cts != null && !_cts.IsCancellationRequested;
        public string? ActiveEncoderDescription => _codec == "h264" ? _h264Encoder?.EncoderDescription : "JPEG";
        public bool IsHardwareAccelerated => _codec == "h264" && (_h264Encoder?.IsHardwareAccelerated ?? false);

        public int MonitorIndex
        {
            get => _monitorIndex;
            set => _monitorIndex = Math.Max(0, value);
        }

        public string? DeviceName
        {
            get => _deviceName;
            set => _deviceName = value;
        }

        public int TargetX
        {
            get => _targetX;
            set => _targetX = value;
        }

        public int TargetY
        {
            get => _targetY;
            set => _targetY = value;
        }

        public int TargetSourceWidth
        {
            get => _targetSourceWidth;
            set => _targetSourceWidth = Math.Max(0, value);
        }

        public int TargetSourceHeight
        {
            get => _targetSourceHeight;
            set => _targetSourceHeight = Math.Max(0, value);
        }

        public int TargetFps
        {
            get => _targetFps;
            set
            {
                _targetFps = Math.Clamp(value, 1, 120);
                _h264Encoder?.UpdateFps(_targetFps);
            }
        }

        public int Quality
        {
            get => _quality;
            set => _quality = Math.Clamp(value, 10, 100);
        }

        private volatile int _bitrateMbps = 8;
        public int BitrateMbps
        {
            get => _bitrateMbps;
            set
            {
                _bitrateMbps = Math.Clamp(value, 1, 50);
                _h264Encoder?.UpdateBitrate(_bitrateMbps * 1_000_000);
            }
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

        public void UpdateSettings(int targetFps, int quality, double resolutionScale = 1.0, string codec = "h264", int targetWidth = 0, int targetHeight = 0, int bitrateMbps = 8)
        {
            TargetFps = targetFps;
            Quality = quality;
            BitrateMbps = bitrateMbps;
            ResolutionScale = resolutionScale;
            Codec = codec;
            TargetWidth = targetWidth;
            TargetHeight = targetHeight;
        }

        public void StartCapture(int targetFps = 60, int quality = 75, double resolutionScale = 1.0, string codec = "h264", int targetWidth = 0, int targetHeight = 0, int bitrateMbps = 8)
        {
            if (IsCapturing)
                return;

            _targetFps = Math.Clamp(targetFps, 1, 120);
            _quality = Math.Clamp(quality, 10, 100);
            _bitrateMbps = Math.Clamp(bitrateMbps, 1, 50);
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
                // Ignorar cancelaciÃ³n
            }
            _cts?.Dispose();
            _cts = null;
            _captureTask = null;
        }

        private void CaptureLoop(CancellationToken token)
        {
            IntPtr mmcssHandle = IntPtr.Zero;
            int mmcssTaskIndex = 0;
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
                try
                {
                    Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DXGI] No se pudo elevar prioridad del proceso: {ex.Message}");
                }

                mmcssHandle = AvSetMmThreadCharacteristics("DisplayPostProcessing", ref mmcssTaskIndex);
                if (mmcssHandle != IntPtr.Zero)
                {
                    Console.WriteLine("[DXGI] Hilo de captura registrado exitosamente en MMCSS (DisplayPostProcessing)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DXGI] Advertencia configurando MMCSS/Prioridad de hilo: {ex.Message}");
            }

            var stopwatch = new Stopwatch();
            var fpsStopwatch = Stopwatch.StartNew();
            int capturedFramesCount = 0;
            int currentHostFps = 0;

            try
            {
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
                        FindTargetAdapterAndOutput(factory, _deviceName, _monitorIndex, out adapter, out output);
                        CreateD3D11Device(adapter, out device, out context);
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
                        bool hasCapturedAtLeastOneFrame = false;
                        int lastCursorX = int.MinValue;
                        int lastCursorY = int.MinValue;
                        var keepAliveStopwatch = Stopwatch.StartNew();

                        while (!token.IsCancellationRequested)
                        {
                            stopwatch.Restart();

                            int currentFps = _targetFps;
                            int frameIntervalMs = Math.Max(5, 1000 / Math.Max(1, currentFps));

                            // Medir duración de captura y codificación (frame time)
                            var frameProcStopwatch = Stopwatch.StartNew();

                            // Adquirir el siguiente frame de la GPU
                            bool hasNewFrame = false;
                            var acquireResult = duplication.AcquireNextFrame((uint)Math.Max(5, Math.Min(10, frameIntervalMs / 2)), out _, out IDXGIResource? desktopResource);

                            if (acquireResult.Success && desktopResource != null)
                            {
                                try
                                {
                                    using var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>();
                                    context.CopyResource(stagingTexture, desktopTexture);
                                    hasNewFrame = true;
                                    hasCapturedAtLeastOneFrame = true;
                                }
                                finally
                                {
                                    duplication.ReleaseFrame();
                                    desktopResource.Dispose();
                                }
                            }
                            else if (acquireResult.Code == Vortice.DXGI.ResultCode.AccessLost.Code ||
                                     acquireResult.Code == Vortice.DXGI.ResultCode.AccessDenied.Code)
                            {
                                Console.WriteLine("[DXGI] Acceso perdido o cambio de escritorio. Reinicializando...");
                                break; // Romper bucle interno para reinicializar
                            }

                            // Detectar movimiento del cursor para respuesta inmediata a 60 FPS incluso si el fondo es estático
                            bool cursorMoved = false;
                            try
                            {
                                CURSORINFO curInfo = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
                                if (GetCursorInfo(out curInfo) && curInfo.flags == CURSOR_SHOWING)
                                {
                                    if (curInfo.ptScreenPos.x != lastCursorX || curInfo.ptScreenPos.y != lastCursorY)
                                    {
                                        lastCursorX = curInfo.ptScreenPos.x;
                                        lastCursorY = curInfo.ptScreenPos.y;
                                        cursorMoved = true;
                                    }
                                }
                            }
                            catch { }

                            // Codificar inmediatamente si hay cambio en pantalla, movimiento de mouse, solicitud de keyframe,
                            // o como latido ligero cada 200ms (5 FPS en reposo) sin saturar la cola de red
                            bool shouldEncode = hasNewFrame ||
                                                (hasCapturedAtLeastOneFrame && (cursorMoved || _forceKeyframeRequested || (_codec == "h264" && keepAliveStopwatch.ElapsedMilliseconds >= 200)));

                            if (shouldEncode)
                            {
                                keepAliveStopwatch.Restart();
                                bool forceKeyframe = _forceKeyframeRequested;
                                _forceKeyframeRequested = false;

                                // Leer pixels mapeados desde CPU
                                var mapped = context.Map(stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                                try
                                {
                                    var frame = EncodeFrameFromPointer(
                                        mapped.DataPointer,
                                        screenWidth,
                                        screenHeight,
                                        (int)mapped.RowPitch,
                                        new FrameEncodingOptions(
                                            _quality,
                                            ResolutionScale,
                                            screenLeft,
                                            screenTop,
                                            _targetWidth,
                                            _targetHeight,
                                            forceKeyframe));

                                    if (frame != null)
                                    {
                                        frameProcStopwatch.Stop();
                                        int frameTimeMs = (int)frameProcStopwatch.ElapsedMilliseconds;

                                        capturedFramesCount++;
                                        if (fpsStopwatch.ElapsedMilliseconds >= 1000)
                                        {
                                            currentHostFps = (int)Math.Round(capturedFramesCount * 1000.0 / fpsStopwatch.ElapsedMilliseconds);
                                            capturedFramesCount = 0;
                                            fpsStopwatch.Restart();
                                        }

                                        frame.HostFps = currentHostFps > 0 ? currentHostFps : capturedFramesCount;
                                        frame.HostFrameTimeMs = frameTimeMs;

                                        FrameCaptured?.Invoke(this, frame);
                                    }
                                }
                                finally
                                {
                                    context.Unmap(stagingTexture, 0);
                                }
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
                        CaptureFailed?.Invoke(this, ex);
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
            finally
            {
                if (mmcssHandle != IntPtr.Zero)
                {
                    try
                    {
                        AvRevertMmThreadCharacteristics(mmcssHandle);
                    }
                    catch { }
                }
            }
        }

        private readonly record struct FrameEncodingOptions(
            int Quality,
            double Scale,
            int ScreenLeft,
            int ScreenTop,
            int CustomTargetWidth = 0,
            int CustomTargetHeight = 0,
            bool ForceKeyframe = false);

        private readonly record struct CursorDrawOptions(
            int ScreenLeft,
            int ScreenTop,
            float ScaleX = 1f,
            float ScaleY = 1f);

        private static bool NeedsResize(int targetW, int targetH, int width, int height) =>
            targetW > 0 && targetH > 0 && (targetW != width || targetH != height);

        private static bool IsInsideBitmap(float x, float y, int width, int height) =>
            x >= 0 && x < width && y >= 0 && y < height;

        private bool MatchesTargetSourceDimensions(int left, int top, int width, int height) =>
            _targetSourceWidth > 0 && _targetSourceHeight > 0 &&
            left == _targetX && top == _targetY &&
            width == _targetSourceWidth && height == _targetSourceHeight;

        private static bool IsDefaultAdapterAndOutput(IDXGIOutput? selected, uint aIdx, uint oIdx, int defaultIdx) =>
            selected == null && aIdx == 0 && oIdx == (uint)defaultIdx;

        private void FindTargetAdapterAndOutput(
            IDXGIFactory1 factory,
            string? targetDev,
            int matchOutputIdx,
            out IDXGIAdapter1 adapter,
            out IDXGIOutput output)
        {
            IDXGIAdapter1? selectedAdapter = null;
            IDXGIOutput? selectedOutput = null;

            for (uint aIdx = 0; factory.EnumAdapters1(aIdx, out var curAdapter).Success && curAdapter != null; aIdx++)
            {
                for (uint oIdx = 0; curAdapter.EnumOutputs(oIdx, out var curOutput).Success && curOutput != null; oIdx++)
                {
                    string devName = curOutput.Description.DeviceName.TrimEnd('\0');
                    var coords = curOutput.Description.DesktopCoordinates;
                    int oWidth = coords.Right - coords.Left;
                    int oHeight = coords.Bottom - coords.Top;

                    bool match = (!string.IsNullOrEmpty(targetDev) && string.Equals(devName, targetDev, StringComparison.OrdinalIgnoreCase)) ||
                                 MatchesTargetSourceDimensions(coords.Left, coords.Top, oWidth, oHeight) ||
                                 IsDefaultAdapterAndOutput(selectedOutput, aIdx, oIdx, matchOutputIdx);

                    if (match && selectedOutput == null)
                    {
                        selectedAdapter = curAdapter;
                        selectedOutput = curOutput;
                    }
                    else
                    {
                        curOutput.Dispose();
                    }
                }

                if (selectedOutput == null || selectedAdapter != curAdapter)
                {
                    curAdapter.Dispose();
                }
            }

            if (selectedAdapter == null || selectedOutput == null)
            {
                selectedOutput?.Dispose();
                selectedAdapter?.Dispose();

                if (factory.EnumAdapters1(0, out selectedAdapter).Failure || selectedAdapter == null)
                {
                    throw new InvalidOperationException("No se pudo obtener el adaptador gráfico primario (GPU 0)");
                }

                if (selectedAdapter.EnumOutputs(0, out selectedOutput).Failure || selectedOutput == null)
                {
                    throw new InvalidOperationException("No se pudo obtener la salida de pantalla principal (Monitor 0)");
                }
            }

            adapter = selectedAdapter;
            output = selectedOutput;
        }

        private static void CreateD3D11Device(
            IDXGIAdapter1 adapter,
            out ID3D11Device device,
            out ID3D11DeviceContext context)
        {
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
                out ID3D11Device? dev,
                out ID3D11DeviceContext? ctx);

            if (d3dResult.Failure || dev == null || ctx == null)
            {
                throw new InvalidOperationException($"No se pudo crear el dispositivo Direct3D 11: {d3dResult}");
            }

            device = dev;
            context = ctx;

            try
            {
                using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
                if (dxgiDevice != null)
                {
                    dxgiDevice.SetGPUThreadPriority(7);
                    Console.WriteLine("[DXGI] GPU Thread Priority establecido al nivel máximo (7)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DXGI] Advertencia configurando GPU Thread Priority: {ex.Message}");
            }
        }

        private CapturedFrame? EncodeFrameFromPointer(
            IntPtr pBits,
            int width,
            int height,
            int rowPitch,
            in FrameEncodingOptions options)
        {
            int finalWidth = width;
            int finalHeight = height;

            try
            {
                var imageInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var skBitmap = new SKBitmap();
                skBitmap.InstallPixels(imageInfo, pBits, rowPitch);

                SKBitmap bitmapToEncode = skBitmap;
                SKBitmap? resizedBitmap = null;

                int targetW = options.CustomTargetWidth > 0 ? options.CustomTargetWidth : (options.Scale > 0.2 && options.Scale < 0.99 ? (int)(width * options.Scale) : 0);
                int targetH = options.CustomTargetHeight > 0 ? options.CustomTargetHeight : (options.Scale > 0.2 && options.Scale < 0.99 ? (int)(height * options.Scale) : 0);

                if (NeedsResize(targetW, targetH, width, height))
                {
                    resizedBitmap = skBitmap.Resize(new SKImageInfo(targetW, targetH), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                    if (resizedBitmap != null)
                    {
                        bitmapToEncode = resizedBitmap;
                        finalWidth = targetW;
                        finalHeight = targetH;
                    }
                }
                else
                {
                    resizedBitmap = skBitmap.Copy();
                    if (resizedBitmap != null)
                    {
                        bitmapToEncode = resizedBitmap;
                    }
                }

                float scaleX = (float)finalWidth / Math.Max(1, width);
                float scaleY = (float)finalHeight / Math.Max(1, height);
                DrawCursorOnBitmap(bitmapToEncode, new CursorDrawOptions(options.ScreenLeft, options.ScreenTop, scaleX, scaleY));

                byte[]? encodedBytes = null;
                string currentCodec = _codec;

                try
                {
                    if (currentCodec == "h264")
                    {
                        encodedBytes = EncodeH264(bitmapToEncode, finalWidth, finalHeight, options.ForceKeyframe);
                    }
                    else
                    {
                        encodedBytes = EncodeJpeg(bitmapToEncode, options.Quality);
                    }
                }
                finally
                {
                    resizedBitmap?.Dispose();
                }

                if (encodedBytes == null || encodedBytes.Length == 0) return null;

                return new CapturedFrame
                {
                    Data = encodedBytes,
                    Width = finalWidth,
                    Height = finalHeight,
                    Format = currentCodec,
                    Timestamp = DateTime.UtcNow.Ticks
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DXGI] Error codificando fotograma: {ex.Message}");
                return null;
            }
        }

        private byte[]? EncodeH264(SKBitmap bitmap, int width, int height, bool forceKeyframe)
        {
            try
            {
                _h264Encoder ??= new MediaFoundationH264EncoderService(_targetFps, _bitrateMbps * 1_000_000);
                int requiredBytes = width * height * 4;
                if (_reusableBgraBytes == null || _reusableBgraBytes.Length != requiredBytes)
                {
                    _reusableBgraBytes = new byte[requiredBytes];
                }

                Marshal.Copy(bitmap.GetPixels(), _reusableBgraBytes, 0, requiredBytes);
                return _h264Encoder.EncodeFrame(_reusableBgraBytes, width, height, forceKeyframe);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DXGI] Error codificando H.264 por hardware: {ex.Message}");
                return null;
            }
        }

        private static byte[]? EncodeJpeg(SKBitmap bitmap, int quality)
        {
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image?.Encode(SKEncodedImageFormat.Jpeg, quality);
            return data?.ToArray();
        }

        private static void DrawCursorOnBitmap(SKBitmap bitmap, in CursorDrawOptions options)
        {
            try
            {
                CURSORINFO cursorInfo = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
                if (GetCursorInfo(out cursorInfo) && cursorInfo.flags == CURSOR_SHOWING)
                {
                    float x = (cursorInfo.ptScreenPos.x - options.ScreenLeft) * options.ScaleX;
                    float y = (cursorInfo.ptScreenPos.y - options.ScreenTop) * options.ScaleY;

                    if (IsInsideBitmap(x, y, bitmap.Width, bitmap.Height))
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

        [DllImport("avrt.dll", SetLastError = true)]
        private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref int taskIndex);

        [DllImport("avrt.dll", SetLastError = true)]
        private static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);

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
