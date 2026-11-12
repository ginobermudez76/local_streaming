using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using LanScreenShare.Core.Models;
using LanScreenShare.Network.Streaming;

namespace LanScreenShare.Desktop
{
    public partial class MainWindow
    {
        #region Remote Control & Game Mode

        private bool CanHostSyncClipboard() => _isHosting && _allowRemoteControl && _allowClipboardSync;
        private bool CanViewerSyncClipboard() => _isViewing && _remoteControlEnabled && _allowClipboardSync;

        private void UpdateHostControlServerState()
        {
            if (!_isHosting) return;
            if (_allowRemoteControl)
            {
                if (!_controlServer.IsRunning) _ = _controlServer.StartAsync(5003);
            }
            else
            {
                _controlServer.Stop();
            }
        }

        private void InitializeControlCheckboxes()
        {
            var allowCb = this.FindControl<CheckBox>("AllowRemoteControlCheckBox");
            var controlAccessPanel = this.FindControl<StackPanel>("ControlAccessModePanel");

            if (allowCb != null)
            {
                allowCb.IsChecked = _appSettings.AllowRemoteControl;
                _allowRemoteControl = _appSettings.AllowRemoteControl;
                if (controlAccessPanel != null) controlAccessPanel.IsVisible = _allowRemoteControl;

                allowCb.IsCheckedChanged += (s, e) =>
                {
                    _allowRemoteControl = allowCb.IsChecked == true;
                    if (controlAccessPanel != null) controlAccessPanel.IsVisible = _allowRemoteControl;
                    UpdateHostControlServerState();
                    Console.WriteLine($"[ControlHost] Permiso de control remoto: {_allowRemoteControl}");
                    SaveCurrentSettings();
                };
            }

            var clipboardSyncCb = this.FindControl<CheckBox>("ClipboardSyncCheckBox");
            if (clipboardSyncCb != null)
            {
                clipboardSyncCb.IsChecked = _appSettings.ClipboardSyncEnabled;
                _allowClipboardSync = _appSettings.ClipboardSyncEnabled;
                clipboardSyncCb.IsCheckedChanged += (s, e) =>
                {
                    bool enabled = clipboardSyncCb.IsChecked == true;
                    _allowClipboardSync = enabled;
                    _appSettings.ClipboardSyncEnabled = enabled;
                    SaveCurrentSettings();
                };
            }
        }

        private bool CanHostHandleRemoteControl => _isHosting && _allowRemoteControl;

        private void WireControlServerEvents()
        {
            _controlServer.ClipboardTextReceived += (s, text) =>
            {
                if (CanHostSyncClipboard()) ApplyRemoteClipboardText(text);
            };
            _controlServer.ClientConnected += (s, id) => OnHostRemoteControlRequested();
            _controlServer.SourceCatalogRequested += (s, e) => OnHostRemoteControlRequested();
            _controlServer.SwitchMonitorRequested += (s, idx) =>
            {
                if (CanHostHandleRemoteControl) HandleRemoteMonitorSwitchOnHost(idx);
            };
            _controlServer.SelectSourceRequested += (s, srcId) =>
            {
                if (CanHostHandleRemoteControl) HandleRemoteSelectSourceOnHost(srcId);
            };
        }

        private void OnHostRemoteControlRequested()
        {
            if (CanHostHandleRemoteControl)
            {
                BroadcastHostSourceCatalog();
            }
        }

        private void WireControlClientEvents()
        {
            _controlClient.ClipboardTextReceived += (s, text) =>
            {
                if (CanViewerSyncClipboard()) ApplyRemoteClipboardText(text);
            };
            _controlClient.MonitorInfoReceived += (s, monitorLabel) =>
            {
                if (_isViewing) ApplyRemoteMonitorInfo(monitorLabel);
            };
        }

        private void InitializeControlSubsystem()
        {
            _controlServer = new TcpControlServer(_sessionAuthority, _inputSimulatorService, _sslServerOptions);
            _controlClient = new TcpControlClient();
            _discoveryService.IsRemoteControlAllowedProvider = () => _allowRemoteControl;

            InitializeControlCheckboxes();
            WireControlServerEvents();
            WireControlClientEvents();
        }

        private readonly record struct ImageGeometry(Size ImageSize, Rect Bounds);

