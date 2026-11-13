using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;
using SkiaSharp;

namespace LanScreenShare.Media
{
    [SupportedOSPlatform("windows")]
    public class WindowsScreenCaptureService : IScreenCaptureService
    {
        public readonly record struct ScreenBounds(int X, int Y, int Width, int Height, bool AllScreens = false);

        public event EventHandler<CapturedFrame>? FrameCaptured;
        public event EventHandler<bool>? SecureDesktopChanged;

        protected virtual void OnSecureDesktopChanged(bool isSecure) =>
            SecureDesktopChanged?.Invoke(this, isSecure);
        private CancellationTokenSource? _cts;
        private Task? _captureTask;
        private bool _disposed;

        private volatile int _targetFps = 30;
        private volatile int _quality = 70;
        private volatile int _scalePercent = 100;
        private volatile int _targetWidth = 0;
        private volatile int _targetHeight = 0;
        private volatile string _codec = "h264";
        private volatile bool _forceKeyframeRequested = true;
        private byte[]? _reusableBgraBytes;
        private MediaFoundationH264EncoderService? _h264Encoder;

        public void RequestKeyframe()
        {
            _forceKeyframeRequested = true;
        }

        private volatile int _captureX = 0;
        private volatile int _captureY = 0;
        private volatile int _captureWidth = 0;
        private volatile int _captureHeight = 0;
        private volatile bool _captureAllScreens = false;

        public bool IsCapturing => _cts != null && !_cts.IsCancellationRequested;
        public string? ActiveEncoderDescription => _codec == "h264" ? _h264Encoder?.EncoderDescription : "JPEG";
        public bool IsHardwareAccelerated => _codec == "h264" && (_h264Encoder?.IsHardwareAccelerated ?? false);

        public int CaptureX
        {
            get => _captureX;
            set => _captureX = value;
        }

        public int CaptureY
        {
            get => _captureY;
            set => _captureY = value;
        }

        public int CaptureWidth
        {
            get => _captureWidth;
            set => _captureWidth = Math.Max(0, value);
        }

        public int CaptureHeight
        {
            get => _captureHeight;
            set => _captureHeight = Math.Max(0, value);
        }

        public bool CaptureAllScreens
        {
            get => _captureAllScreens;
            set => _captureAllScreens = value;
        }

        public void SetTargetBounds(ScreenBounds bounds)
        {
            _captureX = bounds.X;
            _captureY = bounds.Y;
            _captureWidth = Math.Max(0, bounds.Width);
            _captureHeight = Math.Max(0, bounds.Height);
            _captureAllScreens = bounds.AllScreens;
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

            _captureTask = Task.Run(() => CaptureLoopAsync(token), token);
        }

        private async Task CaptureLoopAsync(CancellationToken token)
        {
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
            }
            catch { }

            var stopwatch = new Stopwatch();
            var fpsTracker = new HostFpsTracker();

