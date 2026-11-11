using System;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;
using LanScreenShare.Core.Services;

namespace LanScreenShare.Media
{
    /// <summary>
    /// Fábrica desacoplada para instanciación del motor de captura de pantalla según el sistema operativo y la fuente seleccionada.
    /// En Windows selecciona DXGI (GPU acelerado), GDI (BitBlt CPU) o WindowCapture (HWND).
    /// En Linux, Android, macOS o entornos sin GPU nativa de Windows selecciona CrossPlatformScreenCaptureService.
    /// </summary>
    public static class CaptureServiceFactory
    {
        public static IScreenCaptureService Create(CaptureSource source, string engine)
        {
            if (!OperatingSystem.IsWindows())
            {
                return new CrossPlatformScreenCaptureService();
            }

            if (source.Type == CaptureSourceType.Window && source.Handle != IntPtr.Zero)
            {
                return new WindowCaptureService(source.Handle);
            }

            if (source.Type == CaptureSourceType.AllScreens || engine == "gdi")
            {
                var gdi = new WindowsScreenCaptureService();
                if (source.Type == CaptureSourceType.AllScreens)
                {
                    gdi.SetTargetBounds(0, 0, 0, 0, allScreens: true);
                }
                else
                {
                    gdi.SetTargetBounds(source.X, source.Y, source.Width, source.Height, allScreens: false);
                }
                return gdi;
            }

            var dxgi = new DxgiScreenCaptureService
            {
                MonitorIndex = source.MonitorIndex,
                DeviceName = source.DeviceName,
                TargetX = source.X,
                TargetY = source.Y,
                TargetSourceWidth = source.Width,
                TargetSourceHeight = source.Height
            };
            return dxgi;
        }
    }
}