        private bool TryGetNormalizedCoordinates(PointerEventArgs e, out float normX, out float normY)
        {
            normX = 0f;
            normY = 0f;

            var screenImage = this.FindControl<Image>("ScreenImage");
            if (!TryGetValidImageGeometry(screenImage, out var geom))
                return false;

            var pos = e.GetPosition(screenImage);
            if (screenImage!.Stretch == Stretch.Fill)
            {
                normX = (float)Math.Clamp(pos.X / geom.Bounds.Width, 0.0, 1.0);
                normY = (float)Math.Clamp(pos.Y / geom.Bounds.Height, 0.0, 1.0);
                return true;
            }

            return TryCalculateUniformCoordinates(pos, geom, out normX, out normY);
        }

        private static bool HasPositiveDimensions(Size s) => s.Width > 0 && s.Height > 0;
        private static bool HasPositiveDimensions(Rect r) => r.Width > 0 && r.Height > 0;

        private static bool TryGetValidImageGeometry(Image? image, out ImageGeometry geom)
        {
            geom = default;
            if (image?.Source == null) return false;
            var size = image.Source.Size;
            var bounds = image.Bounds;
            if (!HasPositiveDimensions(size) || !HasPositiveDimensions(bounds))
                return false;

            geom = new ImageGeometry(size, bounds);
            return true;
        }

        private static bool TryCalculateUniformCoordinates(Point pos, in ImageGeometry geom, out float normX, out float normY)
        {
            normX = 0f;
            normY = 0f;
            double scale = Math.Min(geom.Bounds.Width / geom.ImageSize.Width, geom.Bounds.Height / geom.ImageSize.Height);
            double renderedW = geom.ImageSize.Width * scale;
            double renderedH = geom.ImageSize.Height * scale;
            double offsetX = (geom.Bounds.Width - renderedW) / 2.0;
            double offsetY = (geom.Bounds.Height - renderedH) / 2.0;

            if (IsOutsideBounds(pos.X, offsetX, renderedW) || IsOutsideBounds(pos.Y, offsetY, renderedH))
                return false;

            normX = (float)Math.Clamp((pos.X - offsetX) / renderedW, 0.0, 1.0);
            normY = (float)Math.Clamp((pos.Y - offsetY) / renderedH, 0.0, 1.0);
            return true;
        }

        private static bool IsOutsideBounds(double pos, double offset, double size) =>
            pos < offset || pos > offset + size;

        private void MainWindow_KeyDown(object? sender, KeyEventArgs e)
        {
            if (TryHandleShortcutKeys(e))
            {
                e.Handled = true;
            }
        }

        private bool TryHandleShortcutKeys(KeyEventArgs e)
        {
            bool hasCtrlAlt = e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            if (hasCtrlAlt)
            {
                if (e.Key == Key.L) { ToggleGameMode(); return true; }
                if (e.Key == Key.M) { _ = RequestRemoteMonitorSwitchAsync(); return true; }
            }

            if (e.Key == Key.F11) { ToggleFullscreen(); return true; }
            if (e.Key == Key.Escape && _isFullscreen) { SetFullscreen(false); return true; }

            return false;
        }

        private static bool IsShortcutKey(KeyEventArgs e)
        {
            bool hasCtrlAlt = e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            return hasCtrlAlt && (e.Key == Key.L || e.Key == Key.M);
        }

        private async Task<bool> EnsureControlConnectedAsync()
        {
            if (_controlClient.IsConnected)
                return true;

            if (string.IsNullOrEmpty(_currentConnectedDeviceIp))
                return false;

            UpdateStatus("Solicitando acceso de control remoto al anfitrión...", "#F59E0B");

            try
            {
                var handshake = (_streamClient as TcpStreamClient)?.ActiveSessionHandshake;
                await _controlClient.ConnectAsync(new ControlClientConnectionOptions(_currentConnectedDeviceIp, 5003, _sslClientOptions, handshake?.SessionId, handshake?.ControlToken));
                Console.WriteLine($"[ControlViewer] Conectado a canal de control remoto en {_currentConnectedDeviceIp}:5003");
                _remoteControlEnabled = true;

                var remoteControlBtn = this.FindControl<ToggleButton>("RemoteControlButton");
                if (remoteControlBtn != null && remoteControlBtn.IsChecked != true)
                {
                    remoteControlBtn.IsChecked = true;
                    remoteControlBtn.Background = new SolidColorBrush(Color.Parse("#2563EB"));
                }
                _popoutWindow?.SetRemoteControlState(true);
                _ = _controlClient.RequestSourceCatalogAsync();
                UpdateStatus($"Conectado a {_currentConnectedDeviceName} • Control remoto activo", "#22C55E");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ControlClient] Control remoto denegado o no disponible: {ex.Message}");
                string detail = ex.InnerException != null ? $"{ex.Message} ({ex.InnerException.Message})" : ex.Message;
                UpdateStatus($"Control remoto no disponible: {detail}", "#EF4444");
                return false;
            }
        }

