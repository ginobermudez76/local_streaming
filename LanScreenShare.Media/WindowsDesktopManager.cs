using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace LanScreenShare.Media
{
    /// <summary>
    /// Administra la detección y conmutación de escritorios Win32 (Default vs Winlogon / Escritorio Seguro).
    /// Especificación: ADR-002.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class WindowsDesktopManager
    {
        public const string DesktopDefault = "Default";
        public const string DesktopWinlogon = "Winlogon";
        public const string DesktopScreenSaver = "Screen-saver";

        private const uint DESKTOP_READOBJECTS = 0x0001;
        private const uint DESKTOP_SWITCHDESKTOP = 0x0100;
        private const uint DESKTOP_ALL_ACCESS = 0x01FF;

        private const int UOI_NAME = 2;
        private const int ERROR_ACCESS_DENIED = 5;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseDesktop(IntPtr hDesktop);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetThreadDesktop(IntPtr hDesktop);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetThreadDesktop(uint dwThreadId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetUserObjectInformation(
            IntPtr hObj,
            int nIndex,
            [Out] byte[] pvInfo,
            uint nLength,
            out uint lpnLengthNeeded);

        public static string? GetDesktopName(IntPtr hDesktop)
        {
            if (hDesktop == IntPtr.Zero)
                return null;

            GetUserObjectInformation(hDesktop, UOI_NAME, Array.Empty<byte>(), 0, out uint needed);
            if (needed == 0)
                return null;

            byte[] buffer = new byte[needed];
            if (!GetUserObjectInformation(hDesktop, UOI_NAME, buffer, needed, out _))
                return null;

            return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
        }

        public static bool IsSecureDesktopName(string name) =>
            string.Equals(name, DesktopWinlogon, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, DesktopScreenSaver, StringComparison.OrdinalIgnoreCase);

        public static bool IsActiveDesktopSecure(out string desktopName)
        {
            if (!OperatingSystem.IsWindows())
            {
                desktopName = DesktopDefault;
                return false;
            }

            IntPtr hDesktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS | DESKTOP_SWITCHDESKTOP);
            if (hDesktop == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_ACCESS_DENIED)
                {
                    desktopName = DesktopWinlogon;
                    return true;
                }

                desktopName = "Unknown";
                return false;
            }

            try
            {
                string? name = GetDesktopName(hDesktop);
                desktopName = name ?? DesktopDefault;
                return IsSecureDesktopName(desktopName);
            }
            finally
            {
                CloseDesktop(hDesktop);
            }
        }

        public static bool TryAttachThreadToInputDesktop(out string desktopName, out bool isSecureDesktop)
        {
            if (!OperatingSystem.IsWindows())
            {
                desktopName = DesktopDefault;
                isSecureDesktop = false;
                return false;
            }

            IntPtr hDesktop = OpenInputDesktop(0, false, DESKTOP_ALL_ACCESS);
            if (hDesktop == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_ACCESS_DENIED)
                {
                    desktopName = DesktopWinlogon;
                    isSecureDesktop = true;
                    return false;
                }

                desktopName = "Unknown";
                isSecureDesktop = false;
                return false;
            }

            try
            {
                string? name = GetDesktopName(hDesktop);
                desktopName = name ?? DesktopDefault;
                isSecureDesktop = IsSecureDesktopName(desktopName);

                IntPtr hThreadDesktop = GetThreadDesktop(GetCurrentThreadId());
                string? currentThreadDesktopName = GetDesktopName(hThreadDesktop);
                if (string.Equals(desktopName, currentThreadDesktopName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return SetThreadDesktop(hDesktop);
            }
            finally
            {
                CloseDesktop(hDesktop);
            }
        }
    }
}
