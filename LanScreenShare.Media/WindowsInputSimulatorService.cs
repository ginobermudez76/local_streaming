using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;
using LanScreenShare.Core.Security;

namespace LanScreenShare.Media
{
    [SupportedOSPlatform("windows")]
    public class WindowsInputSimulatorService : IInputSimulatorService
    {
        private readonly object _inputLock = new();
        private readonly InjectedInputTracker _tracker = new();
        private bool _disposed;
        private CaptureSource? _currentTargetSource;

        public InjectedInputTracker Tracker => _tracker;

        public void SetTargetSource(CaptureSource source)
        {
            lock (_inputLock)
            {
                _currentTargetSource = source;
            }
        }

        private bool IsTargetWindowValid =>
            _currentTargetSource != null &&
            _currentTargetSource.Type == CaptureSourceType.Window &&
            _currentTargetSource.Handle != IntPtr.Zero;

        private bool IsTargetCustomBoundsValid =>
            _currentTargetSource != null &&
            _currentTargetSource.Width > 0 &&
            _currentTargetSource.Height > 0;

        private static INPUT CreateMouseInput(int dx, int dy, uint flags, uint mouseData = 0) => new()
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = dx,
                    dy = dy,
                    mouseData = mouseData,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };

        private static uint SendSingleMouseInput(int dx, int dy, uint flags, uint mouseData = 0)
        {
            var input = new INPUT[] { CreateMouseInput(dx, dy, flags, mouseData) };
            return SendInput(1, input, Marshal.SizeOf<INPUT>());
        }

