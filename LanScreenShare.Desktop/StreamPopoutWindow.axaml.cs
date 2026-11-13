using System;
using System.IO;
using System.Linq;
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
        private DispatcherTimer _headerAutoHideTimer = null!;
        private DispatcherTimer _footerAutoHideTimer = null!;
        private bool _isMouseOverHeaderBar;
        private bool _isMouseOverFooterBar;
        private DispatcherTimer? _gameModeBannerTimer;
        private Stretch _currentStretch = Stretch.Uniform;
        private double _currentAspect = 16.0 / 9.0;
        private bool _isClosingFromAction;

        public StreamPopoutWindow()
        {
            InitializeComponent();
            InitializeHeaderAndAutoHide();
            InitializeFooterAndAutoHide();
            InitializeScreenImageEvents();
            InitializeRemoteControlButtons();
            InitializeAudioControls();
            InitializeWindowButtons();
        }

        private void InitializeHeaderAndAutoHide()
        {
            _headerAutoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _headerAutoHideTimer.Tick += OnHeaderAutoHideTick;

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
                    if (_isFullscreen) RestartHeaderTimer();
                };
            }

            this.AddHandler(InputElement.PointerMovedEvent, Popout_GlobalPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        }

        private void InitializeFooterAndAutoHide()
        {
            _footerAutoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _footerAutoHideTimer.Tick += OnFooterAutoHideTick;

            var footerBar = this.FindControl<Border>("StreamFooterBar");
            if (footerBar != null)
            {
                footerBar.PointerEntered += (s, e) =>
                {
                    _isMouseOverFooterBar = true;
                    _footerAutoHideTimer.Stop();
                };
                footerBar.PointerExited += (s, e) =>
                {
                    _isMouseOverFooterBar = false;
                    RestartFooterTimer();
                };
            }

            RestartFooterTimer();
        }

        private void OnHeaderAutoHideTick(object? sender, EventArgs e)
        {
            if (!_isFullscreen || _isMouseOverHeaderBar) return;
            var header = this.FindControl<Border>("StreamHeaderBar");
            if (header != null) header.IsVisible = false;
            _headerAutoHideTimer.Stop();
        }

        private void OnFooterAutoHideTick(object? sender, EventArgs e)
        {
            if (_isMouseOverFooterBar) return;
            var footer = this.FindControl<Border>("StreamFooterBar");
            if (footer != null) footer.IsVisible = false;
            _footerAutoHideTimer.Stop();
        }

        private void RestartFooterTimer()
        {
            _footerAutoHideTimer.Stop();
            _footerAutoHideTimer.Start();
        }

        private void InitializeScreenImageEvents()
        {
            var screenImage = this.FindControl<Image>("ScreenImage");
            if (screenImage != null)
            {
                screenImage.DoubleTapped += (s, e) =>
                {
                    if (_isRemoteControlEnabled || _isGameMode) return;
                    ToggleFullscreen();
                };
                screenImage.PointerMoved += ScreenImage_PointerMoved;
                screenImage.PointerPressed += ScreenImage_PointerPressed;
                screenImage.PointerReleased += ScreenImage_PointerReleased;
                screenImage.PointerWheelChanged += ScreenImage_PointerWheelChanged;
                screenImage.KeyDown += ScreenImage_KeyDown;
                screenImage.KeyUp += ScreenImage_KeyUp;
            }
        }

        private void InitializeRemoteControlButtons()
        {
            var remoteControlBtn = this.FindControl<ToggleButton>("RemoteControlButton");
            if (remoteControlBtn != null)
            {
                remoteControlBtn.IsCheckedChanged += (s, e) => RemoteControlToggled?.Invoke(remoteControlBtn.IsChecked == true);
            }

            var gameModeBtn = this.FindControl<ToggleButton>("GameModeButton");
            if (gameModeBtn != null)
            {
                gameModeBtn.IsCheckedChanged += (s, e) => GameModeToggled?.Invoke();
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
        }

        private void ConfigureAudioVolumeSlider(string sliderName, Action<float>? volumeChanged, Action<double> updateVolumeText)
        {
            var slider = this.FindControl<Slider>(sliderName);
            if (slider == null) return;

            slider.PropertyChanged += (s, e) =>
            {
                if (e.Property.Name == nameof(Slider.Value))
                {
                    float vol = (float)Math.Clamp(slider.Value / 100.0, 0.0, 1.0);
                    volumeChanged?.Invoke(vol);
                    updateVolumeText(slider.Value);
                }
            };
        }

        private void InitializeAudioControls()
        {
            var muteBtn = this.FindControl<Button>("AudioMuteButton");
            if (muteBtn != null) muteBtn.Click += (s, e) => MuteToggled?.Invoke();

            ConfigureAudioVolumeSlider("AudioVolumeSlider", VolumeChanged, UpdateVolumeText);

            var micMuteBtn = this.FindControl<Button>("MicMuteButton");
            if (micMuteBtn != null) micMuteBtn.Click += (s, e) => MicMuteToggled?.Invoke();

            ConfigureAudioVolumeSlider("MicVolumeSlider", MicVolumeChanged, UpdateMicVolumeText);
        }

        private void InitializeWindowButtons()
        {
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
                if (screenImage.Source != image)
                {
                    screenImage.Source = image;
                }

                if (image.Size.Width > 0 && image.Size.Height > 0)
                {
                    _currentAspect = image.Size.Width / image.Size.Height;
                }

                screenImage.InvalidateVisual();
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

        private readonly record struct BannerStyle(string IconName, string ColorHex, string Text, double Seconds);

        private static readonly BannerStyle ActiveGameModeStyle = new(
            "Icon3dRotation", "#38BDF8", "Modo 3D Activo — Presiona Ctrl + Alt + L para liberar el cursor", 3.0);

        private static readonly BannerStyle InactiveGameModeStyle = new(
            "IconLockOpen", "#FBBF24", "Mouse Liberado — Presiona Ctrl + Alt + L para recapturar", 2.5);

        private static bool IsMutedOrZero(bool isMuted, float volume) =>
            isMuted || volume <= 0.01f;

        private bool TryGetStreamGeometry(string resourceKey, out StreamGeometry geometry)
        {
            if (this.TryFindResource(resourceKey, out var res) && res is StreamGeometry geo)
            {
                geometry = geo;
                return true;
            }
            geometry = null!;
            return false;
        }

        private void ShowGameModeBanner(bool isGameMode, Border? banner, TextBlock? bannerText, PathIcon? bannerIcon)
        {
            if (banner == null || bannerText == null) return;

            var style = isGameMode ? ActiveGameModeStyle : InactiveGameModeStyle;
            banner.IsVisible = true;

            if (bannerIcon != null && TryGetStreamGeometry(style.IconName, out var geo))
            {
                bannerIcon.Data = geo;
                bannerIcon.Foreground = new SolidColorBrush(Color.Parse(style.ColorHex));
            }
            bannerText.Text = style.Text;
            bannerText.Foreground = new SolidColorBrush(Color.Parse(style.ColorHex));

            RestartBannerTimer(banner, style.Seconds);
        }

        private void RestartBannerTimer(Border banner, double seconds)
        {
            _gameModeBannerTimer?.Stop();
            _gameModeBannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            _gameModeBannerTimer.Tick += (s, e) =>
            {
                banner.IsVisible = false;
                _gameModeBannerTimer?.Stop();
            };
            _gameModeBannerTimer.Start();
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

            if (screenImage != null)
            {
                screenImage.Cursor = isGameMode ? new Cursor(StandardCursorType.None) : Cursor.Default;
            }

            ShowGameModeBanner(isGameMode, banner, bannerText, bannerIcon);
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
            string iconName = IsMutedOrZero(isMuted, volume) ? "IconVolumeOff" : "IconVolumeUp";
            if (muteIcon != null && TryGetStreamGeometry(iconName, out var geo))
            {
                muteIcon.Data = geo;
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
            if (muteIcon == null) return;

            bool muted = IsMutedOrZero(isMuted, volume);
            string iconName = muted ? "IconMicOff" : "IconMic";
            if (TryGetStreamGeometry(iconName, out var geo))
            {
                muteIcon.Data = geo;
                muteIcon.Foreground = new SolidColorBrush(Color.Parse(muted ? "#EF4444" : "#10B981"));
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
            if (_isGameMode) return;
            double posY = e.GetPosition(this).Y;
            double windowHeight = this.Bounds.Height > 0 ? this.Bounds.Height : this.ClientSize.Height;

            if (_isFullscreen)
            {
                var headerBar = this.FindControl<Border>("StreamHeaderBar");
                if (headerBar != null) UpdateHeaderVisibility(headerBar, posY);
            }

            var footerBar = this.FindControl<Border>("StreamFooterBar");
            if (footerBar != null) UpdateFooterVisibility(footerBar, posY, windowHeight);
        }

        private void UpdateHeaderVisibility(Border headerBar, double posY)
        {
            if (posY <= 20)
            {
                if (!headerBar.IsVisible) headerBar.IsVisible = true;
                RestartHeaderTimer();
                return;
            }

            if (!headerBar.IsVisible || _isMouseOverHeaderBar) return;

            double headerHeight = headerBar.Bounds.Height > 0 ? headerBar.Bounds.Height : 50;
            if (posY <= headerHeight)
            {
                RestartHeaderTimer();
            }
            else if (posY > headerHeight + 25)
            {
                headerBar.IsVisible = false;
                _headerAutoHideTimer.Stop();
            }
        }

        private void UpdateFooterVisibility(Border footerBar, double posY, double windowHeight)
        {
            if (windowHeight <= 0) return;

            double footerHeight = footerBar.Bounds.Height > 0 ? footerBar.Bounds.Height : 50;
            double threshold = windowHeight - Math.Max(40, footerHeight);

            if (posY >= threshold)
            {
                if (!footerBar.IsVisible) footerBar.IsVisible = true;
                RestartFooterTimer();
                return;
            }

            if (!footerBar.IsVisible || _isMouseOverFooterBar) return;

            if (posY < threshold - 25)
            {
                footerBar.IsVisible = false;
                _footerAutoHideTimer.Stop();
            }
        }

        private void RestartHeaderTimer()
        {
            _headerAutoHideTimer.Stop();
            _headerAutoHideTimer.Start();
        }

        private void ToggleFullscreen() => SetFullscreen(!_isFullscreen);

        private void UpdateFullscreenButtonUI(bool fullscreen)
        {
            var fullscreenIcon = this.FindControl<PathIcon>("FullscreenButtonIcon");
            var fullscreenText = this.FindControl<TextBlock>("FullscreenButtonText");

            string iconRes = fullscreen ? "IconFullscreenExit" : "IconFullscreen";
            if (fullscreenIcon != null && TryGetStreamGeometry(iconRes, out var geo))
            {
                fullscreenIcon.Data = geo;
            }
            if (fullscreenText != null)
            {
                fullscreenText.Text = fullscreen ? "Salir" : "Pantalla Completa";
            }
        }

        private void SetFullscreen(bool fullscreen)
        {
            _isFullscreen = fullscreen;
            var headerBar = this.FindControl<Border>("StreamHeaderBar");
            var footerBar = this.FindControl<Border>("StreamFooterBar");

            if (fullscreen)
            {
                _previousWindowState = this.WindowState;
                this.WindowState = WindowState.FullScreen;
            }
            else
            {
                this.WindowState = _previousWindowState == WindowState.FullScreen ? WindowState.Normal : _previousWindowState;
            }

            UpdateFullscreenButtonUI(fullscreen);

            if (headerBar != null)
            {
                headerBar.IsVisible = !fullscreen;
            }

            if (footerBar != null)
            {
                footerBar.IsVisible = !fullscreen;
                if (!fullscreen)
                {
                    RestartFooterTimer();
                }
                else
                {
                    _footerAutoHideTimer.Stop();
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

        private void PopoutWindow_Closing(object? sender, WindowClosingEventArgs e)
        {
            SetFullscreen(false);
            _headerAutoHideTimer.Stop();
            _footerAutoHideTimer.Stop();
            _gameModeBannerTimer?.Stop();

            if (!_isClosingFromAction)
            {
                ReattachRequested?.Invoke();
            }
        }
    }
}
