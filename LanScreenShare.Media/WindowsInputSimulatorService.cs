using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Media
{
    [SupportedOSPlatform("windows")]
    public class WindowsInputSimulatorService : IInputSimulatorService
    {
        private bool _disposed;

        public void SimulateInput(RemoteInputMessage input)
        {
            try
            {
                switch (input.Type)
                {
                    case InputEventType.MouseMove:
                        SendMouseMove(input.NormalizedX, input.NormalizedY);
                        break;

                    case InputEventType.MouseDown:
                        SendMouseButton(input.Button, isDown: true, input.NormalizedX, input.NormalizedY);
                        break;

                    case InputEventType.MouseUp:
                        SendMouseButton(input.Button, isDown: false, input.NormalizedX, input.NormalizedY);
                        break;

                    case InputEventType.MouseWheel:
                        SendMouseWheel(input.WheelDelta, input.NormalizedX, input.NormalizedY);
                        break;

                    case InputEventType.KeyDown:
                        SendKeyboard(input.KeyCode, isDown: true);
                        break;

                    case InputEventType.KeyUp:
                        SendKeyboard(input.KeyCode, isDown: false);
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[InputSimulator] Error simulando entrada: {ex.Message}");
            }
        }

        private static void SendMouseMove(float normX, float normY)
        {
            var pInputs = new INPUT[1];
            pInputs[0].type = INPUT_MOUSE;
            pInputs[0].u.mi = new MOUSEINPUT
            {
                dx = (int)Math.Round(Math.Clamp(normX, 0f, 1f) * 65535),
                dy = (int)Math.Round(Math.Clamp(normY, 0f, 1f) * 65535),
                dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                mouseData = 0,
                time = 0,
                dwExtraInfo = IntPtr.Zero
            };

            SendInput(1, pInputs, Marshal.SizeOf<INPUT>());
        }

        private static void SendMouseButton(RemoteMouseButton button, bool isDown, float normX, float normY)
        {
            uint flag = 0;
            switch (button)
            {
                case RemoteMouseButton.Left:
                    flag = isDown ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP;
                    break;
                case RemoteMouseButton.Right:
                    flag = isDown ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP;
                    break;
                case RemoteMouseButton.Middle:
                    flag = isDown ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP;
                    break;
                default:
                    return;
            }

            var pInputs = new INPUT[1];
            pInputs[0].type = INPUT_MOUSE;
            pInputs[0].u.mi = new MOUSEINPUT
            {
                dx = (int)Math.Round(Math.Clamp(normX, 0f, 1f) * 65535),
                dy = (int)Math.Round(Math.Clamp(normY, 0f, 1f) * 65535),
                dwFlags = flag | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                mouseData = 0,
                time = 0,
                dwExtraInfo = IntPtr.Zero
            };

            SendInput(1, pInputs, Marshal.SizeOf<INPUT>());
        }

        private static void SendMouseWheel(short delta, float normX, float normY)
        {
            var pInputs = new INPUT[1];
            pInputs[0].type = INPUT_MOUSE;
            pInputs[0].u.mi = new MOUSEINPUT
            {
                dx = (int)Math.Round(Math.Clamp(normX, 0f, 1f) * 65535),
                dy = (int)Math.Round(Math.Clamp(normY, 0f, 1f) * 65535),
                dwFlags = MOUSEEVENTF_WHEEL | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                mouseData = (uint)delta,
                time = 0,
                dwExtraInfo = IntPtr.Zero
            };

            SendInput(1, pInputs, Marshal.SizeOf<INPUT>());
        }

        private static void SendKeyboard(int keyCode, bool isDown)
        {
            if (keyCode <= 0 || keyCode > 0xFFFF)
                return;

            var pInputs = new INPUT[1];
            pInputs[0].type = INPUT_KEYBOARD;
            pInputs[0].u.ki = new KEYBDINPUT
            {
                wVk = (ushort)keyCode,
                wScan = 0,
                dwFlags = isDown ? 0 : KEYEVENTF_KEYUP,
                time = 0,
                dwExtraInfo = IntPtr.Zero
            };

            SendInput(1, pInputs, Marshal.SizeOf<INPUT>());
        }

        #region Win32 P/Invoke

        private const uint INPUT_MOUSE = 0;
        private const uint INPUT_KEYBOARD = 1;

        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;
        private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
        private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

        private const uint KEYEVENTF_KEYUP = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            public MOUSEINPUT mi;
            [FieldOffset(0)]
            public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

        #endregion

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
