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
    public class WindowCaptureService : IScreenCaptureService
    {
        private readonly IntPtr _hwnd;
        private CancellationTokenSource? _cts;
        private Task? _captureTask;
        private bool _disposed;

        private volatile int _targetFps = 30;
        private volatile int _quality = 75;
        private volatile int _scalePercent = 100;
        private volatile int _targetWidth = 0;
        private volatile int _targetHeight = 0;
        private volatile string _codec = "jpeg";
        private MediaFoundationH264EncoderService? _h264Encoder;

        public event EventHandler<CapturedFrame>? FrameCaptured;
        public bool IsCapturing => _cts != null && !_cts.IsCancellationRequested;

        public IntPtr WindowHandle => _hwnd;

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

        public WindowCaptureService(IntPtr hwnd)
        {
            _hwnd = hwnd;
        }

        public void UpdateSettings(int targetFps, int quality, double resolutionScale = 1.0, string codec = "jpeg", int targetWidth = 0, int targetHeight = 0)
        {
            TargetFps = targetFps;
            Quality = quality;
            ResolutionScale = resolutionScale;
            Codec = codec;
            TargetWidth = targetWidth;
            TargetHeight = targetHeight;
        }

        public void StartCapture(int targetFps = 30, int quality = 75, double resolutionScale = 1.0, string codec = "jpeg", int targetWidth = 0, int targetHeight = 0)
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
                _captureTask?.Wait(800);
            }
            catch
            {
                // Ignorar excepciones al esperar cancelación
            }
            _cts?.Dispose();
            _cts = null;
            _captureTask = null;
        }

        private void CaptureLoop(CancellationToken token)
        {
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
            }
            catch { }

            var stopwatch = new Stopwatch();
            var fpsStopwatch = Stopwatch.StartNew();
            int capturedFramesCount = 0;
            int currentHostFps = 0;

            while (!token.IsCancellationRequested)
            {
                stopwatch.Restart();

                if (!IsWindow(_hwnd))
                {
                    Console.WriteLine("[WindowCapture] La ventana objetivo fue cerrada.");
                    break;
                }

                // Si la ventana está minimizada, pausar brevemente
                if (IsIconic(_hwnd))
                {
                    Thread.Sleep(100);
                    continue;
                }

                int currentFps = _targetFps;
                int frameIntervalMs = Math.Max(5, 1000 / Math.Max(1, currentFps));

                try
                {
                    var frameProcStopwatch = Stopwatch.StartNew();
                    var frame = CaptureWindowFrame(_hwnd, _quality, ResolutionScale, _targetWidth, _targetHeight);

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
                catch (Exception ex)
                {
                    Console.WriteLine($"[WindowCapture] Error capturando ventana: {ex.Message}");
                }

                int elapsed = (int)stopwatch.ElapsedMilliseconds;
                int delay = frameIntervalMs - elapsed;
                if (delay > 0)
                {
                    Thread.Sleep(delay);
                }
            }
        }

        private CapturedFrame? CaptureWindowFrame(IntPtr hwnd, int quality, double scale, int customTargetW = 0, int customTargetH = 0)
        {
            if (!GetClientRect(hwnd, out RECT clientRect))
                return null;

            int width = clientRect.Right - clientRect.Left;
            int height = clientRect.Bottom - clientRect.Top;

            if (width <= 0 || height <= 0)
                return null;

            IntPtr hdcWindow = GetDC(hwnd);
            if (hdcWindow == IntPtr.Zero)
                return null;

            IntPtr hdcMem = CreateCompatibleDC(hdcWindow);
            if (hdcMem == IntPtr.Zero)
            {
                ReleaseDC(hwnd, hdcWindow);
                return null;
            }

            BITMAPINFO bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = width;
            bmi.bmiHeader.biHeight = -height; // Top-down DIB
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = BI_RGB;

            IntPtr hBitmap = CreateDIBSection(hdcMem, ref bmi, DIB_RGB_COLORS, out IntPtr pBits, IntPtr.Zero, 0);
            if (hBitmap == IntPtr.Zero || pBits == IntPtr.Zero)
            {
                DeleteDC(hdcMem);
                ReleaseDC(hwnd, hdcWindow);
                return null;
            }

            IntPtr hOld = SelectObject(hdcMem, hBitmap);

            try
            {
                // Intentar capturar con PrintWindow (PW_RENDERFULLCONTENT captura búferes acelerados por DWM/GPU)
                bool captured = PrintWindow(hwnd, hdcMem, PW_RENDERFULLCONTENT);
                if (!captured)
                {
                    // Fallback a BitBlt directo desde el DC de la ventana
                    BitBlt(hdcMem, 0, 0, width, height, hdcWindow, 0, 0, SRCCOPY);
                }

                // Dibujar el cursor si está sobre la ventana cliente
                DrawCursorOnDC(hwnd, hdcMem, width, height);

                int finalWidth = width;
                int finalHeight = height;

                var imageInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var skBitmap = new SKBitmap();
                skBitmap.InstallPixels(imageInfo, pBits, width * 4);

                SKBitmap bitmapToEncode = skBitmap;
                SKBitmap? resizedBitmap = null;

                int targetW = customTargetW > 0 ? customTargetW : (scale > 0.2 && scale < 0.99 ? (int)(width * scale) : 0);
                int targetH = customTargetH > 0 ? customTargetH : (scale > 0.2 && scale < 0.99 ? (int)(height * scale) : 0);

                if (targetW > 0 && targetH > 0 && (targetW != width || targetH != height))
                {
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
                            Console.WriteLine($"[WindowCapture] Error en H.264, fallback a JPEG: {ex.Message}");
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
            finally
            {
                SelectObject(hdcMem, hOld);
                DeleteObject(hBitmap);
                DeleteDC(hdcMem);
                ReleaseDC(hwnd, hdcWindow);
            }
        }

        private static void DrawCursorOnDC(IntPtr hwnd, IntPtr hdc, int clientWidth, int clientHeight)
        {
            try
            {
                CURSORINFO pci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
                if (GetCursorInfo(out pci) && pci.flags == CURSOR_SHOWING && pci.hCursor != IntPtr.Zero)
                {
                    POINT pt = pci.ptScreenPos;
                    if (ScreenToClient(hwnd, ref pt))
                    {
                        if (pt.x >= 0 && pt.x < clientWidth && pt.y >= 0 && pt.y < clientHeight)
                        {
                            DrawIconEx(hdc, pt.x, pt.y, pci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
                        }
                    }
                }
            }
            catch
            {
                // Ignorar error de dibujado de cursor
            }
        }

        #region Win32 P/Invoke

        private const int PW_RENDERFULLCONTENT = 0x00000002;
        private const int SRCCOPY = 0x00CC0020;
        private const int DIB_RGB_COLORS = 0;
        private const int BI_RGB = 0;
        private const int CURSOR_SHOWING = 0x00000001;
        private const int DI_NORMAL = 0x0003;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
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

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

        [DllImport("user32.dll")]
        private static extern bool GetCursorInfo(out CURSORINFO pci);

        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyWidth, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);

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

