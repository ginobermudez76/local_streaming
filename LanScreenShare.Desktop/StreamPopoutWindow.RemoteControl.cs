using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Desktop
{
    public partial class StreamPopoutWindow
    {
        private readonly record struct RenderMetrics(Size ImageSize, Rect ControlBounds);

        private bool TryGetNormalizedCoordinates(PointerEventArgs e, out float normX, out float normY)
        {
            normX = 0f;
            normY = 0f;

            var screenImage = this.FindControl<Image>("ScreenImage");
            if (screenImage?.Source == null) return false;

            var imageSize = screenImage.Source.Size;
            if (imageSize.Width <= 0 || imageSize.Height <= 0) return false;

            var controlBounds = screenImage.Bounds;
            if (controlBounds.Width <= 0 || controlBounds.Height <= 0) return false;

            if (screenImage.Stretch == Avalonia.Media.Stretch.Fill)
            {
                var pos = e.GetPosition(screenImage);
                normX = (float)Math.Clamp(pos.X / controlBounds.Width, 0.0, 1.0);
                normY = (float)Math.Clamp(pos.Y / controlBounds.Height, 0.0, 1.0);
                return true;
            }

            return TryCalculateAspectCoordinates(e.GetPosition(screenImage), new RenderMetrics(imageSize, controlBounds), out normX, out normY);
        }

        private static bool TryCalculateAspectCoordinates(
            Point p,
            RenderMetrics metrics,
            out float normX,
            out float normY)
        {
            double scale = Math.Min(metrics.ControlBounds.Width / metrics.ImageSize.Width, metrics.ControlBounds.Height / metrics.ImageSize.Height);
            double renderedW = metrics.ImageSize.Width * scale;
            double renderedH = metrics.ImageSize.Height * scale;

            double offsetX = (metrics.ControlBounds.Width - renderedW) / 2.0;
            double offsetY = (metrics.ControlBounds.Height - renderedH) / 2.0;

            if (IsOutsideBounds(p.X, offsetX, renderedW) || IsOutsideBounds(p.Y, offsetY, renderedH))
            {
                normX = 0f;
                normY = 0f;
                return false;
            }

            normX = (float)Math.Clamp((p.X - offsetX) / renderedW, 0.0, 1.0);
            normY = (float)Math.Clamp((p.Y - offsetY) / renderedH, 0.0, 1.0);
            return true;
        }

        private static bool IsOutsideBounds(double pos, double offset, double size) =>
            pos < offset || pos > offset + size;

        private static bool HasValidBounds(Control? c) =>
            c != null && c.Bounds.Width > 0 && c.Bounds.Height > 0;

        private PixelPoint GetScreenImageCenterOnScreen()
        {
            var screenImage = this.FindControl<Image>("ScreenImage");
            if (HasValidBounds(screenImage))
            {
                int imgCenterX = this.Position.X + (int)Math.Round(screenImage!.Bounds.X + (screenImage.Bounds.Width / 2.0));
                int imgCenterY = this.Position.Y + (int)Math.Round(screenImage.Bounds.Y + (screenImage.Bounds.Height / 2.0));
                return new PixelPoint(imgCenterX, imgCenterY);
            }

            int winCenterX = this.Position.X + (int)Math.Round(this.Bounds.Width / 2.0);
            int winCenterY = this.Position.Y + (int)Math.Round(this.Bounds.Height / 2.0);
            return new PixelPoint(winCenterX, winCenterY);
        }

        private void HandleGameModePointerMove()
        {
            if (_isRecenteringMouse)
            {
                _isRecenteringMouse = false;
                return;
            }

            if (!OperatingSystem.IsWindows()) return;

            var center = GetScreenImageCenterOnScreen();
            if (GetCursorPos(out POINT curPos))
            {
                int dx = curPos.X - center.X;
                int dy = curPos.Y - center.Y;

                if (dx != 0 || dy != 0)
                {
                    RemoteInputSent?.Invoke(new RemoteInputMessage
                    {
                        Type = InputEventType.MouseMoveRelative,
                        NormalizedX = dx,
                        NormalizedY = dy
                    });

                    _isRecenteringMouse = true;
                    SetCursorPos(center.X, center.Y);
                }
            }
        }

        private void ScreenImage_PointerMoved(object? sender, PointerEventArgs e)
        {
            if (!_isRemoteControlEnabled || IsPointerOverHeader(e)) return;

            if (_isGameMode)
            {
                HandleGameModePointerMove();
                return;
            }

            if (TryGetNormalizedCoordinates(e, out float x, out float y))
            {
                RemoteInputSent?.Invoke(new RemoteInputMessage
                {
                    Type = InputEventType.MouseMove,
                    NormalizedX = x,
                    NormalizedY = y
                });
            }
        }

        private static RemoteMouseButton GetPressedMouseButton(PointerPointProperties props)
        {
            if (props.IsLeftButtonPressed) return RemoteMouseButton.Left;
            if (props.IsRightButtonPressed) return RemoteMouseButton.Right;
            if (props.IsMiddleButtonPressed) return RemoteMouseButton.Middle;
            return RemoteMouseButton.None;
        }

        private void SendMouseInput(
            PointerEventArgs e,
            InputEventType type,
            RemoteMouseButton button = RemoteMouseButton.None,
            short wheelDelta = 0)
        {
            if (_isGameMode)
            {
                RemoteInputSent?.Invoke(new RemoteInputMessage
                {
                    Type = type,
                    Button = button,
                    WheelDelta = wheelDelta,
                    NormalizedX = -1f,
                    NormalizedY = -1f
                });
                e.Handled = true;
                return;
            }

            if (TryGetNormalizedCoordinates(e, out float x, out float y))
            {
                RemoteInputSent?.Invoke(new RemoteInputMessage
                {
                    Type = type,
                    Button = button,
                    WheelDelta = wheelDelta,
                    NormalizedX = x,
                    NormalizedY = y
                });
                e.Handled = true;
            }
        }

        private void ScreenImage_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!_isRemoteControlEnabled || IsPointerOverHeader(e)) return;

            var screenImage = this.FindControl<Image>("ScreenImage");
            screenImage?.Focus();

            var btn = GetPressedMouseButton(e.GetCurrentPoint(screenImage).Properties);
            if (btn != RemoteMouseButton.None)
            {
                SendMouseInput(e, InputEventType.MouseDown, btn);
            }
        }

        private static RemoteMouseButton GetReleasedMouseButton(PointerPointProperties props) =>
            props.PointerUpdateKind switch
            {
                PointerUpdateKind.RightButtonReleased => RemoteMouseButton.Right,
                PointerUpdateKind.MiddleButtonReleased => RemoteMouseButton.Middle,
                _ => RemoteMouseButton.Left
            };

        private void ScreenImage_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_isRemoteControlEnabled || IsPointerOverHeader(e)) return;

            var screenImage = this.FindControl<Image>("ScreenImage");
            var btn = GetReleasedMouseButton(e.GetCurrentPoint(screenImage).Properties);
            SendMouseInput(e, InputEventType.MouseUp, btn);
        }

        private void ScreenImage_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            if (!_isRemoteControlEnabled || IsPointerOverHeader(e)) return;

            short delta = (short)(e.Delta.Y * 120);
            SendMouseInput(e, InputEventType.MouseWheel, wheelDelta: delta);
        }

        private void SendKeyInput(KeyEventArgs e, InputEventType type)
        {
            RemoteInputSent?.Invoke(new RemoteInputMessage
            {
                Type = type,
                KeyCode = (int)e.Key
            });
            e.Handled = true;
        }

        private void ScreenImage_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.F11)
            {
                ToggleFullscreen();
                e.Handled = true;
                return;
            }

            if (!_isRemoteControlEnabled) return;

            SendKeyInput(e, InputEventType.KeyDown);
        }

        private void ScreenImage_KeyUp(object? sender, KeyEventArgs e)
        {
            if (!_isRemoteControlEnabled) return;

            SendKeyInput(e, InputEventType.KeyUp);
        }

        private bool HandlePopoutKeyShortcuts(KeyEventArgs e)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                if (e.Key == Key.L) { GameModeToggled?.Invoke(); return true; }
                if (e.Key == Key.M) { CycleMonitorRequested?.Invoke(); return true; }
            }

            if (e.Key == Key.F11) { ToggleFullscreen(); return true; }
            if (e.Key == Key.Escape && _isFullscreen) { SetFullscreen(false); return true; }

            return false;
        }

        private void PopoutWindow_KeyDown(object? sender, KeyEventArgs e)
        {
            if (HandlePopoutKeyShortcuts(e))
            {
                e.Handled = true;
            }
        }

        #region Win32 P/Invoke

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        #endregion
    }
}