            while (!token.IsCancellationRequested)
            {
                stopwatch.Restart();
                int frameIntervalMs = Math.Max(5, 1000 / Math.Max(1, _targetFps));

                CaptureAndDispatchFrame(fpsTracker);

                int elapsed = (int)stopwatch.ElapsedMilliseconds;
                int delay = frameIntervalMs - elapsed;
                if (delay > 0)
                {
                    try
                    {
                        await Task.Delay(delay, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        private void CaptureAndDispatchFrame(HostFpsTracker fpsTracker)
        {
            try
            {
                var frameProcStopwatch = Stopwatch.StartNew();
                bool forceKeyframe = _forceKeyframeRequested;
                _forceKeyframeRequested = false;

                var options = new ScreenCaptureOptions(_quality, ResolutionScale, _targetWidth, _targetHeight, forceKeyframe);
                var frame = CaptureDesktopFrame(options);
                if (frame != null)
                {
                    frameProcStopwatch.Stop();
                    frame.HostFrameTimeMs = (int)frameProcStopwatch.ElapsedMilliseconds;
                    frame.HostFps = fpsTracker.RecordFrameAndGetFps();

                    FrameCaptured?.Invoke(this, frame);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScreenCapture] Error capturando frame: {ex.Message}");
            }
        }

        public void StopCapture()
        {
            _cts?.Cancel();
            try
            {
                _captureTask?.Wait(500);
            }
            catch
            {
                // Ignorar excepciones al esperar cancelación
            }
            _cts?.Dispose();
            _cts = null;
            _captureTask = null;
        }

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

        private readonly record struct ScreenCaptureOptions(
            int Quality,
            double Scale,
            int CustomTargetWidth = 0,
            int CustomTargetHeight = 0,
            bool ForceKeyframe = false);

        private static bool NeedsResize(int targetW, int targetH, int width, int height) =>
            targetW > 0 && targetH > 0 && (targetW != width || targetH != height);

        private bool ShouldCaptureVirtualScreen() =>
            _captureAllScreens || _captureWidth <= 0 || _captureHeight <= 0;

        private void GetCaptureBounds(out int left, out int top, out int width, out int height)
        {
            if (ShouldCaptureVirtualScreen())
            {
                left = GetSystemMetrics(SM_XVIRTUALSCREEN);
                top = GetSystemMetrics(SM_YVIRTUALSCREEN);
                width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
                height = GetSystemMetrics(SM_CYVIRTUALSCREEN);

                if (width <= 0 || height <= 0)
                {
                    width = GetSystemMetrics(SM_CXSCREEN);
                    height = GetSystemMetrics(SM_CYSCREEN);
                    left = 0;
                    top = 0;
                }
            }
            else
            {
                left = _captureX;
                top = _captureY;
                width = _captureWidth;
                height = _captureHeight;
            }
        }

        private sealed class GdiDesktopCaptureSession : IDisposable
        {
            public IntPtr HdcSrc { get; private set; }
            public IntPtr HdcDest { get; private set; }
            public IntPtr HBitmap { get; private set; }
            public IntPtr PBits { get; private set; }
            public IntPtr HOld { get; private set; }

            public static GdiDesktopCaptureSession? TryCreate(int width, int height)
            {
                IntPtr hdcSrc = GetDC(IntPtr.Zero);
                if (hdcSrc == IntPtr.Zero) return null;

                IntPtr hdcDest = CreateCompatibleDC(hdcSrc);
                if (hdcDest == IntPtr.Zero)
                {
                    ReleaseDC(IntPtr.Zero, hdcSrc);
                    return null;
                }

                var bmi = new BITMAPINFO();
                bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
                bmi.bmiHeader.biWidth = width;
                bmi.bmiHeader.biHeight = -height;
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = BI_RGB;

                IntPtr hBitmap = CreateDIBSection(hdcDest, ref bmi, DIB_RGB_COLORS, out IntPtr pBits, IntPtr.Zero, 0);
                if (hBitmap == IntPtr.Zero || pBits == IntPtr.Zero)
                {
                    DeleteDC(hdcDest);
                    ReleaseDC(IntPtr.Zero, hdcSrc);
                    return null;
                }

                IntPtr hOld = SelectObject(hdcDest, hBitmap);
                return new GdiDesktopCaptureSession
                {
                    HdcSrc = hdcSrc,
                    HdcDest = hdcDest,
                    HBitmap = hBitmap,
                    PBits = pBits,
                    HOld = hOld
                };
            }

            public void Dispose()
            {
                if (HOld != IntPtr.Zero) SelectObject(HdcDest, HOld);
                if (HBitmap != IntPtr.Zero) DeleteObject(HBitmap);
                if (HdcDest != IntPtr.Zero) DeleteDC(HdcDest);
                if (HdcSrc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, HdcSrc);
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
            int width,
            int height,
            in ScreenCaptureOptions options)
        {
            int targetW = ComputeTargetDimension(options.CustomTargetWidth, width, options.Scale);
            int targetH = ComputeTargetDimension(options.CustomTargetHeight, height, options.Scale);

            if (NeedsResize(targetW, targetH, width, height))
            {
                var resized = skBitmap.Resize(new SKImageInfo(targetW, targetH), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                if (resized != null)
                {
                    return (resized, resized, targetW, targetH);
                }
            }

            return (skBitmap, null, width, height);
        }

        private CapturedFrame? EncodeCapturedBitmap(IntPtr pBits, int width, int height, in ScreenCaptureOptions options)
        {
            var imageInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var skBitmap = new SKBitmap();
            skBitmap.InstallPixels(imageInfo, pBits);

            var (bitmapToEncode, resizedBitmap, finalWidth, finalHeight) = PrepareEncodingBitmap(skBitmap, width, height, options);

            try
            {
                string currentCodec = _codec;
                byte[]? encodedBytes = null;

                if (currentCodec == "h264")
                {
                    encodedBytes = EncodeH264(bitmapToEncode, finalWidth, finalHeight, options.ForceKeyframe);
                }

                if (encodedBytes == null || encodedBytes.Length == 0)
                {
                    currentCodec = "jpeg";
                    encodedBytes = EncodeJpeg(bitmapToEncode, options.Quality);
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
            finally
            {
                resizedBitmap?.Dispose();
            }
        }

        private CapturedFrame? CaptureDesktopFrame(in ScreenCaptureOptions options)
        {
            GetCaptureBounds(out int left, out int top, out int width, out int height);
            if (width <= 0 || height <= 0) return null;

            using var session = GdiDesktopCaptureSession.TryCreate(width, height);
            if (session == null) return null;

            BitBlt(session.HdcDest, 0, 0, width, height, session.HdcSrc, left, top, SRCCOPY | CAPTUREBLT);
            DrawMouseCursor(session.HdcDest, left, top);

            return EncodeCapturedBitmap(session.PBits, width, height, options);
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
                Console.WriteLine($"[GDI] H.264 error, fallback automático a JPEG: {ex.Message}");
                return null;
            }
        }

        private static byte[]? EncodeJpeg(SKBitmap bitmap, int quality)
        {
            using var image = SKImage.FromBitmap(bitmap);
            if (image != null)
            {
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
                return data?.ToArray();
            }
            return null;
        }

        private static void DrawMouseCursor(IntPtr hdcDest, int screenLeft, int screenTop)
        {
            try
            {
                CURSORINFO cursorInfo = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
                if (GetCursorInfo(out cursorInfo) && cursorInfo.flags == CURSOR_SHOWING)
                {
                    DrawIcon(hdcDest, cursorInfo.ptScreenPos.x - screenLeft, cursorInfo.ptScreenPos.y - screenTop, cursorInfo.hCursor);
                }
            }
            catch
            {
                // Ignorar error al capturar el cursor
            }
        }

        #region P/Invoke GDI & User32

        private const int SRCCOPY = 0x00CC0020;
        private const int CAPTUREBLT = 0x40000000;
        private const int DIB_RGB_COLORS = 0;
        private const int BI_RGB = 0;
        private const int CURSOR_SHOWING = 0x00000001;

        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hObject, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hObjectSource, int nXSrc, int nYSrc, int dwRop);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, [In] ref BITMAPINFO pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern bool DrawIcon(IntPtr hDC, int X, int Y, IntPtr hIcon);

        [DllImport("user32.dll")]
        private static extern bool GetCursorInfo(out CURSORINFO pci);

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public uint[] bmiColors;
        }

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
