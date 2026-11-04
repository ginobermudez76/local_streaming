using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Media
{
    [SupportedOSPlatform("windows")]
    public class CaptureSourceDiscoveryService
    {
        public static List<CaptureSource> GetScreenSources()
        {
            var sources = new List<CaptureSource>();

            if (!OperatingSystem.IsWindows())
                return sources;

            try
            {
                // 1. Opción de Todas las pantallas (Escritorio virtual)
                int vLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
                int vTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
                int vWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
                int vHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);

                if (vWidth <= 0 || vHeight <= 0)
                {
                    vWidth = GetSystemMetrics(SM_CXSCREEN);
                    vHeight = GetSystemMetrics(SM_CYSCREEN);
                    vLeft = 0;
                    vTop = 0;
                }

                // 2. Enumerar cada monitor físico detectado
                int monitorIndex = 0;
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
                {
                    var mi = new MONITORINFOEX();
                    mi.cbSize = Marshal.SizeOf<MONITORINFOEX>();

                    if (GetMonitorInfo(hMonitor, ref mi))
                    {
                        int w = mi.rcMonitor.Right - mi.rcMonitor.Left;
                        int h = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
                        bool isPrimary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;
                        string devName = new string(mi.szDevice).TrimEnd('\0');
                        string monitorName = isPrimary ? $"Pantalla {monitorIndex + 1} (Principal)" : $"Pantalla {monitorIndex + 1}";

                        sources.Add(new CaptureSource
                        {
                            Type = CaptureSourceType.Screen,
                            Id = $"screen:{monitorIndex}",
                            Name = monitorName,
                            Subtitle = $"{w}×{h} • {devName}",
                            MonitorIndex = monitorIndex,
                            DeviceName = devName,
                            Handle = hMonitor,
                            X = mi.rcMonitor.Left,
                            Y = mi.rcMonitor.Top,
                            Width = w,
                            Height = h,
                            IsPrimary = isPrimary
                        });

                        monitorIndex++;
                    }
                    return true;
                }, IntPtr.Zero);

                // Si hay más de un monitor, agregar la opción de "Todas las pantallas" al principio
                if (sources.Count > 1)
                {
                    sources.Insert(0, new CaptureSource
                    {
                        Type = CaptureSourceType.AllScreens,
                        Id = "screen:all",
                        Name = "Todas las Pantallas (Escritorio Completo)",
                        Subtitle = $"{vWidth}×{vHeight} • Combinadas",
                        DeviceName = "VirtualDesktop",
                        X = vLeft,
                        Y = vTop,
                        Width = vWidth,
                        Height = vHeight,
                        IsPrimary = false
                    });
                }
                else if (sources.Count == 0)
                {
                    // Fallback a pantalla principal
                    sources.Add(new CaptureSource
                    {
                        Type = CaptureSourceType.Screen,
                        Id = "screen:0",
                        Name = "Pantalla Principal",
                        Subtitle = $"{vWidth}×{vHeight}",
                        MonitorIndex = 0,
                        DeviceName = @"\\.\DISPLAY1",
                        X = 0,
                        Y = 0,
                        Width = vWidth,
                        Height = vHeight,
                        IsPrimary = true
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CaptureDiscovery] Error enumerando pantallas: {ex.Message}");
            }

            return sources;
        }

        public static List<CaptureSource> GetWindowSources()
        {
            var sources = new List<CaptureSource>();

            if (!OperatingSystem.IsWindows())
                return sources;

            try
            {
                uint currentProcessId = (uint)Process.GetCurrentProcess().Id;

                EnumWindows((hWnd, lParam) =>
                {
                    if (!IsWindowVisible(hWnd))
                        return true;

                    // Omitir ventanas cloaked (ocultas por DWM, como apps UWP en segundo plano o escritorios virtuales)
                    int cloaked = 0;
                    if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0)
                        return true;

                    var sbTitle = new StringBuilder(512);
                    GetWindowText(hWnd, sbTitle, 512);
                    string title = sbTitle.ToString().Trim();

                    if (string.IsNullOrEmpty(title))
                        return true;

                    // Excluir ventanas del sistema conocidas que no son aplicaciones de usuario
                    var sbClass = new StringBuilder(256);
                    GetClassName(hWnd, sbClass, 256);
                    string className = sbClass.ToString();

                    if (className == "Progman" || className == "WorkerW" ||
                        className == "Shell_TrayWnd" || className == "Shell_SecondaryTrayWnd" ||
                        className == "Windows.UI.Core.CoreWindow" && title == "Inicio" ||
                        className == "EdgeUiInputTopWndClass")
                    {
                        return true;
                    }

                    // Excluir ventanas sin tamaño real (ancho/alto menor a 120px)
                    if (!GetWindowRect(hWnd, out RECT rect))
                        return true;

                    int w = rect.Right - rect.Left;
                    int h = rect.Bottom - rect.Top;
                    if (w < 120 || h < 120)
                        return true;

                    GetWindowThreadProcessId(hWnd, out uint pid);

                    // Excluir nuestra propia aplicación para evitar bucles de captura en espejo
                    if (pid == currentProcessId)
                        return true;

                    string procName = "";
                    try
                    {
                        using var proc = Process.GetProcessById((int)pid);
                        procName = proc.ProcessName;
                    }
                    catch
                    {
                        procName = "Aplicación";
                    }

                    // Excluir procesos de sistema de fondo
                    if (procName.Equals("SearchHost", StringComparison.OrdinalIgnoreCase) ||
                        procName.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                        procName.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase) ||
                        procName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    sources.Add(new CaptureSource
                    {
                        Type = CaptureSourceType.Window,
                        Id = $"window:{hWnd.ToInt64()}",
                        Name = title,
                        Subtitle = $"{procName} • {w}×{h}",
                        Handle = hWnd,
                        ProcessName = procName,
                        ProcessId = (int)pid,
                        X = rect.Left,
                        Y = rect.Top,
                        Width = w,
                        Height = h
                    });

                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CaptureDiscovery] Error enumerando ventanas: {ex.Message}");
            }

            return sources;
        }

        #region Win32 P/Invoke

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;
        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;
        private const int MONITORINFOF_PRIMARY = 1;
        private const int DWMWA_CLOAKED = 14;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        #endregion
    }
}

