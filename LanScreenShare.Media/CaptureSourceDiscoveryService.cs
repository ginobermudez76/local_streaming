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
    public class CaptureSourceDiscoveryService
    {
        private static readonly HashSet<string> ExcludedProcessNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "SearchHost",
            "StartMenuExperienceHost",
            "TextInputHost",
            "ShellExperienceHost",
            "ApplicationFrameHost"
        };

        private static bool IsSystemWindowClass(string className, string title)
        {
            if (className == "Progman" || className == "WorkerW")
                return true;
            if (className == "Shell_TrayWnd" || className == "Shell_SecondaryTrayWnd")
                return true;
            if (className == "EdgeUiInputTopWndClass")
                return true;
            return className == "Windows.UI.Core.CoreWindow" && title == "Inicio";
        }

        private static CaptureSource CreateFallbackScreenSource(int width, int height, string deviceName, string subtitle) =>
            new()
            {
                Type = CaptureSourceType.Screen,
                Id = "screen:0",
                Name = "Pantalla principal",
                Subtitle = subtitle,
                MonitorIndex = 0,
                DeviceName = deviceName,
                X = 0,
                Y = 0,
                Width = width,
                Height = height,
                IsPrimary = true
            };

        private static (int left, int top, int width, int height) GetVirtualScreenDimensions()
        {
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

            return (vLeft, vTop, vWidth, vHeight);
        }

        private static List<CaptureSource> EnumeratePhysicalMonitors()
        {
            var monitors = new List<CaptureSource>();
            int monitorIndex = 0;

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData) =>
            {
                var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };

                if (GetMonitorInfo(hMonitor, ref mi))
                {
                    int w = mi.rcMonitor.Right - mi.rcMonitor.Left;
                    int h = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
                    bool isPrimary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;
                    string devName = new string(mi.szDevice).TrimEnd('\0');
                    string cleanDevName = devName.Replace(@"\\.\", "").Trim();

                    monitors.Add(new CaptureSource
                    {
                        Type = CaptureSourceType.Screen,
                        Id = $"screen:{monitorIndex}",
                        Name = $"Pantalla {monitorIndex + 1}",
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

            return monitors;
        }

        public static List<CaptureSource> GetScreenSources()
        {
            var sources = new List<CaptureSource>();

            if (!OperatingSystem.IsWindows())
            {
                sources.Add(CreateFallbackScreenSource(1920, 1080, "DefaultDisplay", "1920 × 1080 · Multiplataforma"));
                return sources;
            }

            try
            {
                var (vLeft, vTop, vWidth, vHeight) = GetVirtualScreenDimensions();
                var physicalMonitors = EnumeratePhysicalMonitors();
                sources.AddRange(physicalMonitors);

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
                    sources.Add(CreateFallbackScreenSource(vWidth, vHeight, @"\\.\DISPLAY1", $"{vWidth} × {vHeight} · DISPLAY1"));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CaptureDiscovery] Error enumerando pantallas: {ex.Message}");
            }

            return sources;
        }

        private static bool IsWindowCloaked(IntPtr hWnd) =>
            DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

        private static bool TryGetWindowStrings(IntPtr hWnd, out string title, out string className)
        {
            var sbTitle = new StringBuilder(512);
            GetWindowText(hWnd, sbTitle, 512);
            title = sbTitle.ToString().Trim();

            var sbClass = new StringBuilder(256);
            GetClassName(hWnd, sbClass, 256);
            className = sbClass.ToString();

            return !string.IsNullOrEmpty(title);
        }

        private static bool TryGetWindowDimensions(IntPtr hWnd, out RECT rect, out int width, out int height)
        {
            if (!GetWindowRect(hWnd, out rect))
            {
                width = 0;
                height = 0;
                return false;
            }

            width = rect.Right - rect.Left;
            height = rect.Bottom - rect.Top;
            return width >= 120 && height >= 120;
        }

        private static bool TryCreateWindowSource(IntPtr hWnd, uint currentProcessId, out CaptureSource? source)
        {
            source = null;
            if (!IsWindowVisible(hWnd) || IsWindowCloaked(hWnd)) return false;
            if (!TryGetWindowStrings(hWnd, out string title, out string className)) return false;
            if (IsSystemWindowClass(className, title)) return false;
            if (!TryGetWindowDimensions(hWnd, out RECT rect, out int w, out int h)) return false;

            GetWindowThreadProcessId(hWnd, out uint pid);
            pid = ResolveEffectiveWindowProcess(hWnd, pid, out string procName);

            if (pid == currentProcessId || ExcludedProcessNames.Contains(procName))
                return false;

            source = new CaptureSource
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
            };

            return true;
        }

        public static List<CaptureSource> GetWindowSources()
        {
            var sources = new List<CaptureSource>();
            if (!OperatingSystem.IsWindows()) return sources;

            try
            {
                uint currentProcessId = (uint)Process.GetCurrentProcess().Id;
                EnumWindows((hWnd, _) =>
                {
                    if (TryCreateWindowSource(hWnd, currentProcessId, out var source) && source != null)
                    {
                        sources.Add(source);
                    }
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

            if (pid == 0) return 0;

            uint rootPid = ResolveEffectiveWindowProcess(hWnd, pid, out string procName);
            if (rootPid > 0) return (int)rootPid;

            int audioSessionPid = FindAudioSessionProcessId(procName, (int)pid);
            if (audioSessionPid > 0)
            {
                Console.WriteLine($"[CaptureDiscovery] Sesión de audio identificada para '{procName}': PID {audioSessionPid}");
                return audioSessionPid;
            }

            return (int)pid;
        }

        private static uint FindUwpChildProcess(IntPtr hWnd, uint initialPid)
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
            return realChildPid;
        }

        private static uint ResolveUwpChildProcess(IntPtr hWnd, uint initialPid, ref string procName)
        {
            if (hWnd == IntPtr.Zero || !procName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
                return initialPid;

            uint childPid = FindUwpChildProcess(hWnd, initialPid);
            if (childPid == 0) return initialPid;

            procName = GetProcessNameSafe(childPid);
            return childPid;
        }

        private static uint ResolveRootProcessIfApplicable(uint pid, string procName)
        {
            if (string.IsNullOrEmpty(procName) || procName.Equals("Aplicación", StringComparison.OrdinalIgnoreCase))
                return pid;

            uint rootPid = FindApplicationRootProcessId(pid, procName);
            return rootPid > 0 ? rootPid : pid;
        }

        private static uint ResolveEffectiveWindowProcess(IntPtr hWnd, uint initialPid, out string procName)
        {
            procName = GetProcessNameSafe(initialPid);
            uint pid = ResolveUwpChildProcess(hWnd, initialPid, ref procName);
            return ResolveRootProcessIfApplicable(pid, procName);
        }

        public static int FindAudioSessionProcessId(string procName, int fallbackPid)
        {
            if (!OperatingSystem.IsWindows())
                return fallbackPid;

            if (string.IsNullOrEmpty(procName) || procName.Equals("Aplicación", StringComparison.OrdinalIgnoreCase))
                return fallbackPid;

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var mgr = device.AudioSessionManager;
                var sessions = mgr.Sessions;

                int detectedPid = ScanSessionsForProcess(sessions, procName);
                if (detectedPid > 0)
                    return detectedPid;
            }
            catch
            {
                // Dispositivo o driver no expone IAudioSessionManager2 o sin sesiones activas
            }

            return fallbackPid;
        }

        [SupportedOSPlatform("windows")]
        private static int ScanSessionsForProcess(SessionCollection sessions, string procName)
        {
            int matchingPid = 0;
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                uint sPid = s.GetProcessID;
                if (sPid == 0) continue;

                string sName = GetProcessNameSafe(sPid);
                if (!sName.Equals(procName, StringComparison.OrdinalIgnoreCase)) continue;

                if (s.State == AudioSessionState.AudioSessionStateActive)
                {
                    return (int)sPid;
                }
                matchingPid = (int)sPid;
            }
            return matchingPid;
        }

        private static (Dictionary<uint, uint> ParentMap, Dictionary<uint, string> NameMap) ReadProcessHierarchy(IntPtr snapshot)
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
                    {
                        exeName = exeName.Substring(0, dotIdx);
                    }
                    nameMap[pe.th32ProcessID] = exeName;
                } while (Process32Next(snapshot, ref pe));
            }

            return (parentMap, nameMap);
        }

        private static uint FindRootInProcessHierarchy(
            uint startPid,
            string targetName,
            Dictionary<uint, uint> parentMap,
            Dictionary<uint, string> nameMap)
        {
            uint current = startPid;
            for (int depth = 0; depth < 10; depth++)
            {
                if (!parentMap.TryGetValue(current, out uint parentPid)) break;
                if (parentPid == 0 || parentPid == current) break;

                if (nameMap.TryGetValue(parentPid, out string? parentName) &&
                    parentName.Equals(targetName, StringComparison.OrdinalIgnoreCase))
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

        public static uint FindApplicationRootProcessId(uint pid, string procName)
        {
            if (pid == 0) return 0;

            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return pid;

            try
            {
                var (parentMap, nameMap) = ReadProcessHierarchy(snapshot);
                return FindRootInProcessHierarchy(pid, procName, parentMap, nameMap);
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
            if (pid == 0) return "Aplicación";

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
