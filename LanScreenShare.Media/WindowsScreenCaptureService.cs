using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;
using SkiaSharp;

namespace LanScreenShare.Media
{
    public class WindowsScreenCaptureService : IScreenCaptureService
    {
        private CancellationTokenSource? _cts;
        private Task? _captureTask;
        private bool _disposed;

        public event EventHandler<CapturedFrame>? FrameCaptured;
        public bool IsCapturing => _cts != null && !_cts.IsCancellationRequested;

        public void StartCapture(int targetFps = 30, int quality = 75)
        {
            if (IsCapturing)
                return;

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            int frameIntervalMs = Math.Max(10, 1000 / targetFps);

            _captureTask = Task.Run(async () =>
            {
                var stopwatch = new Stopwatch();

                while (!token.IsCancellationRequested)
                {
                    stopwatch.Restart();

                    try
                    {
                        var frame = CaptureDesktopFrame(quality);
                        if (frame != null)
                        {
                            FrameCaptured?.Invoke(this, frame);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ScreenCapture] Error capturando frame: {ex.Message}");
                    }

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
            }, token);
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

        private CapturedFrame? CaptureDesktopFrame(int quality)
        {
            int left = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int top = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int height = GetSystemMetrics(SM_CYVIRTUALSCREEN);

            if (width <= 0 || height <= 0)
            {
                width = GetSystemMetrics(SM_CXSCREEN);
                height = GetSystemMetrics(SM_CYSCREEN);
                left = 0;
                top = 0;
            }

            if (width <= 0 || height <= 0)
                return null;

            IntPtr hdcSrc = GetDC(IntPtr.Zero);
            if (hdcSrc == IntPtr.Zero)
                return null;

            IntPtr hdcDest = CreateCompatibleDC(hdcSrc);
            if (hdcDest == IntPtr.Zero)
            {
                ReleaseDC(IntPtr.Zero, hdcSrc);
                return null;
            }

            BITMAPINFO bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = width;
            bmi.bmiHeader.biHeight = -height; // Top-down DIB
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

            // Copiar el contenido de la pantalla al DC de destino
            BitBlt(hdcDest, 0, 0, width, height, hdcSrc, left, top, SRCCOPY | CAPTUREBLT);

            // Dibujar el cursor del mouse sobre el fotograma capturado
            DrawMouseCursor(hdcDest, left, top);

            byte[]? jpegBytes = null;
            try
            {
                var imageInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using (var skBitmap = new SKBitmap())
                {
                    skBitmap.InstallPixels(imageInfo, pBits);

                    using (var image = SKImage.FromBitmap(skBitmap))
                    {
                        if (image != null)
                        {
                            using (var data = image.Encode(SKEncodedImageFormat.Jpeg, quality))
                            {
                                jpegBytes = data.ToArray();
                            }
                        }
                    }
                }
            }
            finally
            {
                SelectObject(hdcDest, hOld);
                DeleteObject(hBitmap);
                DeleteDC(hdcDest);
                ReleaseDC(IntPtr.Zero, hdcSrc);
            }

            if (jpegBytes == null || jpegBytes.Length == 0)
                return null;

            return new CapturedFrame
            {
                Data = jpegBytes,
                Width = width,
                Height = height,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Format = "jpeg"
            };
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
                // Continuar sin cursor si falla la consulta
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            StopCapture();
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        #region Win32 P/Invoke

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;
        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;

        private const int SRCCOPY = 0x00CC0020;
        private const int CAPTUREBLT = 0x40000000;
        private const int DIB_RGB_COLORS = 0;
        private const int BI_RGB = 0;
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
        private static extern int GetSystemMetrics(int nIndex);

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
        private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

        [DllImport("user32.dll")]
        private static extern bool GetCursorInfo(out CURSORINFO pci);

        [DllImport("user32.dll")]
        private static extern bool DrawIcon(IntPtr hDC, int X, int Y, IntPtr hIcon);

        #endregion
    }
}