        private async void ToggleGameMode()
        {
            if (!_isGameMode)
            {
                bool connected = await EnsureControlConnectedAsync();
                if (!connected)
                {
                    var gameModeBtn = this.FindControl<ToggleButton>("GameModeButton");
                    if (gameModeBtn != null) gameModeBtn.IsChecked = false;
                    return;
                }

                _isGameMode = true;
            }
            else
            {
                _isGameMode = false;
            }

            UpdateGameModeUI();
        }

        private void UpdateGameModeUI()
        {
            var gameModeBtn = this.FindControl<ToggleButton>("GameModeButton");
            if (gameModeBtn != null)
            {
                gameModeBtn.IsChecked = _isGameMode;
                gameModeBtn.Background = _isGameMode
                    ? new SolidColorBrush(Color.Parse("#2563EB"))
                    : new SolidColorBrush(Color.Parse("#27272A"));
            }

            if (_isGameMode)
            {
                ActivateGameModeUi();
            }
            else
            {
                DeactivateGameModeUi();
            }
        }

        private void ActivateGameModeUi()
        {
            _remoteControlEnabled = true;
            var remoteControlBtn = this.FindControl<ToggleButton>("RemoteControlButton");
            if (remoteControlBtn != null)
            {
                remoteControlBtn.IsChecked = true;
                remoteControlBtn.Background = new SolidColorBrush(Color.Parse("#2563EB"));
            }

            var screenImage = this.FindControl<Image>("ScreenImage");
            if (screenImage != null)
            {
                screenImage.Focus();
                screenImage.Cursor = new Cursor(StandardCursorType.None);
            }

            if (OperatingSystem.IsWindows())
            {
                var center = GetScreenImageCenterOnScreen();
                _isRecenteringMouse = true;
                SetCursorPos(center.X, center.Y);
            }

            _gameModeBannerTimer?.Stop();
            var banner = this.FindControl<Border>("GameModeBanner");
            var bannerText = this.FindControl<TextBlock>("GameModeBannerText");
            var bannerIcon = this.FindControl<PathIcon>("GameModeBannerIcon");
            if (banner != null && bannerText != null)
            {
                banner.IsVisible = true;
                if (bannerIcon != null)
                {
                    bannerIcon.Data = (this.FindResource("Icon3dRotation") as StreamGeometry)!;
                    bannerIcon.Foreground = new SolidColorBrush(Color.Parse("#38BDF8"));
                }
                bannerText.Text = "Modo Juego 3D Activo — Presiona Ctrl + Alt + L para liberar el mouse";
                bannerText.Foreground = new SolidColorBrush(Color.Parse("#38BDF8"));
            }
        }

        private void DeactivateGameModeUi()
        {
            var screenImage = this.FindControl<Image>("ScreenImage");
            if (screenImage != null)
            {
                screenImage.Cursor = Cursor.Default;
            }

            var banner = this.FindControl<Border>("GameModeBanner");
            var bannerText = this.FindControl<TextBlock>("GameModeBannerText");
            var bannerIcon = this.FindControl<PathIcon>("GameModeBannerIcon");
            if (banner != null && bannerText != null)
            {
                banner.IsVisible = true;
                if (bannerIcon != null)
                {
                    bannerIcon.Data = (this.FindResource("IconLockOpen") as StreamGeometry)!;
                    bannerIcon.Foreground = new SolidColorBrush(Color.Parse("#FBBF24"));
                }
                bannerText.Text = "Mouse Liberado — Presiona Ctrl + Alt + L para recapturar";
                bannerText.Foreground = new SolidColorBrush(Color.Parse("#FBBF24"));

                _gameModeBannerTimer?.Stop();
                _gameModeBannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
                _gameModeBannerTimer.Tick += (s, e) =>
                {
                    banner.IsVisible = false;
                    _gameModeBannerTimer?.Stop();
                };
                _gameModeBannerTimer.Start();
            }
        }

