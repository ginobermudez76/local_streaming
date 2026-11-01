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

        private static void SendMouseMove(float normX, float normY)
        {
            int screenW = GetSystemMetrics(SM_CXSCREEN);
            int screenH = GetSystemMetrics(SM_CYSCREEN);

            int targetX = (int)Math.Round(Math.Clamp(normX, 0f, 1f) * (screenW - 1));
            int targetY = (int)Math.Round(Math.Clamp(normY, 0f, 1f) * (screenH - 1));

            SetCursorPos(targetX, targetY);
        }

        private static void SendMouseMoveRelative(int dx, int dy)
        {
            if (dx == 0 && dy == 0)
                return;

            mouse_event(MOUSEEVENTF_MOVE, (uint)dx, (uint)dy, 0, UIntPtr.Zero);
        }

        private static void SendMouseButton(RemoteMouseButton button, bool isDown, float normX, float normY)
        {
            if (normX >= 0f && normY >= 0f)
            {
                SendMouseMove(normX, normY);
            }

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

            mouse_event(flag, 0, 0, 0, UIntPtr.Zero);
        }

        private static void SendMouseWheel(short delta, float normX, float normY)
        {
            if (normX >= 0f && normY >= 0f)
            {
                SendMouseMove(normX, normY);
            }
            mouse_event(MOUSEEVENTF_WHEEL, 0, 0, (uint)delta, UIntPtr.Zero);
        }

        private static void SendKeyboard(int keyCode, bool isDown)
        {
            if (keyCode <= 0 || keyCode > 0xFF)
                return;

            uint flags = isDown ? 0 : KEYEVENTF_KEYUP;
            keybd_event((byte)keyCode, 0, flags, UIntPtr.Zero);
        }

        #region Win32 P/Invoke

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        private const uint MOUSEEVENTF_MOVE = 0x0001;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const uint MOUSEEVENTF_WHEEL = 0x0800;

        private const uint KEYEVENTF_KEYUP = 0x0002;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

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
