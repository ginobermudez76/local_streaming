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

        public void SimulateInput(RemoteInputMessage input)
        {
            if (_disposed) return;

            lock (_inputLock)
            {
                try
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
                catch (Exception ex)
                {
                    Console.WriteLine($"[InputSimulator] Error simulando entrada: {ex.Message}");
                }
            }
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
                int idx = 0;

                // 1. Liberar teclas registradas exclusivamente (KEYUP)
                foreach (int vk in keys)
                {
                    inputs[idx++] = new INPUT
                    {
                        type = INPUT_KEYBOARD,
                        u = new InputUnion
                        {
                            ki = new KEYBDINPUT
                            {
                                wVk = (ushort)vk,
                                wScan = 0,
                                dwFlags = KEYEVENTF_KEYUP,
                                time = 0,
                                dwExtraInfo = UIntPtr.Zero
                            }
                        }
                    };
                }

                // 2. Liberar botones de ratón registrados exclusivamente (UP)
                foreach (var button in buttons)
                {
                    uint flag = button switch
                    {
                        RemoteMouseButton.Left => MOUSEEVENTF_LEFTUP,
                        RemoteMouseButton.Right => MOUSEEVENTF_RIGHTUP,
                        RemoteMouseButton.Middle => MOUSEEVENTF_MIDDLEUP,
                        _ => 0
                    };

                    if (flag != 0)
                    {
                        inputs[idx++] = new INPUT
                        {
                            type = INPUT_MOUSE,
                            u = new InputUnion
                            {
                                mi = new MOUSEINPUT
                                {
                                    dx = 0,
                                    dy = 0,
                                    mouseData = 0,
                                    dwFlags = flag,
                                    time = 0,
                                    dwExtraInfo = UIntPtr.Zero
                                }
                            }
                        };
                    }
                }

                if (idx > 0)
                {
                    uint inserted = SendInput((uint)idx, inputs, Marshal.SizeOf<INPUT>());
                    if (inserted < (uint)idx)
                    {
                        Console.WriteLine($"[InputSimulator] Advertencia: Sólo se liberaron {inserted} de {idx} eventos de limpieza.");
                    }
                }

                _tracker.Reset();
            }
        }

        private void SendMouseMove(float normX, float normY)
        {
            int targetX;
            int targetY;

            if (_currentTargetSource != null && _currentTargetSource.Type == CaptureSourceType.Window && _currentTargetSource.Handle != IntPtr.Zero)
            {
                if (GetClientRect(_currentTargetSource.Handle, out RECT clientRect))
                {
                    POINT pt = new POINT { x = 0, y = 0 };
                    ClientToScreen(_currentTargetSource.Handle, ref pt);
                    int winW = clientRect.Right - clientRect.Left;
                    int winH = clientRect.Bottom - clientRect.Top;
                    targetX = pt.x + (int)Math.Round(Math.Clamp(normX, 0f, 1f) * Math.Max(1, winW - 1));
                    targetY = pt.y + (int)Math.Round(Math.Clamp(normY, 0f, 1f) * Math.Max(1, winH - 1));
                    SetCursorPos(targetX, targetY);
                    return;
                }
            }
            else if (_currentTargetSource != null && _currentTargetSource.Width > 0 && _currentTargetSource.Height > 0)
            {
                targetX = _currentTargetSource.X + (int)Math.Round(Math.Clamp(normX, 0f, 1f) * (_currentTargetSource.Width - 1));
                targetY = _currentTargetSource.Y + (int)Math.Round(Math.Clamp(normY, 0f, 1f) * (_currentTargetSource.Height - 1));
                SetCursorPos(targetX, targetY);
                return;
            }

            int screenW = GetSystemMetrics(SM_CXSCREEN);
            int screenH = GetSystemMetrics(SM_CYSCREEN);

            targetX = (int)Math.Round(Math.Clamp(normX, 0f, 1f) * (screenW - 1));
            targetY = (int)Math.Round(Math.Clamp(normY, 0f, 1f) * (screenH - 1));

            SetCursorPos(targetX, targetY);
        }

        private static void SendMouseMoveRelative(int dx, int dy)
        {
            if (dx == 0 && dy == 0)
                return;

            var input = new INPUT[]
            {
                new()
                {
                    type = INPUT_MOUSE,
                    u = new InputUnion
                    {
                        mi = new MOUSEINPUT
                        {
                            dx = dx,
                            dy = dy,
                            mouseData = 0,
                            dwFlags = MOUSEEVENTF_MOVE,
                            time = 0,
                            dwExtraInfo = UIntPtr.Zero
                        }
                    }
                }
            };

            SendInput(1, input, Marshal.SizeOf<INPUT>());
        }

        private void SendMouseButton(RemoteMouseButton button, bool isDown, float normX, float normY)
        {
            if (normX >= 0f && normY >= 0f)
            {
                SendMouseMove(normX, normY);
            }

            if (isDown && _currentTargetSource?.Type == CaptureSourceType.Window && _currentTargetSource.Handle != IntPtr.Zero)
            {
                SetForegroundWindow(_currentTargetSource.Handle);
            }

            uint flag = button switch
            {
                RemoteMouseButton.Left => isDown ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
                RemoteMouseButton.Right => isDown ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
                RemoteMouseButton.Middle => isDown ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
                _ => 0
            };

            if (flag == 0) return;

            var input = new INPUT[]
            {
                new()
                {
                    type = INPUT_MOUSE,
                    u = new InputUnion
                    {
                        mi = new MOUSEINPUT
                        {
                            dx = 0,
                            dy = 0,
                            mouseData = 0,
                            dwFlags = flag,
                            time = 0,
                            dwExtraInfo = UIntPtr.Zero
                        }
                    }
                }
            };

            uint inserted = SendInput(1, input, Marshal.SizeOf<INPUT>());
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

            var input = new INPUT[]
            {
                new()
                {
                    type = INPUT_MOUSE,
                    u = new InputUnion
                    {
                        mi = new MOUSEINPUT
                        {
                            dx = 0,
                            dy = 0,
                            mouseData = (uint)delta,
                            dwFlags = MOUSEEVENTF_WHEEL,
                            time = 0,
                            dwExtraInfo = UIntPtr.Zero
                        }
                    }
                }
            };

            SendInput(1, input, Marshal.SizeOf<INPUT>());
        }

        private void SendKeyboard(int keyCode, bool isDown)
        {
            if (keyCode <= 0 || keyCode > 0xFF)
                return;

            uint flags = isDown ? 0 : KEYEVENTF_KEYUP;

            var input = new INPUT[]
            {
                new()
                {
                    type = INPUT_KEYBOARD,
                    u = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = (ushort)keyCode,
                            wScan = 0,
                            dwFlags = flags,
                            time = 0,
                            dwExtraInfo = UIntPtr.Zero
                        }
                    }
                }
            };

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