        private static INPUT CreateKeyboardInput(int vk, uint flags) => new()
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)vk,
                    wScan = 0,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };

        private static INPUT CreateKeyReleaseInput(int vk) =>
            CreateKeyboardInput(vk, KEYEVENTF_KEYUP);

        private static uint GetMouseButtonUpFlag(RemoteMouseButton button) => button switch
        {
            RemoteMouseButton.Left => MOUSEEVENTF_LEFTUP,
            RemoteMouseButton.Right => MOUSEEVENTF_RIGHTUP,
            RemoteMouseButton.Middle => MOUSEEVENTF_MIDDLEUP,
            _ => 0
        };

        private static INPUT CreateMouseButtonReleaseInput(uint flag) =>
            CreateMouseInput(0, 0, flag);

        private static uint GetMouseButtonFlag(RemoteMouseButton button, bool isDown) => (button, isDown) switch
        {
            (RemoteMouseButton.Left, true) => MOUSEEVENTF_LEFTDOWN,
            (RemoteMouseButton.Left, false) => MOUSEEVENTF_LEFTUP,
            (RemoteMouseButton.Right, true) => MOUSEEVENTF_RIGHTDOWN,
            (RemoteMouseButton.Right, false) => MOUSEEVENTF_RIGHTUP,
            (RemoteMouseButton.Middle, true) => MOUSEEVENTF_MIDDLEDOWN,
            (RemoteMouseButton.Middle, false) => MOUSEEVENTF_MIDDLEUP,
            _ => 0
        };

        private void DispatchInputEvent(in RemoteInputMessage input)
        {
            switch (input.Type)
            {
                case InputEventType.MouseMove:
                    SendMouseMove(input.NormalizedX, input.NormalizedY);
                    break;

                case InputEventType.MouseMoveRelative:
                    SendMouseMoveRelative((int)Math.Round(input.NormalizedX), (int)Math.Round(input.NormalizedY));
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

        public void SimulateInput(RemoteInputMessage input)
        {
            if (_disposed) return;

            lock (_inputLock)
            {
                try
                {
                    DispatchInputEvent(input);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[InputSimulator] Error simulando entrada: {ex.Message}");
                }
            }
        }

        private static int PopulateReleaseInputs(INPUT[] inputs, int[] keys, RemoteMouseButton[] buttons)
        {
            int idx = 0;
            foreach (int vk in keys)
            {
                inputs[idx++] = CreateKeyReleaseInput(vk);
            }

            foreach (var button in buttons)
            {
                uint flag = GetMouseButtonUpFlag(button);
                if (flag != 0)
                {
                    inputs[idx++] = CreateMouseButtonReleaseInput(flag);
                }
            }
            return idx;
        }

        public void ReleaseInjectedInputs()
        {
            lock (_inputLock)
            {
                var keys = _tracker.GetPressedKeysSnapshot();
                var buttons = _tracker.GetPressedMouseButtonsSnapshot();

                if (keys.Length == 0 && buttons.Length == 0)
                    return;

                var inputs = new INPUT[keys.Length + buttons.Length];
                int count = PopulateReleaseInputs(inputs, keys, buttons);

                if (count > 0)
                {
                    uint inserted = SendInput((uint)count, inputs, Marshal.SizeOf<INPUT>());
                    if (inserted < (uint)count)
                    {
                        Console.WriteLine($"[InputSimulator] Advertencia: Sólo se liberaron {inserted} de {count} eventos de limpieza.");
                    }
                }

                _tracker.Reset();
            }
        }

        private bool TrySendMouseMoveToWindow(float normX, float normY)
        {
            if (!IsTargetWindowValid) return false;
            IntPtr hwnd = _currentTargetSource!.Handle;
            if (!GetClientRect(hwnd, out RECT clientRect)) return false;

            POINT pt = new POINT { x = 0, y = 0 };
            ClientToScreen(hwnd, ref pt);
            int winW = clientRect.Right - clientRect.Left;
            int winH = clientRect.Bottom - clientRect.Top;
            int targetX = pt.x + (int)Math.Round(Math.Clamp(normX, 0f, 1f) * Math.Max(1, winW - 1));
            int targetY = pt.y + (int)Math.Round(Math.Clamp(normY, 0f, 1f) * Math.Max(1, winH - 1));
            SetCursorPos(targetX, targetY);
            return true;
        }

        private bool TrySendMouseMoveToBounds(float normX, float normY)
        {
            if (!IsTargetCustomBoundsValid) return false;
            int targetX = _currentTargetSource!.X + (int)Math.Round(Math.Clamp(normX, 0f, 1f) * (_currentTargetSource.Width - 1));
            int targetY = _currentTargetSource.Y + (int)Math.Round(Math.Clamp(normY, 0f, 1f) * (_currentTargetSource.Height - 1));
            SetCursorPos(targetX, targetY);
            return true;
        }

        private static void SendMouseMoveToScreen(float normX, float normY)
        {
            int screenW = GetSystemMetrics(SM_CXSCREEN);
            int screenH = GetSystemMetrics(SM_CYSCREEN);
            int targetX = (int)Math.Round(Math.Clamp(normX, 0f, 1f) * (screenW - 1));
            int targetY = (int)Math.Round(Math.Clamp(normY, 0f, 1f) * (screenH - 1));
            SetCursorPos(targetX, targetY);
        }

        private void SendMouseMove(float normX, float normY)
        {
            if (TrySendMouseMoveToWindow(normX, normY) || TrySendMouseMoveToBounds(normX, normY))
            {
                return;
            }

            SendMouseMoveToScreen(normX, normY);
        }

        private static void SendMouseMoveRelative(int dx, int dy)
        {
            if (dx == 0 && dy == 0) return;
            SendSingleMouseInput(dx, dy, MOUSEEVENTF_MOVE);
        }

        private void SendMouseButton(RemoteMouseButton button, bool isDown, float normX, float normY)
        {
            if (normX >= 0f && normY >= 0f)
            {
                SendMouseMove(normX, normY);
            }

            if (isDown && IsTargetWindowValid)
            {
                SetForegroundWindow(_currentTargetSource!.Handle);
            }

            uint flag = GetMouseButtonFlag(button, isDown);
            if (flag == 0) return;

            uint inserted = SendSingleMouseInput(0, 0, flag);
            if (inserted > 0)
            {
                _tracker.RecordMouseButton(button, isDown);
            }
        }

        private void SendMouseWheel(short delta, float normX, float normY)
        {
            if (normX >= 0f && normY >= 0f)
            {
                SendMouseMove(normX, normY);
            }

            SendSingleMouseInput(0, 0, MOUSEEVENTF_WHEEL, (uint)delta);
        }

        private void SendKeyboard(int keyCode, bool isDown)
        {
            if (keyCode <= 0 || keyCode > 0xFF)
                return;

            uint flags = isDown ? 0 : KEYEVENTF_KEYUP;
            var input = new INPUT[] { CreateKeyboardInput(keyCode, flags) };
            uint inserted = SendInput(1, input, Marshal.SizeOf<INPUT>());
            if (inserted > 0)
            {
                _tracker.RecordKey(keyCode, isDown);
            }
        }

        #region Win32 P/Invoke & Structures

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

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

        private const uint KEYEVENTF_KEYUP = 0x0002;

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
        private struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        #endregion

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            ReleaseInjectedInputs();
            GC.SuppressFinalize(this);
        }
    }
}
