using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Desktop
{
    public partial class StreamPopoutWindow : Window
    {
        public event Action? ReattachRequested;
        public event Action? DisconnectRequested;
        public event Action<bool>? RemoteControlToggled;
        public event Action? GameModeToggled;
        public event Action? SwitchMonitorRequested;
        public event Action? CycleMonitorRequested;
        public event Action? SendFileRequested;
        public event Action<string[]>? FilesDropped;
        public event Action? MuteToggled;
        public event Action<float>? VolumeChanged;
        public event Action? MicMuteToggled;
        public event Action<float>? MicVolumeChanged;
        public event Action<Stretch>? StretchModeChanged;
        public event Action<RemoteInputMessage>? RemoteInputSent;

        private bool _isRemoteControlEnabled;
        private bool _isGameMode;
        private bool _isRecenteringMouse;
        private bool _isFullscreen;
        private WindowState _previousWindowState = WindowState.Normal;
        private readonly DispatcherTimer _headerAutoHideTimer;
        private bool _isMouseOverHeaderBar;
        private DispatcherTimer? _gameModeBannerTimer;
        private Stretch _currentStretch = Stretch.Uniform;
        private double _currentAspect = 16.0 / 9.0;
        private bool _isClosingFromAction;

        public StreamPopoutWindow()
        {
            InitializeComponent();

            _headerAutoHideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2.5)
            };
            _headerAutoHideTimer.Tick += (s, e) =>
            {
                if (_isFullscreen && !_isMouseOverHeaderBar)
                {
                    var header = this.FindControl<Border>("StreamHeaderBar");
                    if (header != null)
                    {
                        header.IsVisible = false;
                    }
                    _headerAutoHideTimer.Stop();
                }
            };

            var headerBar = this.FindControl<Border>("StreamHeaderBar");
            if (headerBar != null)
            {
                headerBar.PointerEntered += (s, e) =>
                {
                    _isMouseOverHeaderBar = true;
                    _headerAutoHideTimer.Stop();
                };

                headerBar.PointerExited += (s, e) =>
                {
                    _isMouseOverHeaderBar = false;
                    if (_isFullscreen)
                    {
                        _headerAutoHideTimer.Stop();
                        _headerAutoHideTimer.Start();
                    }
                };
            }

            this.AddHandler(InputElement.PointerMovedEvent, Popout_GlobalPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

            var screenImage = this.FindControl<Image>("ScreenImage");
            if (screenImage != null)
            {
                screenImage.DoubleTapped += (s, e) =>
                {
                    // Si el control remoto o el modo juego están activos, no alternar pantalla completa
                    // para permitir abrir carpetas, archivos y hacer clics rápidos en juegos del anfitrión
                    if (_isRemoteControlEnabled || _isGameMode)
                        return;

                    ToggleFullscreen();
                };
                screenImage.PointerMoved += ScreenImage_PointerMoved;
                screenImage.PointerPressed += ScreenImage_PointerPressed;
                screenImage.PointerReleased += ScreenImage_PointerReleased;
                screenImage.PointerWheelChanged += ScreenImage_PointerWheelChanged;
                screenImage.KeyDown += ScreenImage_KeyDown;
                screenImage.KeyUp += ScreenImage_KeyUp;
            }

            var remoteControlBtn = this.FindControl<ToggleButton>("RemoteControlButton");
            if (remoteControlBtn != null)
            {
                remoteControlBtn.IsCheckedChanged += (s, e) =>
                {
                    RemoteControlToggled?.Invoke(remoteControlBtn.IsChecked == true);
                };
            }

            var gameModeBtn = this.FindControl<ToggleButton>("GameModeButton");
            if (gameModeBtn != null)
            {
                gameModeBtn.IsCheckedChanged += (s, e) =>
                {
                    GameModeToggled?.Invoke();
                };
            }

            var switchMonitorBtn = this.FindControl<Button>("SwitchMonitorButton");
            if (switchMonitorBtn != null)
            {
                switchMonitorBtn.Click += (s, e) => SwitchMonitorRequested?.Invoke();
            }

            var sendFileBtn = this.FindControl<Button>("SendFileButton");
            if (sendFileBtn != null)
            {
                sendFileBtn.Click += (s, e) => SendFileRequested?.Invoke();
            }

            this.AddHandler(DragDrop.DropEvent, Popout_Drop);
            this.AddHandler(DragDrop.DragOverEvent, Popout_DragOver);

            var muteBtn = this.FindControl<Button>("AudioMuteButton");
            if (muteBtn != null)
            {
                muteBtn.Click += (s, e) => MuteToggled?.Invoke();
            }

            var volumeSlider = this.FindControl<Slider>("AudioVolumeSlider");
            if (volumeSlider != null)
            {
                volumeSlider.PropertyChanged += (s, e) =>
                {
                    if (e.Property.Name == nameof(Slider.Value))
                    {
                        float vol = (float)Math.Clamp(volumeSlider.Value / 100.0, 0.0, 1.0);
                        VolumeChanged?.Invoke(vol);
                        UpdateVolumeText(volumeSlider.Value);
                    }
                };
            }

            var micMuteBtn = this.FindControl<Button>("MicMuteButton");
            if (micMuteBtn != null)
            {
                micMuteBtn.Click += (s, e) => MicMuteToggled?.Invoke();
            }

            var micVolumeSlider = this.FindControl<Slider>("MicVolumeSlider");
            if (micVolumeSlider != null)
            {
                micVolumeSlider.PropertyChanged += (s, e) =>
                {
                    if (e.Property.Name == nameof(Slider.Value))
                    {
                        float vol = (float)Math.Clamp(micVolumeSlider.Value / 100.0, 0.0, 1.0);
                        MicVolumeChanged?.Invoke(vol);
                        UpdateMicVolumeText(micVolumeSlider.Value);
                    }
                };
            }

            var stretchBtn = this.FindControl<Button>("StretchModeButton");
            if (stretchBtn != null)
            {
                stretchBtn.Click += (s, e) =>
                {
                    _currentStretch = _currentStretch == Stretch.Uniform ? Stretch.Fill : Stretch.Uniform;
                    SetStretchMode(_currentStretch);
                    StretchModeChanged?.Invoke(_currentStretch);
                };
            }

            var autoFitBtn = this.FindControl<Button>("AutoFitButton");
            if (autoFitBtn != null)
            {
                autoFitBtn.Click += (s, e) => AutoFitToAspect(_currentAspect);
            }

            var fullscreenBtn = this.FindControl<Button>("FullscreenButton");
            if (fullscreenBtn != null)
            {
                fullscreenBtn.Click += (s, e) => ToggleFullscreen();
            }

            var reattachBtn = this.FindControl<Button>("ReattachButton");
            if (reattachBtn != null)
            {
                reattachBtn.Click += (s, e) =>
                {
                    _isClosingFromAction = true;
                    ReattachRequested?.Invoke();
                    this.Close();
                };
            }

            var disconnectBtn = this.FindControl<Button>("DisconnectButton");
            if (disconnectBtn != null)
            {
                disconnectBtn.Click += (s, e) =>
                {
                    _isClosingFromAction = true;
                    DisconnectRequested?.Invoke();
                    this.Close();
                };
            }

            this.KeyDown += PopoutWindow_KeyDown;
            this.Closing += PopoutWindow_Closing;
        }

        private void Popout_DragOver(object? sender, DragEventArgs e)
        {
            if (e.DataTransfer.Contains(DataFormat.File))
            {
                e.DragEffects = DragDropEffects.Copy;
            }
            else
            {
                e.DragEffects = DragDropEffects.None;
            }
        }

        private void Popout_Drop(object? sender, DragEventArgs e)
        {
            if (e.DataTransfer.TryGetFiles() is { } files)
            {
                var paths = files.Select(f => f.Path.LocalPath).Where(File.Exists).ToArray();
                if (paths.Length > 0)
                {
                    FilesDropped?.Invoke(paths);
                }
            }
        }

        public void UpdateFrame(IImage image)
        {
            var screenImage = this.FindControl<Image>("ScreenImage");
            if (screenImage != null)
            {
                screenImage.Source = image;
                if (image.Size.Width > 0 && image.Size.Height > 0)
                {
                    _currentAspect = image.Size.Width / image.Size.Height;
                }
            }
        }

        public void UpdateMetrics(string statsText, string? qualityColorHex = null)
        {
            var statsTextControl = this.FindControl<TextBlock>("StatsText");
            if (statsTextControl != null)
            {
                statsTextControl.Text = statsText;
            }

            if (!string.IsNullOrEmpty(qualityColorHex))
            {
                var dot = this.FindControl<Avalonia.Controls.Shapes.Ellipse>("StatsQualityDot");
                if (dot != null && Avalonia.Media.Color.TryParse(qualityColorHex, out var color))
                {
                    dot.Fill = new Avalonia.Media.SolidColorBrush(color);
                }
            }
        }

        public void SetRemoteControlAllowed(bool allowed)
        {
            var remoteControlBtn = this.FindControl<ToggleButton>("RemoteControlButton");
            var gameModeBtn = this.FindControl<ToggleButton>("GameModeButton");
            var switchMonitorBtn = this.FindControl<Button>("SwitchMonitorButton");
            if (remoteControlBtn != null) remoteControlBtn.IsVisible = allowed;
            if (gameModeBtn != null) gameModeBtn.IsVisible = allowed;
            if (switchMonitorBtn != null) switchMonitorBtn.IsVisible = allowed;
        }

        public void SetActiveMonitorLabel(string label)
        {
            var text = this.FindControl<TextBlock>("SwitchMonitorButtonText");
            if (text != null)
            {
                text.Text = string.IsNullOrWhiteSpace(label) ? "Pantalla" : label;
            }
        }

        public Button? GetSwitchMonitorButton() => this.FindControl<Button>("SwitchMonitorButton");

        public void SetRemoteControlState(bool enabled)
        {
            _isRemoteControlEnabled = enabled;
            var remoteControlBtn = this.FindControl<ToggleButton>("RemoteControlButton");
            if (remoteControlBtn != null)
            {
                remoteControlBtn.IsChecked = enabled;
                remoteControlBtn.Background = enabled ? new SolidColorBrush(Color.Parse("#2563EB")) : new SolidColorBrush(Color.Parse("#27272A"));
            }
        }

        public void SetGameModeState(bool isGameMode)
        {
            _isGameMode = isGameMode;
            var gameModeBtn = this.FindControl<ToggleButton>("GameModeButton");
            var screenImage = this.FindControl<Image>("ScreenImage");
            var banner = this.FindControl<Border>("GameModeBanner");
            var bannerText = this.FindControl<TextBlock>("GameModeBannerText");
            var bannerIcon = this.FindControl<PathIcon>("GameModeBannerIcon");

            if (gameModeBtn != null)
            {
                gameModeBtn.IsChecked = isGameMode;
                gameModeBtn.Background = isGameMode ? new SolidColorBrush(Color.Parse("#2563EB")) : new SolidColorBrush(Color.Parse("#27272A"));
            }

            if (isGameMode)
            {
                if (screenImage != null) screenImage.Cursor = new Cursor(StandardCursorType.None);
                if (banner != null && bannerText != null)
                {
                    banner.IsVisible = true;
                    if (bannerIcon != null)
                    {
                        bannerIcon.Data = (this.FindResource("Icon3dRotation") as StreamGeometry)!;
                        bannerIcon.Foreground = new SolidColorBrush(Color.Parse("#38BDF8"));
                    }
                    bannerText.Text = "Modo 3D Activo — Presiona Ctrl + Alt + L para liberar el cursor";
                    bannerText.Foreground = new SolidColorBrush(Color.Parse("#38BDF8"));

                    _gameModeBannerTimer?.Stop();
                    _gameModeBannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    _gameModeBannerTimer.Tick += (s, e) =>
                    {
                        banner.IsVisible = false;
                        _gameModeBannerTimer?.Stop();
                    };
                    _gameModeBannerTimer.Start();
                }
            }
            else
            {
                if (screenImage != null) screenImage.Cursor = Cursor.Default;
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
        }

        public void SetVolume(float volume, bool isMuted)
        {
            var volumeSlider = this.FindControl<Slider>("AudioVolumeSlider");
            if (volumeSlider != null)
            {
                volumeSlider.Value = Math.Round(volume * 100);
            }
            UpdateVolumeText(volume * 100);

            var muteIcon = this.FindControl<PathIcon>("AudioMuteIcon");
            if (muteIcon != null)
            {
                muteIcon.Data = (this.FindResource(isMuted || volume <= 0.01f ? "IconVolumeOff" : "IconVolumeUp") as StreamGeometry)!;
            }
        }

        private void UpdateVolumeText(double percent)
        {
            var volumeText = this.FindControl<TextBlock>("AudioVolumeText");
            if (volumeText != null)
            {
                volumeText.Text = $"{(int)Math.Round(percent)}%";
            }
        }

        public void SetMicVolume(float volume, bool isMuted)
        {
            var volumeSlider = this.FindControl<Slider>("MicVolumeSlider");
            if (volumeSlider != null)
            {
                volumeSlider.Value = Math.Round(volume * 100);
            }
            UpdateMicVolumeText(volume * 100);

            var muteIcon = this.FindControl<PathIcon>("MicMuteIcon");
            if (muteIcon != null)
            {
                muteIcon.Data = (this.FindResource(isMuted || volume <= 0.01f ? "IconMicOff" : "IconMic") as StreamGeometry)!;
                muteIcon.Foreground = new SolidColorBrush(isMuted || volume <= 0.01f ? Avalonia.Media.Color.Parse("#EF4444") : Avalonia.Media.Color.Parse("#10B981"));
            }
        }

        private void UpdateMicVolumeText(double percent)
        {
            var volumeText = this.FindControl<TextBlock>("MicVolumeText");
            if (volumeText != null)
            {
                volumeText.Text = $"{(int)Math.Round(percent)}%";
            }
        }

        public void SetStretchMode(Stretch stretch)
        {
            _currentStretch = stretch;
            var screenImage = this.FindControl<Image>("ScreenImage");
            if (screenImage != null)
            {
                screenImage.Stretch = stretch;
            }

            var stretchBtnText = this.FindControl<TextBlock>("StretchModeButtonText");
            if (stretchBtnText != null)
            {
                stretchBtnText.Text = stretch == Stretch.Uniform ? "Ajustar" : "Llenar";
            }
        }

        public void AutoFitToAspect(double aspect)
        {
            if (aspect <= 0.2 || aspect > 5.0) return;
            _currentAspect = aspect;

            if (_isFullscreen) return;

            double currentW = this.ClientSize.Width;
            double targetH = Math.Round(currentW / aspect);
            if (targetH >= 300 && targetH <= 2500)
            {
                this.Height = targetH;
            }
        }

        private void Popout_GlobalPointerMoved(object? sender, PointerEventArgs e)
        {
            if (!_isFullscreen || _isGameMode) return;

            var headerBar = this.FindControl<Border>("StreamHeaderBar");
            if (headerBar == null) return;

            var pos = e.GetPosition(this);

            if (pos.Y <= 20)
            {
                if (!headerBar.IsVisible) headerBar.IsVisible = true;
                _headerAutoHideTimer.Stop();
                _headerAutoHideTimer.Start();
            }
            else if (headerBar.IsVisible)
            {
                double headerHeight = headerBar.Bounds.Height > 0 ? headerBar.Bounds.Height : 50;
                if (pos.Y <= headerHeight)
                {
                    _headerAutoHideTimer.Stop();
                    _headerAutoHideTimer.Start();
                }
                else if (pos.Y > headerHeight + 25)
                {
                    headerBar.IsVisible = false;
                    _headerAutoHideTimer.Stop();
                }
            }
        }

        private void ToggleFullscreen()
        {
            SetFullscreen(!_isFullscreen);
        }

        private void SetFullscreen(bool fullscreen)
        {
            _isFullscreen = fullscreen;
            var headerBar = this.FindControl<Border>("StreamHeaderBar");
            var fullscreenIcon = this.FindControl<PathIcon>("FullscreenButtonIcon");
            var fullscreenText = this.FindControl<TextBlock>("FullscreenButtonText");

            if (_isFullscreen)
            {
                _previousWindowState = this.WindowState;
                this.WindowState = WindowState.FullScreen;

                if (fullscreenIcon != null) fullscreenIcon.Data = (this.FindResource("IconFullscreenExit") as StreamGeometry)!;
                if (fullscreenText != null) fullscreenText.Text = "Salir";

                if (headerBar != null)
                {
                    headerBar.IsVisible = false;
                }
            }
            else
            {
                this.WindowState = _previousWindowState == WindowState.FullScreen ? WindowState.Normal : _previousWindowState;

                if (fullscreenIcon != null) fullscreenIcon.Data = (this.FindResource("IconFullscreen") as StreamGeometry)!;
                if (fullscreenText != null) fullscreenText.Text = "Pantalla Completa";

                if (headerBar != null)
                {
                    headerBar.IsVisible = true;
                }
            }
        }

        private bool IsPointerOverHeader(PointerEventArgs e)
        {
            var header = this.FindControl<Border>("StreamHeaderBar");
            if (header == null || !header.IsVisible) return false;
            var pos = e.GetPosition(this);
            double headerHeight = header.Bounds.Height > 0 ? header.Bounds.Height : 50;
            return pos.Y <= headerHeight;
        }

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

            if (screenImage.Stretch == Stretch.Fill)
            {
                var pos = e.GetPosition(screenImage);
                normX = (float)Math.Clamp(pos.X / controlBounds.Width, 0.0, 1.0);
                normY = (float)Math.Clamp(pos.Y / controlBounds.Height, 0.0, 1.0);
                return true;
            }

            double scaleX = controlBounds.Width / imageSize.Width;
            double scaleY = controlBounds.Height / imageSize.Height;
            double scale = Math.Min(scaleX, scaleY);

            double renderedW = imageSize.Width * scale;
            double renderedH = imageSize.Height * scale;

            double offsetX = (controlBounds.Width - renderedW) / 2.0;
            double offsetY = (controlBounds.Height - renderedH) / 2.0;

            var p = e.GetPosition(screenImage);

            if (p.X < offsetX || p.X > offsetX + renderedW ||
                p.Y < offsetY || p.Y > offsetY + renderedH)
            {
                return false;
            }

            normX = (float)Math.Clamp((p.X - offsetX) / renderedW, 0.0, 1.0);
            normY = (float)Math.Clamp((p.Y - offsetY) / renderedH, 0.0, 1.0);
            return true;
        }

        private Avalonia.PixelPoint GetScreenImageCenterOnScreen()
        {
            var screenImage = this.FindControl<Image>("ScreenImage");
            if (screenImage != null && screenImage.Bounds.Width > 0 && screenImage.Bounds.Height > 0)
            {
                int imgCenterX = this.Position.X + (int)Math.Round(screenImage.Bounds.X + (screenImage.Bounds.Width / 2.0));
                int imgCenterY = this.Position.Y + (int)Math.Round(screenImage.Bounds.Y + (screenImage.Bounds.Height / 2.0));
                return new Avalonia.PixelPoint(imgCenterX, imgCenterY);
            }

            int winCenterX = this.Position.X + (int)Math.Round(this.Bounds.Width / 2.0);
            int winCenterY = this.Position.Y + (int)Math.Round(this.Bounds.Height / 2.0);
            return new Avalonia.PixelPoint(winCenterX, winCenterY);
        }

        private void ScreenImage_PointerMoved(object? sender, PointerEventArgs e)
        {
            if (!_isRemoteControlEnabled) return;
            if (IsPointerOverHeader(e)) return;

            if (_isGameMode)
            {
                if (_isRecenteringMouse)
                {
                    _isRecenteringMouse = false;
                    return;
                }

                if (OperatingSystem.IsWindows())
                {
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

        private void ScreenImage_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!_isRemoteControlEnabled) return;
            if (IsPointerOverHeader(e)) return;

            var screenImage = this.FindControl<Image>("ScreenImage");
            screenImage?.Focus();

            var point = e.GetCurrentPoint(screenImage);
            var props = point.Properties;

            RemoteMouseButton button = props.IsLeftButtonPressed ? RemoteMouseButton.Left
                : props.IsRightButtonPressed ? RemoteMouseButton.Right
                : props.IsMiddleButtonPressed ? RemoteMouseButton.Middle
                : RemoteMouseButton.None;

            if (button == RemoteMouseButton.None) return;

            if (_isGameMode)
            {
                RemoteInputSent?.Invoke(new RemoteInputMessage
                {
                    Type = InputEventType.MouseDown,
                    Button = button,
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
                    Type = InputEventType.MouseDown,
                    Button = button,
                    NormalizedX = x,
                    NormalizedY = y
                });
                e.Handled = true;
            }
        }

        private void ScreenImage_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (!_isRemoteControlEnabled) return;
            if (IsPointerOverHeader(e)) return;

            var screenImage = this.FindControl<Image>("ScreenImage");
            var point = e.GetCurrentPoint(screenImage);
            var props = point.Properties;

            RemoteMouseButton button = props.PointerUpdateKind == PointerUpdateKind.LeftButtonReleased ? RemoteMouseButton.Left
                : props.PointerUpdateKind == PointerUpdateKind.RightButtonReleased ? RemoteMouseButton.Right
                : props.PointerUpdateKind == PointerUpdateKind.MiddleButtonReleased ? RemoteMouseButton.Middle
                : RemoteMouseButton.Left;

            if (_isGameMode)
            {
                RemoteInputSent?.Invoke(new RemoteInputMessage
                {
                    Type = InputEventType.MouseUp,
                    Button = button,
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
                    Type = InputEventType.MouseUp,
                    Button = button,
                    NormalizedX = x,
                    NormalizedY = y
                });
                e.Handled = true;
            }
        }

        private void ScreenImage_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            if (!_isRemoteControlEnabled) return;
            if (IsPointerOverHeader(e)) return;

            short delta = (short)(e.Delta.Y * 120);

            if (_isGameMode)
            {
                RemoteInputSent?.Invoke(new RemoteInputMessage
                {
                    Type = InputEventType.MouseWheel,
                    WheelDelta = delta,
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
                    Type = InputEventType.MouseWheel,
                    WheelDelta = delta,
                    NormalizedX = x,
                    NormalizedY = y
                });
                e.Handled = true;
            }
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

            int keyCode = (int)e.Key;
            RemoteInputSent?.Invoke(new RemoteInputMessage
            {
                Type = InputEventType.KeyDown,
                KeyCode = keyCode
            });
            e.Handled = true;
        }

        private void ScreenImage_KeyUp(object? sender, KeyEventArgs e)
        {
            if (!_isRemoteControlEnabled) return;

            int keyCode = (int)e.Key;
            RemoteInputSent?.Invoke(new RemoteInputMessage
            {
                Type = InputEventType.KeyUp,
                KeyCode = keyCode
            });
            e.Handled = true;
        }

        private void PopoutWindow_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.L &&
                e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
                e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                GameModeToggled?.Invoke();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.M &&
                e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
                e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                CycleMonitorRequested?.Invoke();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.F11)
            {
                ToggleFullscreen();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape && _isFullscreen)
            {
                SetFullscreen(false);
                e.Handled = true;
                return;
            }
        }

        private void PopoutWindow_Closing(object? sender, WindowClosingEventArgs e)
        {
            SetFullscreen(false);
            _headerAutoHideTimer.Stop();
            _gameModeBannerTimer?.Stop();

            if (!_isClosingFromAction)
            {
                ReattachRequested?.Invoke();
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