        private Avalonia.PixelPoint GetScreenImageCenterOnScreen()
        {
            var screenImage = this.FindControl<Image>("ScreenImage");
            if (screenImage != null && HasPositiveDimensions(screenImage.Bounds))
            {
                int imgCenterX = this.Position.X + (int)Math.Round(screenImage.Bounds.X + (screenImage.Bounds.Width / 2.0));
                int imgCenterY = this.Position.Y + (int)Math.Round(screenImage.Bounds.Y + (screenImage.Bounds.Height / 2.0));
                return new Avalonia.PixelPoint(imgCenterX, imgCenterY);
            }

            int winCenterX = this.Position.X + (int)Math.Round(this.Bounds.Width / 2.0);
            int winCenterY = this.Position.Y + (int)Math.Round(this.Bounds.Height / 2.0);
            return new Avalonia.PixelPoint(winCenterX, winCenterY);
        }

        private bool IsPointerOverFullscreenHeader(PointerEventArgs e)
        {
            if (!_isFullscreen) return false;
            var header = this.FindControl<Border>("StreamHeaderBar");
            if (header == null || !header.IsVisible) return false;
            var pos = e.GetPosition(this);
            double headerHeight = header.Bounds.Height > 0 ? header.Bounds.Height : 50;
            return pos.Y <= headerHeight;
        }

        private bool CanProcessPointerInput(PointerEventArgs e) =>
            _remoteControlEnabled && _controlClient.IsConnected && !IsPointerOverFullscreenHeader(e);

        private void ScreenImage_PointerMoved(object? sender, PointerEventArgs e)
        {
            if (!CanProcessPointerInput(e))
                return;

            if (_isGameMode)
            {
                HandleGameModePointerMoved();
                return;
            }

            if (TryGetNormalizedCoordinates(e, out float x, out float y))
            {
                _ = _controlClient.SendInputAsync(new RemoteInputMessage
                {
                    Type = InputEventType.MouseMove,
                    NormalizedX = x,
                    NormalizedY = y
                });
            }
        }

        private void HandleGameModePointerMoved()
        {
            if (_isRecenteringMouse)
            {
                _isRecenteringMouse = false;
                return;
            }

            if (!OperatingSystem.IsWindows())
                return;

            var center = GetScreenImageCenterOnScreen();
            if (GetCursorPos(out POINT curPos))
            {
                int dx = curPos.X - center.X;
                int dy = curPos.Y - center.Y;

                if (dx != 0 || dy != 0)
                {
                    _ = _controlClient.SendInputAsync(new RemoteInputMessage
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

        private void DispatchPointerButtonEvent(PointerEventArgs e, InputEventType eventType, RemoteMouseButton button)
        {
            if (_isGameMode)
            {
                _ = _controlClient.SendInputAsync(new RemoteInputMessage
                {
                    Type = eventType,
                    Button = button,
                    NormalizedX = -1f,
                    NormalizedY = -1f
                });
                e.Handled = true;
                return;
            }

            if (TryGetNormalizedCoordinates(e, out float x, out float y))
            {
                _ = _controlClient.SendInputAsync(new RemoteInputMessage
                {
                    Type = eventType,
                    Button = button,
                    NormalizedX = x,
                    NormalizedY = y
                });
                e.Handled = true;
            }
        }

        private void ScreenImage_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!CanProcessPointerInput(e))
                return;

            var screenImage = this.FindControl<Image>("ScreenImage");
            screenImage?.Focus();

            var props = e.GetCurrentPoint(screenImage).Properties;
            RemoteMouseButton button = props.IsLeftButtonPressed ? RemoteMouseButton.Left
                : props.IsRightButtonPressed ? RemoteMouseButton.Right
                : props.IsMiddleButtonPressed ? RemoteMouseButton.Middle
                : RemoteMouseButton.None;

            if (button != RemoteMouseButton.None)
            {
                DispatchPointerButtonEvent(e, InputEventType.MouseDown, button);
            }
        }

        private void ScreenImage_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!CanProcessPointerInput(e))
                return;

            var screenImage = this.FindControl<Image>("ScreenImage");
            var props = e.GetCurrentPoint(screenImage).Properties;

            RemoteMouseButton button = props.PointerUpdateKind switch
            {
                PointerUpdateKind.LeftButtonReleased => RemoteMouseButton.Left,
                PointerUpdateKind.RightButtonReleased => RemoteMouseButton.Right,
                PointerUpdateKind.MiddleButtonReleased => RemoteMouseButton.Middle,
                _ => RemoteMouseButton.Left
            };

            DispatchPointerButtonEvent(e, InputEventType.MouseUp, button);
        }

