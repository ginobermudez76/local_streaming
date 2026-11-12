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

        public void UpdateSettings(CaptureProfile profile)
        {
            TargetFps = profile.TargetFps;
            Quality = profile.Quality;
            BitrateMbps = profile.BitrateMbps;
            ResolutionScale = profile.ResolutionScale;
            Codec = profile.Codec;
            TargetWidth = profile.TargetWidth;
            TargetHeight = profile.TargetHeight;
        }

        public void StartCapture(CaptureProfile profile)
        {
            if (IsCapturing)
                return;

            _targetFps = Math.Clamp(profile.TargetFps, 1, 120);
            _quality = Math.Clamp(profile.Quality, 10, 100);
            _bitrateMbps = Math.Clamp(profile.BitrateMbps, 1, 50);
            ResolutionScale = profile.ResolutionScale;
            Codec = profile.Codec;
            TargetWidth = profile.TargetWidth;
            TargetHeight = profile.TargetHeight;

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

        private static IntPtr ConfigureCaptureThreadPriority()
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
            return mmcssHandle;
        }

        private static void RevertMmcss(IntPtr mmcssHandle)
        {
            if (mmcssHandle == IntPtr.Zero) return;
            try
            {
                AvRevertMmThreadCharacteristics(mmcssHandle);
            }
            catch { }
        }

        private void CaptureLoop(CancellationToken token)
        {
            IntPtr mmcssHandle = ConfigureCaptureThreadPriority();
            var stopwatch = new Stopwatch();
            var fpsTracker = new HostFpsTracker();

            try
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var session = CreateDxgiSession();
                        RunSessionCaptureLoop(session, token, stopwatch, fpsTracker);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[DXGI Capture] Error en bucle DXGI: {ex.Message}");
                        CaptureFailed?.Invoke(this, ex);
                        Thread.Sleep(500);
                    }
                }
            }
            finally
            {
                RevertMmcss(mmcssHandle);
            }
        }

        private bool ShouldEncodeFrame(
            FrameAcquireStatus status,
            bool hasCapturedAtLeastOneFrame,
            bool cursorMoved,
            long elapsedKeepAliveMs)
        {
            if (status == FrameAcquireStatus.NewFrame) return true;
            if (!hasCapturedAtLeastOneFrame) return false;
            if (cursorMoved || _forceKeyframeRequested) return true;
            return _codec == "h264" && elapsedKeepAliveMs >= 200;
        }

        private void RunSessionCaptureLoop(
            DxgiSessionResources session,
            CancellationToken token,
            Stopwatch stopwatch,
            HostFpsTracker fpsTracker)
        {
            bool hasCapturedAtLeastOneFrame = false;
            int lastCursorX = int.MinValue;
            int lastCursorY = int.MinValue;
            var keepAliveStopwatch = Stopwatch.StartNew();

            while (!token.IsCancellationRequested)
            {
                stopwatch.Restart();

                int currentFps = _targetFps;
                int frameIntervalMs = Math.Max(5, 1000 / Math.Max(1, currentFps));
                var frameProcStopwatch = Stopwatch.StartNew();

                var status = TryAcquireAndCopyFrame(session, frameIntervalMs, ref hasCapturedAtLeastOneFrame);
                if (status == FrameAcquireStatus.AccessLost)
                {
                    Console.WriteLine("[DXGI] Acceso perdido o cambio de escritorio. Reinicializando...");
                    break;
                }

                bool cursorMoved = DetectCursorMovement(ref lastCursorX, ref lastCursorY);
                if (ShouldEncodeFrame(status, hasCapturedAtLeastOneFrame, cursorMoved, keepAliveStopwatch.ElapsedMilliseconds))
                {
                    keepAliveStopwatch.Restart();
                    ProcessAndDispatchFrame(session, frameProcStopwatch, fpsTracker);
                }

                ApplyFramePacing(stopwatch, frameIntervalMs);
            }
        }

        private enum FrameAcquireStatus
        {
            NoFrame,
            NewFrame,
            AccessLost
        }

        private static FrameAcquireStatus TryAcquireAndCopyFrame(
            DxgiSessionResources session,
            int frameIntervalMs,
            ref bool hasCapturedAtLeastOneFrame)
        {
            uint timeoutMs = (uint)Math.Max(5, Math.Min(10, frameIntervalMs / 2));
            var acquireResult = session.Duplication!.AcquireNextFrame(timeoutMs, out _, out IDXGIResource? desktopResource);

            if (acquireResult.Success && desktopResource != null)
            {
                try
                {
                    using var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>();
                    session.Context!.CopyResource(session.StagingTexture!, desktopTexture);
                    hasCapturedAtLeastOneFrame = true;
                    return FrameAcquireStatus.NewFrame;
                }
                finally
                {
                    session.Duplication.ReleaseFrame();
                    desktopResource.Dispose();
                }
            }

            if (acquireResult.Code == Vortice.DXGI.ResultCode.AccessLost.Code ||
                acquireResult.Code == Vortice.DXGI.ResultCode.AccessDenied.Code)
            {
                return FrameAcquireStatus.AccessLost;
            }

            return FrameAcquireStatus.NoFrame;
        }

        private static bool DetectCursorMovement(ref int lastCursorX, ref int lastCursorY)
        {
            try
            {
                CURSORINFO curInfo = new() { cbSize = Marshal.SizeOf<CURSORINFO>() };
                if (GetCursorInfo(out curInfo) && curInfo.flags == CURSOR_SHOWING)
                {
                    if (curInfo.ptScreenPos.x != lastCursorX || curInfo.ptScreenPos.y != lastCursorY)
                    {
                        lastCursorX = curInfo.ptScreenPos.x;
                        lastCursorY = curInfo.ptScreenPos.y;
                        return true;
                    }
                }
            }
            catch { }

            return false;
        }

        private void ProcessAndDispatchFrame(
            DxgiSessionResources session,
            Stopwatch frameProcStopwatch,
            HostFpsTracker fpsTracker)
        {
            bool forceKeyframe = _forceKeyframeRequested;
            _forceKeyframeRequested = false;

            var mapped = session.Context!.Map(session.StagingTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var buffer = new RawFrameBuffer(
                    mapped.DataPointer,
                    session.Bounds.ScreenWidth,
                    session.Bounds.ScreenHeight,
                    (int)mapped.RowPitch);

                var options = new FrameEncodingOptions(
                    _quality,
                    ResolutionScale,
                    session.Bounds.ScreenLeft,
                    session.Bounds.ScreenTop,
                    _targetWidth,
                    _targetHeight,
                    forceKeyframe);

                var frame = EncodeFrameFromPointer(buffer, options);
                if (frame != null)
                {
                    frameProcStopwatch.Stop();
                    frame.HostFrameTimeMs = (int)frameProcStopwatch.ElapsedMilliseconds;
                    frame.HostFps = fpsTracker.RecordFrameAndGetFps();

                    FrameCaptured?.Invoke(this, frame);
                }
            }
            finally
            {
                session.Context.Unmap(session.StagingTexture!, 0);
            }
        }

        private static void ApplyFramePacing(Stopwatch stopwatch, int frameIntervalMs)
        {
            int elapsed = (int)stopwatch.ElapsedMilliseconds;
            int delay = frameIntervalMs - elapsed;
            if (delay > 0)
            {
                Thread.Sleep(delay);
            }
        }

        public readonly record struct RawFrameBuffer(IntPtr DataPointer, int Width, int Height, int RowPitch);

        public readonly record struct DesktopBounds(int ScreenWidth, int ScreenHeight, int ScreenLeft, int ScreenTop);

        private sealed class HostFpsTracker
        {
            private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
            private int _frameCount;
            private int _currentFps;

            public int RecordFrameAndGetFps()
            {
                _frameCount++;
                if (_stopwatch.ElapsedMilliseconds >= 1000)
                {
                    _currentFps = (int)Math.Round(_frameCount * 1000.0 / _stopwatch.ElapsedMilliseconds);
                    _frameCount = 0;
                    _stopwatch.Restart();
                }
                return _currentFps > 0 ? _currentFps : _frameCount;
            }
        }

        private sealed class DxgiSessionResources : IDisposable
        {
            public IDXGIFactory1? Factory { get; init; }
            public IDXGIAdapter1? Adapter { get; init; }
            public IDXGIOutput? Output { get; init; }
            public IDXGIOutput1? Output1 { get; init; }
            public ID3D11Device? Device { get; init; }
            public ID3D11DeviceContext? Context { get; init; }
            public IDXGIOutputDuplication? Duplication { get; init; }
            public ID3D11Texture2D? StagingTexture { get; init; }
            public DesktopBounds Bounds { get; init; }

            public void Dispose()
            {
                StagingTexture?.Dispose();
                Duplication?.Dispose();
                Output1?.Dispose();
                Output?.Dispose();
                Context?.Dispose();
                Device?.Dispose();
                Adapter?.Dispose();
                Factory?.Dispose();
            }
        }

        private DxgiSessionResources CreateDxgiSession()
        {
            if (DXGI.CreateDXGIFactory1(out IDXGIFactory1? factory).Failure || factory == null)
            {
                throw new InvalidOperationException("No se pudo crear IDXGIFactory1");
            }

            FindTargetAdapterAndOutput(factory, _deviceName, _monitorIndex, out IDXGIAdapter1? adapter, out IDXGIOutput? output);
            CreateD3D11Device(adapter, out ID3D11Device? device, out ID3D11DeviceContext? context);
            var output1 = output.QueryInterface<IDXGIOutput1>();

            var bounds = output.Description.DesktopCoordinates;
            int screenWidth = bounds.Right - bounds.Left;
            int screenHeight = bounds.Bottom - bounds.Top;

            var duplication = output1.DuplicateOutput(device);

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

            var stagingTexture = device.CreateTexture2D(stagingDesc);

            return new DxgiSessionResources
            {
                Factory = factory,
                Adapter = adapter,
                Output = output,
                Output1 = output1,
                Device = device,
                Context = context,
                Duplication = duplication,
                StagingTexture = stagingTexture,
                Bounds = new DesktopBounds(screenWidth, screenHeight, bounds.Left, bounds.Top)
            };
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

            if (d3dResult.Failure)
            {
                throw new InvalidOperationException($"No se pudo crear el dispositivo Direct3D 11: {d3dResult}");
            }
            if (dev == null || ctx == null)
            {
                throw new InvalidOperationException("Direct3D 11 devolvió un dispositivo o contexto nulo.");
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

        private static int ComputeTargetDimension(int customTarget, int original, double scale)
        {
            if (customTarget > 0) return customTarget;
            if (scale > 0.2 && scale < 0.99) return (int)(original * scale);
            return 0;
        }

        private static (SKBitmap bitmapToEncode, SKBitmap? disposableBitmap, int finalWidth, int finalHeight) PrepareEncodingBitmap(
            SKBitmap skBitmap,
            in RawFrameBuffer buffer,
            in FrameEncodingOptions options)
        {
            int targetW = ComputeTargetDimension(options.CustomTargetWidth, buffer.Width, options.Scale);
            int targetH = ComputeTargetDimension(options.CustomTargetHeight, buffer.Height, options.Scale);

            if (NeedsResize(targetW, targetH, buffer.Width, buffer.Height))
            {
                var resized = skBitmap.Resize(new SKImageInfo(targetW, targetH), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                if (resized != null)
                {
                    return (resized, resized, targetW, targetH);
                }
            }

            var copied = skBitmap.Copy();
            return (copied ?? skBitmap, copied, buffer.Width, buffer.Height);
        }

        private CapturedFrame? EncodeFrameFromPointer(
            in RawFrameBuffer buffer,
            in FrameEncodingOptions options)
        {
            try
            {
                var imageInfo = new SKImageInfo(buffer.Width, buffer.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var skBitmap = new SKBitmap();
                skBitmap.InstallPixels(imageInfo, buffer.DataPointer, buffer.RowPitch);

                var (bitmapToEncode, resizedBitmap, finalWidth, finalHeight) = PrepareEncodingBitmap(skBitmap, buffer, options);

                try
                {
                    float scaleX = (float)finalWidth / Math.Max(1, buffer.Width);
                    float scaleY = (float)finalHeight / Math.Max(1, buffer.Height);
                    DrawCursorOnBitmap(bitmapToEncode, new CursorDrawOptions(options.ScreenLeft, options.ScreenTop, scaleX, scaleY));

                    string currentCodec = _codec;
                    byte[]? encodedBytes = currentCodec == "h264"
                        ? EncodeH264(bitmapToEncode, finalWidth, finalHeight, options.ForceKeyframe)
                        : EncodeJpeg(bitmapToEncode, options.Quality);

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
                finally
                {
                    resizedBitmap?.Dispose();
                }
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
