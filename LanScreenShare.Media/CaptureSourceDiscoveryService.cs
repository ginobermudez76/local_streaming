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
                        string cleanDevName = devName.Replace(@"\\.\", "").Trim();
                        string monitorName = $"Pantalla {monitorIndex + 1}";

                        sources.Add(new CaptureSource
                        {
                            Type = CaptureSourceType.Screen,
                            Id = $"screen:{monitorIndex}",
                            Name = monitorName,
                            Subtitle = $"{w} × {h} · {cleanDevName}",
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
                        Name = "Todas las pantallas",
                        Subtitle = $"{vWidth} × {vHeight} · Escritorio completo",
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
                        Name = "Pantalla 1",
                        Subtitle = $"{vWidth} × {vHeight} · DISPLAY1",
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
                    pid = ResolveEffectiveWindowProcess(hWnd, pid, out string procName);

                    // Excluir nuestra propia aplicación para evitar bucles de captura en espejo
                    if (pid == currentProcessId)
                        return true;

                    // Excluir procesos de sistema de fondo
                    if (procName.Equals("SearchHost", StringComparison.OrdinalIgnoreCase) ||
                        procName.Equals("StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                        procName.Equals("TextInputHost", StringComparison.OrdinalIgnoreCase) ||
                        procName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase) ||
                        procName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    sources.Add(new CaptureSource
                    {
                        Type = CaptureSourceType.Window,
                        Id = $"window:{hWnd.ToInt64()}",
                        Name = title,
                        Subtitle = $"{procName} · {w} × {h}",
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

        public static int ResolveEffectiveProcessId(IntPtr hWnd, int fallbackPid = 0)
        {
            uint pid = (uint)Math.Max(0, fallbackPid);
            if (hWnd != IntPtr.Zero)
            {
                GetWindowThreadProcessId(hWnd, out uint winPid);
                if (winPid > 0)
                {
                    pid = winPid;
                }
            }

            if (pid == 0)
                return 0;

            return (int)ResolveEffectiveWindowProcess(hWnd, pid, out _);
        }

        private static uint ResolveEffectiveWindowProcess(IntPtr hWnd, uint initialPid, out string procName)
        {
            uint pid = initialPid;
            procName = GetProcessNameSafe(pid);

            // 1. Si es una ventana UWP / MSIX alojada por ApplicationFrameHost.exe, obtener el PID real del hijo CoreWindow
            if (hWnd != IntPtr.Zero && procName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                uint realChildPid = 0;
                EnumChildWindows(hWnd, (childHwnd, _) =>
                {
                    GetWindowThreadProcessId(childHwnd, out uint cPid);
                    if (cPid != 0 && cPid != initialPid)
                    {
                        realChildPid = cPid;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);

                if (realChildPid != 0)
                {
                    pid = realChildPid;
                    procName = GetProcessNameSafe(pid);
                }
            }

            // 2. Si el proceso tiene un padre con el mismo nombre de ejecutable (ej. Chrome, Edge, Discord, Electron),
            // subir al proceso raíz de esa misma aplicación para que IncludeTargetProcessTree capture su subproceso de audio.
            if (!string.IsNullOrEmpty(procName) && !procName.Equals("Aplicación", StringComparison.OrdinalIgnoreCase))
            {
                for (int depth = 0; depth < 6; depth++)
                {
                    uint parentPid = GetParentProcessId(pid);
                    if (parentPid == 0 || parentPid == pid)
                        break;

                    string parentName = GetProcessNameSafe(parentPid);
                    if (parentName.Equals(procName, StringComparison.OrdinalIgnoreCase))
                    {
                        pid = parentPid;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            return pid;
        }

        private static string GetProcessNameSafe(uint pid)
        {
            if (pid == 0)
                return "Aplicación";

            try
            {
                using var proc = Process.GetProcessById((int)pid);
                return proc.ProcessName;
            }
            catch
            {
                return "Aplicación";
            }
        }

        private static uint GetParentProcessId(uint pid)
        {
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                var pbi = new PROCESS_BASIC_INFORMATION();
                int status = NtQueryInformationProcess(
                    proc.Handle,
                    0, // ProcessBasicInformation
                    ref pbi,
                    Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(),
                    out _);

                if (status == 0)
                {
                    return (uint)pbi.InheritedFromUniqueProcessId.ToInt64();
                }
            }
            catch
            {
            }
            return 0;
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
        private struct PROCESS_BASIC_INFORMATION
        {
            public IntPtr Reserved1;
            public IntPtr PebBaseAddress;
            public IntPtr Reserved2_0;
            public IntPtr Reserved2_1;
            public IntPtr UniqueProcessId;
            public IntPtr InheritedFromUniqueProcessId;
        }

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
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(
            IntPtr processHandle,
            int processInformationClass,
            ref PROCESS_BASIC_INFORMATION processInformation,
            int processInformationLength,
            out int returnLength);

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