        private void SendWheelEvent(short delta, float x, float y)
        {
            _ = _controlClient.SendInputAsync(new RemoteInputMessage
            {
                Type = InputEventType.MouseWheel,
                WheelDelta = delta,
                NormalizedX = x,
                NormalizedY = y
            });
        }

        private void ScreenImage_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            if (!CanProcessPointerInput(e))
                return;

            short delta = (short)(e.Delta.Y * 120);

            if (_isGameMode)
            {
                SendWheelEvent(delta, -1f, -1f);
                e.Handled = true;
                return;
            }

            if (TryGetNormalizedCoordinates(e, out float x, out float y))
            {
                SendWheelEvent(delta, x, y);
                e.Handled = true;
            }
        }

        private void SendRemoteKeyEvent(Key key, InputEventType eventType)
        {
            if (_remoteControlEnabled && _controlClient.IsConnected)
            {
                int vk = ConvertAvaloniaKeyToWin32Vk(key);
                if (vk > 0)
                {
                    _ = _controlClient.SendInputAsync(new RemoteInputMessage
                    {
                        Type = eventType,
                        KeyCode = vk
                    });
                }
            }
        }

        private void ScreenImage_KeyDown(object? sender, KeyEventArgs e)
        {
            if (TryHandleShortcutKeys(e))
            {
                e.Handled = true;
                return;
            }

            SendRemoteKeyEvent(e.Key, InputEventType.KeyDown);
            e.Handled = true;
        }

        private void ScreenImage_KeyUp(object? sender, KeyEventArgs e)
        {
            if (IsShortcutKey(e))
            {
                e.Handled = true;
                return;
            }

            SendRemoteKeyEvent(e.Key, InputEventType.KeyUp);
            e.Handled = true;
        }

        private readonly record struct KeyRange(Key Start, Key End, int BaseVk)
        {
            public bool TryConvert(Key key, out int vk)
            {
                if (key >= Start && key <= End)
                {
                    vk = (int)key - (int)Start + BaseVk;
                    return true;
                }
                vk = 0;
                return false;
            }
        }

        private static readonly KeyRange[] SupportedKeyRanges =
        [
            new(Key.A, Key.Z, 0x41),
            new(Key.D0, Key.D9, 0x30),
            new(Key.NumPad0, Key.NumPad9, 0x60),
            new(Key.F1, Key.F12, 0x70)
        ];

        private static readonly Dictionary<Key, int> SpecialKeyMap = new()
        {
            [Key.Back] = 0x08,
            [Key.Tab] = 0x09,
            [Key.Return] = 0x0D,
            [Key.Enter] = 0x0D,
            [Key.Escape] = 0x1B,
            [Key.Space] = 0x20,
            [Key.Left] = 0x25,
            [Key.Up] = 0x26,
            [Key.Right] = 0x27,
            [Key.Down] = 0x28,
            [Key.Delete] = 0x2E,
            [Key.LeftShift] = 0x10,
            [Key.RightShift] = 0x10,
            [Key.LeftCtrl] = 0x11,
            [Key.RightCtrl] = 0x11,
            [Key.LeftAlt] = 0x12,
            [Key.RightAlt] = 0x12,
            [Key.Home] = 0x24,
            [Key.End] = 0x23,
            [Key.PageUp] = 0x21,
            [Key.PageDown] = 0x22
        };

        private static int ConvertAvaloniaKeyToWin32Vk(Key key)
        {
            foreach (var range in SupportedKeyRanges)
            {
                if (range.TryConvert(key, out int vk))
                    return vk;
            }

            return SpecialKeyMap.TryGetValue(key, out int mappedVk) ? mappedVk : 0;
        }

        #endregion

        #region Win32 P/Invoke for Mouse Centering

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
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
