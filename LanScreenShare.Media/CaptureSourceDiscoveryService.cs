using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using LanScreenShare.Core.Models;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

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

            uint rootPid = ResolveEffectiveWindowProcess(hWnd, pid, out string procName);
            if (rootPid > 0)
            {
                return (int)rootPid;
            }

            int audioSessionPid = FindAudioSessionProcessId(procName, (int)pid);
            if (audioSessionPid > 0)
            {
                Console.WriteLine($"[CaptureDiscovery] Sesión de audio identificada para '{procName}': PID {audioSessionPid}");
                return audioSessionPid;
            }

            return (int)pid;
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

            // 2. Si el proceso pertenece a una aplicación multiproceso (ej. Chrome, Edge, Discord, Spotify, WhatsApp),
            // encontrar el proceso raíz mediante Toolhelp32 snapshot para que IncludeTargetProcessTree cubra todo el árbol de procesos.
            if (!string.IsNullOrEmpty(procName) && !procName.Equals("Aplicación", StringComparison.OrdinalIgnoreCase))
            {
                uint rootPid = FindApplicationRootProcessId(pid, procName);
                if (rootPid > 0)
                {
                    pid = rootPid;
                }
            }

            return pid;
        }

        public static int FindAudioSessionProcessId(string procName, int fallbackPid)
        {
            if (string.IsNullOrEmpty(procName) || procName.Equals("Aplicación", StringComparison.OrdinalIgnoreCase))
                return fallbackPid;

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var mgr = device.AudioSessionManager;
                var sessions = mgr.Sessions;

                int activePid = 0;
                int matchingPid = 0;

                for (int i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    uint sPid = s.GetProcessID;
                    if (sPid == 0) continue;

                    string sName = GetProcessNameSafe(sPid);
                    if (sName.Equals(procName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (s.State == AudioSessionState.AudioSessionStateActive)
                        {
                            activePid = (int)sPid;
                            break;
                        }
                        matchingPid = (int)sPid;
                    }
                }

                if (activePid > 0)
                    return activePid;

                if (matchingPid > 0)
                    return matchingPid;
            }
            catch
            {
                // Dispositivo o driver no expone IAudioSessionManager2 o sin sesiones activas
            }

            return fallbackPid;
        }

        public static uint FindApplicationRootProcessId(uint pid, string procName)
        {
            if (pid == 0)
                return 0;

            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
                return pid;

            try
            {
                var parentMap = new Dictionary<uint, uint>();
                var nameMap = new Dictionary<uint, string>();
                var pe = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };

                if (Process32First(snapshot, ref pe))
                {
                    do
                    {
                        parentMap[pe.th32ProcessID] = pe.th32ParentProcessID;
                        string exeName = pe.szExeFile;
                        int dotIdx = exeName.LastIndexOf('.');
                        if (dotIdx > 0)
                            exeName = exeName.Substring(0, dotIdx);
                        nameMap[pe.th32ProcessID] = exeName;
                    } while (Process32Next(snapshot, ref pe));
                }

                uint current = pid;
                for (int depth = 0; depth < 10; depth++)
                {
                    if (!parentMap.TryGetValue(current, out uint parentPid) || parentPid == 0 || parentPid == current)
                        break;

                    if (nameMap.TryGetValue(parentPid, out string? parentName) &&
                        parentName.Equals(procName, StringComparison.OrdinalIgnoreCase))
                    {
                        current = parentPid;
                    }
                    else
                    {
                        break;
                    }
                }

                return current;
            }
            catch
            {
                return pid;
            }
            finally
            {
                CloseHandle(snapshot);
            }
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

        #region Win32 P/Invoke

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;
        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;
        private const int MONITORINFOF_PRIMARY = 1;
        private const int DWMWA_CLOAKED = 14;
        private const uint TH32CS_SNAPPROCESS = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

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
