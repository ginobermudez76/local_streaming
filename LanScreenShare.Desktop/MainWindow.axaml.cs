using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LanScreenShare.Core.Security;
using LanScreenShare.Network.Security;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;
using LanScreenShare.Core.Services;
using LanScreenShare.Media;
using LanScreenShare.Network;
using LanScreenShare.Network.Streaming;

namespace LanScreenShare.Desktop
{
    public partial class MainWindow : Window
    {
        private DiscoveryService _discoveryService = null!;
        private IScreenCaptureService _screenCaptureService = null!;
        private TcpStreamServer _streamServer = null!;
        private TcpStreamClient _streamClient = null!;

        // Seguridad, autoridad de sesiones e identidad mTLS
        private IIdentityKeyStorage _identityKeyStorage = null!;
        private IPairedDeviceStore _pairedDeviceStore = null!;
        private ISessionAuthority _sessionAuthority = null!;
        private X509Certificate2? _identityCertificate;
        private SslServerAuthenticationOptions? _sslServerOptions;
        private SslClientAuthenticationOptions? _sslClientOptions;

        // Servicios de streaming de audio
        private IAudioCaptureService? _audioCaptureService;
        private IAudioPlayerService? _audioPlayerService;
        private IAudioCaptureService? _micCaptureService;
        private IAudioPlayerService? _micAudioPlayerService;
        private TcpAudioServer _audioServer = null!;
        private TcpAudioClient _audioClient = null!;
        private volatile bool _isViewerVoiceChatActive;
        private volatile bool _allowViewerVoiceChat = true;

        // Servicios de control remoto (Mouse, Teclado y Portapapeles)
        private IInputSimulatorService? _inputSimulatorService;
        private TcpControlServer _controlServer = null!;
        private TcpControlClient _controlClient = null!;
        private bool _remoteControlEnabled;
        private volatile bool _allowRemoteControl = true;
        private volatile bool _allowClipboardSync = true;
        private DispatcherTimer _clipboardSyncTimer = null!;
        private string? _lastLocalClipboardText;
        private string? _lastNetworkClipboardText;
        private bool _isPollingClipboard;
        private string _currentRemoteMonitorLabel = "Pantalla";
        private RemoteMonitorCatalogDto? _remoteMonitorCatalog;
        private ContextMenu? _remoteSourceMenu;
        private bool _isGameMode;
        private bool _isRecenteringMouse;
        private DispatcherTimer? _gameModeBannerTimer;
        private TcpFileTransferServer _fileTransferServer = null!;
        private TcpFileTransferClient _fileTransferClient = null!;
        private CancellationTokenSource? _activeTransferCts;
        private string? _lastReceivedFilePath;
        private DispatcherTimer? _fileTransferBannerTimer;
        private readonly AdaptiveBitrateController _abrController = new();

        private bool _isHosting;
        private bool _isViewing;
        private string? _currentConnectedDeviceName;
        private string? _currentConnectedDeviceIp;
        private bool _currentDeviceAllowsRemoteControl;

        // Pantalla completa y barra superior auto-ocultable
        private bool _isFullscreen;
        private WindowState _previousWindowState = WindowState.Normal;
        private DispatcherTimer _headerAutoHideTimer = null!;
        private bool _isMouseOverHeaderBar;

        // Ventana emergente (Pop-out) y modo de ajuste de pantalla
        private StreamPopoutWindow? _popoutWindow;
        private bool _isPopoutActive;
        private Stretch _currentStretchMode = Stretch.Uniform;

        // BÃƒÂºsqueda de dispositivos
        private DispatcherTimer? _searchTimer;

        // Control de acceso y aprobaciÃƒÂ³n de conexiones
        private volatile bool _requireStreamApproval = false;
        private volatile string _remoteControlMode = "auto";
        private TaskCompletionSource<bool>? _streamApprovalTcs;
        private TaskCompletionSource<bool>? _controlApprovalTcs;

        // Decodificador de video por hardware (H.264)
        private IVideoDecoderService? _h264Decoder;
        private WriteableBitmap? _viewerWriteableBitmap;
        private byte[]? _latestDecodedBgra;
        private int _latestDecodedWidth;
        private int _latestDecodedHeight;
        private readonly object _bitmapLock = new();
        private int _isUiRenderPending;

        // Estadísticas de flujo en tiempo real y sincronización A/V
        private readonly AudioVideoSyncController _avSyncController = new();
        private long _metricFramesCount;
        private long _metricBytesCount;
        private int _lastFrameWidth;
        private int _lastFrameHeight;
        private int _lastHostFps;
        private int _lastHostFrameTimeMs;
        private DispatcherTimer _metricsTimer = null!;
        private AppSettings _appSettings = new();
        private bool _isInitializingSettings = true;
        private bool _isApplyingPreset = false;
        private CaptureSource _currentSource = new()
        {
            Type = CaptureSourceType.Screen,
            Id = "screen:0",
            Name = "Pantalla 1",
            MonitorIndex = 0
        };

        public ObservableCollection<DeviceInfo> Devices { get; } = new();

        public MainWindow()
        {
            InitializeComponent();

            _appSettings = SettingsService.Load();

            InitializeCaptureAndDiscovery();
            InitializeSecuritySubsystem();
            InitializeStreamingSubsystem();
            InitializeAudioSubsystem();
            InitializeControlSubsystem();
            InitializeFileTransferSubsystem();
            InitializeDisplayAndWindowControls();
            InitializeAudioControls();
            InitializeAccessAndApproval();
            InitializeEncoderAndSettingsControls();
            InitializeTimersAndLifecycle();
            if (!string.IsNullOrEmpty(_appSettings.TransmissionPreset) && _appSettings.TransmissionPreset != "custom")
            {
                ApplyTransmissionPreset(_appSettings.TransmissionPreset, save: false);
            }
            else
            {
                UpdatePresetButtonsHighlight(_appSettings.TransmissionPreset ?? "quality");
            }

            _isInitializingSettings = false;
            OnSettingChanged();
        }

        #region Remote Control Client Interaction

        private bool TryGetNormalizedCoordinates(PointerEventArgs e, out float normX, out float normY)
        {
            normX = 0f;
            normY = 0f;

            var screenImage = this.FindControl<Image>("ScreenImage");
            if (screenImage?.Source == null)
                return false;

            var imageSize = screenImage.Source.Size;
            if (imageSize.Width <= 0 || imageSize.Height <= 0)
                return false;

            var controlBounds = screenImage.Bounds;
            if (controlBounds.Width <= 0 || controlBounds.Height <= 0)
                return false;

            var pos = e.GetPosition(screenImage);

            if (screenImage.Stretch == Stretch.Fill)
            {
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

            if (IsOutsideBounds(pos.X, offsetX, renderedW) || IsOutsideBounds(pos.Y, offsetY, renderedH))
            {
                return false;
            }

            normX = (float)Math.Clamp((pos.X - offsetX) / renderedW, 0.0, 1.0);
            normY = (float)Math.Clamp((pos.Y - offsetY) / renderedH, 0.0, 1.0);
            return true;
        }

        private static bool IsOutsideBounds(double pos, double offset, double size) =>
            pos < offset || pos > offset + size;

        private void MainWindow_KeyDown(object? sender, KeyEventArgs e)
        {
            bool hasCtrlAlt = e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Alt);

            if (e.Key == Key.L && hasCtrlAlt)
            {
                ToggleGameMode();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.M && hasCtrlAlt)
            {
                _ = RequestRemoteMonitorSwitchAsync();
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

        private void MainWindow_GlobalPointerMoved(object? sender, PointerEventArgs e)
        {
            if (!_isFullscreen || _isGameMode)
                return;

            var headerBar = this.FindControl<Border>("StreamHeaderBar");
            if (headerBar == null)
                return;

            var pos = e.GetPosition(this);

            // Si el cursor se acerca al borde superior de la pantalla (primeros 20px)
            if (pos.Y <= 20)
            {
                if (!headerBar.IsVisible)
                {
                    headerBar.IsVisible = true;
                }
                _headerAutoHideTimer.Stop();
                _headerAutoHideTimer.Start();
            }
            else if (headerBar.IsVisible)
            {
                double headerHeight = headerBar.Bounds.Height > 0 ? headerBar.Bounds.Height : 50;

                if (pos.Y <= headerHeight)
                {
                    // Mantener visible mientras el cursor estÃƒÂ© interactuando con la barra
                    _headerAutoHideTimer.Stop();
                    _headerAutoHideTimer.Start();
                }
                else if (pos.Y > headerHeight + 25)
                {
                    // Si el cursor baja a la zona de transmisiÃƒÂ³n, ocultar la barra
                    headerBar.IsVisible = false;
                    _headerAutoHideTimer.Stop();
                }
            }
        }

        private void SetLeftSidebarCollapsed(bool collapsed)
        {
            var expandedPanel = this.FindControl<Control>("LeftSidebarExpandedPanel");
            var collapsedPanel = this.FindControl<Control>("LeftSidebarCollapsedPanel");
            if (expandedPanel != null) expandedPanel.IsVisible = !collapsed;
            if (collapsedPanel != null) collapsedPanel.IsVisible = collapsed;
        }

        private void SetRightSidebarCollapsed(bool collapsed)
        {
            var expandedPanel = this.FindControl<Control>("RightSidebarExpandedPanel");
            var collapsedPanel = this.FindControl<Control>("RightSidebarCollapsedPanel");
            if (expandedPanel != null) expandedPanel.IsVisible = !collapsed;
            if (collapsedPanel != null) collapsedPanel.IsVisible = collapsed;
        }

        private void ToggleFullscreen()
        {
            SetFullscreen(!_isFullscreen);
        }

        private void SetFullscreen(bool fullscreen)
        {
            if (_isFullscreen == fullscreen)
                return;

            _isFullscreen = fullscreen;
            var sidebar = this.FindControl<Border>("SidebarBorder");
            var rightSidebar = this.FindControl<Border>("RightSidebarBorder");
            var floatExitBtn = this.FindControl<Button>("ExitFullscreenFloatButton");

            var fullscreenIcon = this.FindControl<PathIcon>("FullscreenButtonIcon");
            var fullscreenText = this.FindControl<TextBlock>("FullscreenButtonText");

            var headerBar = this.FindControl<Border>("StreamHeaderBar");
            var workspaceContainer = this.FindControl<Grid>("WorkspaceContainerGrid");
            var workspaceHeader = this.FindControl<Grid>("WorkspaceHeaderGrid");
            var canvasCardBorder = this.FindControl<Border>("CanvasCardBorder");
            var canvasCardHeader = this.FindControl<Border>("CanvasCardHeader");
            var canvasCardFooter = this.FindControl<Border>("CanvasCardFooter");
            var devicesSection = this.FindControl<Border>("DevicesSectionBorder");
            var bottomFooter = this.FindControl<Grid>("BottomFooterGrid");

            if (_isFullscreen)
            {
                _previousWindowState = this.WindowState;
                this.WindowState = WindowState.FullScreen;

                if (sidebar != null) sidebar.IsVisible = false;
                if (rightSidebar != null) rightSidebar.IsVisible = false;
                if (fullscreenIcon != null) fullscreenIcon.Data = (this.FindResource("IconFullscreenExit") as StreamGeometry)!;
                if (fullscreenText != null) fullscreenText.Text = "Salir";
                if (floatExitBtn != null) floatExitBtn.IsVisible = true;

                if (workspaceHeader != null) workspaceHeader.IsVisible = false;
                if (devicesSection != null) devicesSection.IsVisible = false;
                if (bottomFooter != null) bottomFooter.IsVisible = false;
                if (canvasCardHeader != null) canvasCardHeader.IsVisible = false;
                if (canvasCardFooter != null) canvasCardFooter.IsVisible = false;
                if (workspaceContainer != null) workspaceContainer.Margin = new Thickness(0);
                if (canvasCardBorder != null)
                {
                    canvasCardBorder.CornerRadius = new CornerRadius(0);
                    canvasCardBorder.BorderThickness = new Thickness(0);
                }

                if (headerBar != null)
                {
                    Grid.SetRow(headerBar, 0);
                    Grid.SetRowSpan(headerBar, 2);
                    headerBar.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
                    headerBar.ZIndex = 100;
                    headerBar.Background = new SolidColorBrush(Color.Parse("#F211161F"));
                    headerBar.IsVisible = false; // Permanece oculta hasta que el cursor se acerca arriba
                }
            }
            else
            {
                this.WindowState = _previousWindowState == WindowState.FullScreen ? WindowState.Normal : _previousWindowState;

                if (sidebar != null) sidebar.IsVisible = true;
                if (rightSidebar != null) rightSidebar.IsVisible = true;
                if (fullscreenIcon != null) fullscreenIcon.Data = (this.FindResource("IconFullscreen") as StreamGeometry)!;
                if (fullscreenText != null) fullscreenText.Text = "Pantalla Completa";
                if (floatExitBtn != null) floatExitBtn.IsVisible = false;

                if (workspaceHeader != null) workspaceHeader.IsVisible = true;
                if (devicesSection != null) devicesSection.IsVisible = true;
                if (bottomFooter != null) bottomFooter.IsVisible = true;
                if (canvasCardHeader != null) canvasCardHeader.IsVisible = true;
                if (canvasCardFooter != null) canvasCardFooter.IsVisible = true;
                if (workspaceContainer != null) workspaceContainer.Margin = new Thickness(24, 18, 24, 12);
                if (canvasCardBorder != null)
                {
                    canvasCardBorder.CornerRadius = new CornerRadius(14);
                    canvasCardBorder.BorderThickness = new Thickness(1);
                }

                if (headerBar != null)
                {
                    Grid.SetRow(headerBar, 0);
                    Grid.SetRowSpan(headerBar, 1);
                    headerBar.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
                    headerBar.ZIndex = 0;
                    headerBar.Background = new SolidColorBrush(Color.Parse("#11161F"));
                    headerBar.IsVisible = true;
                }
            }
        }

        private void ToggleStretchMode()
        {
            _currentStretchMode = _currentStretchMode == Stretch.Uniform ? Stretch.Fill : Stretch.Uniform;
            var screenImage = this.FindControl<Image>("ScreenImage");
            if (screenImage != null)
            {
                screenImage.Stretch = _currentStretchMode;
            }

            var stretchBtnText = this.FindControl<TextBlock>("StretchModeButtonText");
            if (stretchBtnText != null)
            {
                stretchBtnText.Text = _currentStretchMode == Stretch.Uniform ? "Ajustar" : "Llenar";
            }

            if (_popoutWindow != null)
            {
                _popoutWindow.SetStretchMode(_currentStretchMode);
            }
            SaveCurrentSettings();
        }

        private static void ToggleAudioMuteHelper(IAudioPlayerService? service, Action updateUi, Action saveSettings)
        {
            if (service != null)
            {
                service.IsMuted = !service.IsMuted;
                if (!service.IsMuted && service.Volume <= 0.01f)
                {
                    service.Volume = 0.5f;
                }
                updateUi();
                saveSettings();
            }
        }

        private static void SetAudioVolumeHelper(IAudioPlayerService? service, float vol, Action updateUi, Action saveSettings)
        {
            if (service != null)
            {
                service.Volume = vol;
                if (vol <= 0.01f)
                {
                    service.IsMuted = true;
                }
                else if (service.IsMuted)
                {
                    service.IsMuted = false;
                }
                updateUi();
                saveSettings();
            }
        }

        private void ToggleMute() => ToggleAudioMuteHelper(_audioPlayerService, UpdateAudioUI, SaveCurrentSettings);

        private void SetAudioVolume(float vol) => SetAudioVolumeHelper(_audioPlayerService, vol, UpdateAudioUI, SaveCurrentSettings);

        private void UpdateAudioUI()
        {
            if (_audioPlayerService == null) return;

            bool isMuted = _audioPlayerService.IsMuted || _audioPlayerService.Volume <= 0.01f;
            int percent = (int)Math.Round(_audioPlayerService.Volume * 100);

            var muteIcon = this.FindControl<PathIcon>("AudioMuteIcon");
            if (muteIcon != null)
            {
                muteIcon.Data = (this.FindResource(isMuted ? "IconVolumeOff" : "IconVolumeUp") as StreamGeometry)!;
            }

            var volumeSlider = this.FindControl<Slider>("AudioVolumeSlider");
            if (volumeSlider != null && Math.Abs(volumeSlider.Value - percent) > 1)
            {
                volumeSlider.Value = percent;
            }

            var volumeText = this.FindControl<TextBlock>("AudioVolumeText");
            if (volumeText != null)
            {
                volumeText.Text = $"{percent}%";
            }

            if (_popoutWindow != null)
            {
                _popoutWindow.SetVolume(_audioPlayerService.Volume, _audioPlayerService.IsMuted);
            }
        }

        private void ToggleMicMute() => ToggleAudioMuteHelper(_micAudioPlayerService, UpdateMicUI, SaveCurrentSettings);

        private void SetMicVolume(float vol) => SetAudioVolumeHelper(_micAudioPlayerService, vol, UpdateMicUI, SaveCurrentSettings);

        private void UpdateMicUI()
        {
            if (_micAudioPlayerService == null) return;

            bool isMuted = _micAudioPlayerService.IsMuted || _micAudioPlayerService.Volume <= 0.01f;
            int percent = (int)Math.Round(_micAudioPlayerService.Volume * 100);

            var muteIcon = this.FindControl<PathIcon>("MicMuteIcon");
            if (muteIcon != null)
            {
                muteIcon.Data = (this.FindResource(isMuted ? "IconMicOff" : "IconMic") as StreamGeometry)!;
                muteIcon.Foreground = new SolidColorBrush(isMuted ? Color.Parse("#EF4444") : Color.Parse("#10B981"));
            }

            var volumeSlider = this.FindControl<Slider>("MicVolumeSlider");
            if (volumeSlider != null && Math.Abs(volumeSlider.Value - percent) > 1)
            {
                volumeSlider.Value = percent;
            }

            var volumeText = this.FindControl<TextBlock>("MicVolumeText");
            if (volumeText != null)
            {
                volumeText.Text = $"{percent}%";
            }
        }

        private void UpdateViewerVoiceChatUI(bool active)
        {
            var icon = this.FindControl<PathIcon>("ViewerVoiceChatIcon");
            var text = this.FindControl<TextBlock>("ViewerVoiceChatText");
            var toggle = this.FindControl<ToggleButton>("ViewerVoiceChatToggle");

            if (icon != null)
            {
                icon.Data = (this.FindResource(active ? "IconMic" : "IconMicOff") as StreamGeometry)!;
                icon.Foreground = new SolidColorBrush(active ? Color.Parse("#22C55E") : Color.Parse("#EF4444"));
            }
            if (text != null)
            {
                text.Text = active ? "Hablando" : "Silenciado";
                text.Foreground = new SolidColorBrush(active ? Color.Parse("#22C55E") : Color.Parse("#94A3B8"));
            }
            if (toggle != null)
            {
                toggle.IsChecked = active;
            }
        }

        private void OpenPopoutWindow()
        {
            if (_isPopoutActive) return;

            if (_isFullscreen)
            {
                SetFullscreen(false);
            }

            _isPopoutActive = true;

            var screenImage = this.FindControl<Image>("ScreenImage");
            var popoutPanel = this.FindControl<StackPanel>("PopoutActivePanel");
            if (screenImage != null) screenImage.IsVisible = false;
            if (popoutPanel != null) popoutPanel.IsVisible = true;

            _popoutWindow = new StreamPopoutWindow();
            _popoutWindow.Title = $"Visor Remoto - {_currentConnectedDeviceName ?? "LanScreenShare"}";
            _popoutWindow.SetRemoteControlAllowed(_currentDeviceAllowsRemoteControl);
            _popoutWindow.SetRemoteControlState(_remoteControlEnabled);
            _popoutWindow.SetGameModeState(_isGameMode);
            _popoutWindow.SetActiveMonitorLabel(_currentRemoteMonitorLabel);
            _popoutWindow.SetVolume(_audioPlayerService?.Volume ?? 1.0f, _audioPlayerService?.IsMuted ?? false);
            _popoutWindow.SetMicVolume(_micAudioPlayerService?.Volume ?? 1.0f, _micAudioPlayerService?.IsMuted ?? false);
            _popoutWindow.SetStretchMode(_currentStretchMode);

            if (screenImage?.Source != null)
            {
                _popoutWindow.UpdateFrame(screenImage.Source);
            }

            _popoutWindow.ReattachRequested += ClosePopoutWindow;
            _popoutWindow.DisconnectRequested += () => DisconnectButton_Click(null, null);

            _popoutWindow.RemoteControlToggled += async (enabled) =>
            {
                var remoteControlBtn = this.FindControl<ToggleButton>("RemoteControlButton");
                if (enabled)
                {
                    bool connected = await EnsureControlConnectedAsync();
                    if (!connected)
                    {
                        _popoutWindow?.SetRemoteControlState(false);
                        if (remoteControlBtn != null) remoteControlBtn.IsChecked = false;
                        _remoteControlEnabled = false;
                        return;
                    }
                    _remoteControlEnabled = true;
                    if (remoteControlBtn != null)
                    {
                        remoteControlBtn.IsChecked = true;
                        remoteControlBtn.Background = new SolidColorBrush(Color.Parse("#2563EB"));
                    }
                    _popoutWindow?.SetRemoteControlState(true);
                }
                else
                {
                    _remoteControlEnabled = false;
                    if (remoteControlBtn != null)
                    {
                        remoteControlBtn.IsChecked = false;
                        remoteControlBtn.Background = new SolidColorBrush(Color.Parse("#27272A"));
                    }
                    if (_isGameMode)
                    {
                        _isGameMode = false;
                        UpdateGameModeUI();
                        _popoutWindow?.SetGameModeState(false);
                    }
                    _popoutWindow?.SetRemoteControlState(false);
                }
            };

            _popoutWindow.GameModeToggled += () =>
            {
                ToggleGameMode();
                _popoutWindow?.SetGameModeState(_isGameMode);
            };

            _popoutWindow.SwitchMonitorRequested += async () =>
            {
                await OpenRemoteSourceMenuAsync(_popoutWindow?.GetSwitchMonitorButton());
            };

            _popoutWindow.CycleMonitorRequested += async () =>
            {
                await RequestRemoteMonitorSwitchAsync();
            };

            _popoutWindow.SendFileRequested += async () =>
            {
                await PickAndSendFilesAsync();
            };

            _popoutWindow.FilesDropped += async (paths) =>
            {
                foreach (var p in paths)
                {
                    await SendFileToPeerAsync(p);
                }
            };

            _popoutWindow.MuteToggled += () =>
            {
                ToggleMute();
            };

            _popoutWindow.VolumeChanged += (vol) =>
            {
                SetAudioVolume(vol);
            };

            _popoutWindow.StretchModeChanged += (stretch) =>
            {
                _currentStretchMode = stretch;
                if (screenImage != null) screenImage.Stretch = stretch;
                var stretchBtnText = this.FindControl<TextBlock>("StretchModeButtonText");
                if (stretchBtnText != null) stretchBtnText.Text = stretch == Stretch.Uniform ? "Ajustar" : "Llenar";
                SaveCurrentSettings();
            };

            _popoutWindow.RemoteInputSent += async (input) =>
            {
                if (_controlClient.IsConnected && _remoteControlEnabled)
                {
                    await _controlClient.SendInputAsync(input);
                }
            };

            _popoutWindow.Show();
        }

        private void ClosePopoutWindow()
        {
            if (!_isPopoutActive) return;

            _isPopoutActive = false;

            var screenImage = this.FindControl<Image>("ScreenImage");
            var popoutPanel = this.FindControl<StackPanel>("PopoutActivePanel");
            if (screenImage != null) screenImage.IsVisible = true;
            if (popoutPanel != null) popoutPanel.IsVisible = false;

            if (_popoutWindow != null)
            {
                var win = _popoutWindow;
                _popoutWindow = null;
                try
                {
                    win.Close();
                }
                catch { }
            }
        }

        private bool OnProvisionalPairingRequested(string fingerprint, X509Certificate2 clientCert)
        {
            // Permitir el establecimiento del canal TLS seguro.
            // La autorizaciÃ³n interactiva y verificaciÃ³n SAS se ejecuta en el canal de aplicaciÃ³n en TcpStreamServer.
            return true;
        }

        private bool OnClientProvisionalPairing(string fingerprint, X509Certificate2 hostCert)
        {
            return true;
        }

        private async Task<bool> RequestPairingApprovalAsync(string deviceName, string fingerprint, string sasFormatted)
        {
            _streamApprovalTcs?.TrySetResult(false);
            var tcs = new TaskCompletionSource<bool>();
            _streamApprovalTcs = tcs;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var banner = this.FindControl<Border>("StreamApprovalBanner");
                var title = this.FindControl<TextBlock>("StreamApprovalTitle");
                var subtitle = this.FindControl<TextBlock>("StreamApprovalSubtitle");
                var sasText = this.FindControl<TextBlock>("StreamApprovalSasCode");

                if (title != null) title.Text = $"Emparejamiento: {deviceName}";
                if (subtitle != null) subtitle.Text = "Compara este cÃƒÂ³digo fuera de banda antes de aprobar:";
                if (sasText != null)
                {
                    sasText.Text = $"CÃƒÂ³digo SAS: {sasFormatted}";
                    sasText.IsVisible = true;
                }
                if (banner != null) banner.IsVisible = true;
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var reg = cts.Token.Register(() => tcs.TrySetResult(false));

            try
            {
                return await tcs.Task;
            }
            finally
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var banner = this.FindControl<Border>("StreamApprovalBanner");
                    var sasText = this.FindControl<TextBlock>("StreamApprovalSasCode");
                    if (sasText != null) sasText.IsVisible = false;
                    if (banner != null) banner.IsVisible = false;
                });
            }
        }

        private async Task<bool> RequestApprovalBannerAsync(
            string bannerName,
            string subtitleName,
            string subtitleText,
            Action<TaskCompletionSource<bool>> setTcs)
        {
            var tcs = new TaskCompletionSource<bool>();
            setTcs(tcs);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var banner = this.FindControl<Border>(bannerName);
                var subtitle = this.FindControl<TextBlock>(subtitleName);
                if (subtitle != null) subtitle.Text = subtitleText;
                if (banner != null) banner.IsVisible = true;
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var reg = cts.Token.Register(() => tcs.TrySetResult(false));

            try
            {
                return await tcs.Task;
            }
            finally
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var banner = this.FindControl<Border>(bannerName);
                    if (banner != null) banner.IsVisible = false;
                });
            }
        }

        private Task<bool> RequestStreamApprovalAsync(string clientId)
        {
            _streamApprovalTcs?.TrySetResult(false);
            return RequestApprovalBannerAsync(
                "StreamApprovalBanner",
                "StreamApprovalSubtitle",
                $"El dispositivo '{clientId}' solicita ver tu pantalla.",
                tcs => _streamApprovalTcs = tcs);
        }

        private Task<bool> RequestControlApprovalAsync(string clientId)
        {
            _controlApprovalTcs?.TrySetResult(false);
            return RequestApprovalBannerAsync(
                "ControlApprovalBanner",
                "ControlApprovalSubtitle",
                $"El dispositivo '{clientId}' solicita controlar tu mouse y teclado.",
                tcs => _controlApprovalTcs = tcs);
        }

        private async Task<bool> EnsureControlConnectedAsync()
        {
            if (_controlClient.IsConnected)
                return true;

            if (string.IsNullOrEmpty(_currentConnectedDeviceIp))
                return false;

            UpdateStatus("Solicitando acceso de control remoto al anfitriÃƒÂ³n...", "#F59E0B");

            try
            {
                var handshake = (_streamClient as TcpStreamClient)?.ActiveSessionHandshake;
                await _controlClient.ConnectAsync(_currentConnectedDeviceIp, 5003, _sslClientOptions, handshake?.SessionId, handshake?.ControlToken);
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
                UpdateStatus($"Conectado a {_currentConnectedDeviceName} â€¢ Control remoto activo", "#22C55E");
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

            var banner = this.FindControl<Border>("GameModeBanner");
            var bannerText = this.FindControl<TextBlock>("GameModeBannerText");
            var screenImage = this.FindControl<Image>("ScreenImage");

            if (_isGameMode)
            {
                _remoteControlEnabled = true;
                var remoteControlBtn = this.FindControl<ToggleButton>("RemoteControlButton");
                if (remoteControlBtn != null)
                {
                    remoteControlBtn.IsChecked = true;
                    remoteControlBtn.Background = new SolidColorBrush(Color.Parse("#2563EB"));
                }

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

                var bannerIcon = this.FindControl<PathIcon>("GameModeBannerIcon");
                _gameModeBannerTimer?.Stop();
                if (banner != null && bannerText != null)
                {
                    banner.IsVisible = true;
                    if (bannerIcon != null)
                    {
                        bannerIcon.Data = (this.FindResource("Icon3dRotation") as StreamGeometry)!;
                        bannerIcon.Foreground = new SolidColorBrush(Color.Parse("#38BDF8"));
                    }
                    bannerText.Text = "Modo Juego 3D Activo Ã¢â‚¬â€ Presiona Ctrl + Alt + L para liberar el mouse";
                    bannerText.Foreground = new SolidColorBrush(Color.Parse("#38BDF8"));
                }
            }
            else
            {
                if (screenImage != null)
                {
                    screenImage.Cursor = Cursor.Default;
                }

                var bannerIcon = this.FindControl<PathIcon>("GameModeBannerIcon");
                if (banner != null && bannerText != null)
                {
                    banner.IsVisible = true;
                    if (bannerIcon != null)
                    {
                        bannerIcon.Data = (this.FindResource("IconLockOpen") as StreamGeometry)!;
                        bannerIcon.Foreground = new SolidColorBrush(Color.Parse("#FBBF24"));
                    }
                    bannerText.Text = "Mouse Liberado Ã¢â‚¬â€ Presiona Ctrl + Alt + L para recapturar";
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

        private bool IsPointerOverFullscreenHeader(PointerEventArgs e)
        {
            if (!_isFullscreen) return false;
            var header = this.FindControl<Border>("StreamHeaderBar");
            if (header == null || !header.IsVisible) return false;
            var pos = e.GetPosition(this);
            double headerHeight = header.Bounds.Height > 0 ? header.Bounds.Height : 50;
            return pos.Y <= headerHeight;
        }

        private void ScreenImage_PointerMoved(object? sender, PointerEventArgs e)
        {
            if (!_remoteControlEnabled || !_controlClient.IsConnected)
                return;

            if (IsPointerOverFullscreenHeader(e))
                return;

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
            if (!_remoteControlEnabled || !_controlClient.IsConnected || IsPointerOverFullscreenHeader(e))
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
            if (!_remoteControlEnabled || !_controlClient.IsConnected || IsPointerOverFullscreenHeader(e))
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

        private void ScreenImage_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            if (!_remoteControlEnabled || !_controlClient.IsConnected)
                return;

            if (IsPointerOverFullscreenHeader(e))
                return;

            short delta = (short)(e.Delta.Y * 120);

            if (_isGameMode)
            {
                _ = _controlClient.SendInputAsync(new RemoteInputMessage
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
                _ = _controlClient.SendInputAsync(new RemoteInputMessage
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

            if (e.Key == Key.Escape && _isFullscreen)
            {
                SetFullscreen(false);
                e.Handled = true;
                return;
            }

            bool hasCtrlAlt = e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Alt);

            if (e.Key == Key.L && hasCtrlAlt)
            {
                ToggleGameMode();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.M && hasCtrlAlt)
            {
                _ = RequestRemoteMonitorSwitchAsync();
                e.Handled = true;
                return;
            }

            if (_remoteControlEnabled && _controlClient.IsConnected)
            {
                int vk = ConvertAvaloniaKeyToWin32Vk(e.Key);
                if (vk > 0)
                {
                    _ = _controlClient.SendInputAsync(new RemoteInputMessage
                    {
                        Type = InputEventType.KeyDown,
                        KeyCode = vk
                    });
                    e.Handled = true;
                }
            }
        }

        private void ScreenImage_KeyUp(object? sender, KeyEventArgs e)
        {
            bool hasCtrlAlt = e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            bool isHotKey = (e.Key == Key.L || e.Key == Key.M);
            if (isHotKey && hasCtrlAlt)
            {
                e.Handled = true;
                return;
            }

            if (_remoteControlEnabled && _controlClient.IsConnected)
            {
                int vk = ConvertAvaloniaKeyToWin32Vk(e.Key);
                if (vk > 0)
                {
                    _ = _controlClient.SendInputAsync(new RemoteInputMessage
                    {
                        Type = InputEventType.KeyUp,
                        KeyCode = vk
                    });
                    e.Handled = true;
                }
            }
        }

        private static int ConvertAvaloniaKeyToWin32Vk(Key key)
        {
            if (TryConvertRangeKey(key, Key.A, Key.Z, 0x41, out int vk) ||
                TryConvertRangeKey(key, Key.D0, Key.D9, 0x30, out vk) ||
                TryConvertRangeKey(key, Key.NumPad0, Key.NumPad9, 0x60, out vk) ||
                TryConvertRangeKey(key, Key.F1, Key.F12, 0x70, out vk))
            {
                return vk;
            }

            return ConvertSpecialKeyToVk(key);
        }

        private static bool TryConvertRangeKey(Key key, Key start, Key end, int baseVk, out int vk)
        {
            if (key >= start && key <= end)
            {
                vk = (int)key - (int)start + baseVk;
                return true;
            }
            vk = 0;
            return false;
        }

        private static int ConvertSpecialKeyToVk(Key key) => key switch
        {
            Key.Back => 0x08,
            Key.Tab => 0x09,
            Key.Return or Key.Enter => 0x0D,
            Key.Escape => 0x1B,
            Key.Space => 0x20,
            Key.Left => 0x25,
            Key.Up => 0x26,
            Key.Right => 0x27,
            Key.Down => 0x28,
            Key.Delete => 0x2E,
            Key.LeftShift or Key.RightShift => 0x10,
            Key.LeftCtrl or Key.RightCtrl => 0x11,
            Key.LeftAlt or Key.RightAlt => 0x12,
            Key.Home => 0x24,
            Key.End => 0x23,
            Key.PageUp => 0x21,
            Key.PageDown => 0x22,
            _ => 0
        };

        #endregion

        #region Stream Settings Logic

        private string GetSelectedEngine()
        {
            var combo = this.FindControl<ComboBox>("EngineComboBox");
            if (combo?.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                return tag;
            }
            return "dxgi";
        }

        private string GetSelectedCodec()
        {
            var combo = this.FindControl<ComboBox>("CodecComboBox");
            if (combo?.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                return tag;
            }
            return "jpeg";
        }

        private int GetSelectedFps()
        {
            var combo = this.FindControl<ComboBox>("FpsComboBox");
            return TryGetSelectedItemTagAsInt(combo, out int fps) ? fps : 30;
        }

        private void InitializeResolutionOptions(CaptureSource? source = null)
        {
            source ??= _currentSource;
            var scaleCombo = this.FindControl<ComboBox>("ScaleComboBox");
            if (scaleCombo == null) return;

            int nativeW = (source != null && source.Width > 0) ? source.Width : 1920;
            int nativeH = (source != null && source.Height > 0) ? source.Height : 1080;

            if (source == null || source.Width <= 0 || source.Height <= 0)
            {
                if (OperatingSystem.IsWindows())
                {
                    int sw = GetSystemMetrics(SM_CXSCREEN);
                    int sh = GetSystemMetrics(SM_CYSCREEN);
                    if (sw > 0 && sh > 0)
                    {
                        nativeW = sw;
                        nativeH = sh;
                    }
                }
                else if (Screens?.Primary != null)
                {
                    nativeW = Screens.Primary.Bounds.Width;
                    nativeH = Screens.Primary.Bounds.Height;
                }
            }

            double nativeAspect = (double)nativeW / Math.Max(1, nativeH);

            var commonResolutions = new (int w, int h, string label)[]
            {
                (3840, 2160, "4K UHD"),
                (2560, 1440, "2K QHD"),
                (2560, 1080, "UltraWide"),
                (1920, 1200, "WUXGA"),
                (1920, 1080, "1080p FHD"),
                (1680, 1050, "WSXGA+"),
                (1600, 900, "900p HD+"),
                (1440, 900, "WXGA+"),
                (1366, 768, "768p Laptop"),
                (1280, 800, "WXGA"),
                (1280, 720, "720p HD"),
                (1024, 768, "XGA"),
                (960, 540, "540p qHD"),
                (854, 480, "480p SD")
            };

            var items = new List<ComboBoxItem>();

            // 1. ResoluciÃ³n nativa como primera opciÃ³n
            items.Add(new ComboBoxItem
            {
                Content = $"{nativeW} Ã— {nativeH} Â· Nativa",
                Tag = $"{nativeW}x{nativeH}"
            });

            // 2. Si es configuraciÃ³n multi-pantalla extendida (aspecto muy ancho > 2.2)
            if (nativeAspect > 2.2)
            {
                var ratios = new (double s, string label)[]
                {
                    (0.75, "75% Extendido"),
                    (0.50, "50% Extendido"),
                    (0.3557, "1366x384 Extendido")
                };
                foreach (var (rScale, rLabel) in ratios)
                {
                    int scaledW = (int)Math.Round((nativeW * rScale) / 2.0) * 2;
                    int scaledH = (int)Math.Round((nativeH * rScale) / 2.0) * 2;
                    if (scaledW > 0 && scaledH > 0 && scaledW < nativeW)
                    {
                        items.Add(new ComboBoxItem
                        {
                            Content = $"{scaledW} Ã— {scaledH} Â· {rLabel}",
                            Tag = $"{scaledW}x{scaledH}"
                        });
                    }
                }
            }
            else
            {
                // Filtrar resoluciones menores compatibles con la relaciÃ³n de aspecto
                foreach (var (w, h, label) in commonResolutions)
                {
                    if (w >= nativeW && h >= nativeH)
                        continue;

                    if (w > nativeW || h > nativeH)
                        continue;

                    double aspect = (double)w / h;
                    if (Math.Abs(aspect - nativeAspect) <= 0.08)
                    {
                        items.Add(new ComboBoxItem
                        {
                            Content = $"{w} Ã— {h} Â· {label}",
                            Tag = $"{w}x{h}"
                        });
                    }
                }
            }

            scaleCombo.ItemsSource = items;

            int selectedIndex = 0;
            if (!string.IsNullOrEmpty(_appSettings.ResolutionTag))
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i].Tag as string == _appSettings.ResolutionTag)
                    {
                        selectedIndex = i;
                        break;
                    }
                }
            }
            scaleCombo.SelectedIndex = selectedIndex;
            GetSelectedResolution(out int targetW, out int targetH, out double scale);
            UpdateResolutionLabels(nativeW, nativeH, targetW, targetH, scale);
        }

        private void GetSelectedResolution(out int targetW, out int targetH, out double scale)
        {
            targetW = 0;
            targetH = 0;
            scale = 1.0;

            var combo = this.FindControl<ComboBox>("ScaleComboBox");
            if (combo?.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                if (tag.Contains('x'))
                {
                    var parts = tag.Split('x');
                    if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
                    {
                        targetW = w;
                        targetH = h;
                        return;
                    }
                }
                else if (double.TryParse(tag, NumberStyles.Any, CultureInfo.InvariantCulture, out double s))
                {
                    scale = s;
                    return;
                }
            }
        }

        private IScreenCaptureService CreateCaptureService(CaptureSource source, string engine)
        {
            var captureService = CaptureServiceFactory.Create(source, engine);
            if (OperatingSystem.IsWindows() && captureService is DxgiScreenCaptureService dxgi)
            {
                dxgi.CaptureFailed += (s, ex) =>
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (_isHosting && _screenCaptureService is DxgiScreenCaptureService)
                        {
                            UpdateStatus($"DXGI no disponible en esta pantalla ({ex.Message}), cambiando a GDI...", "#F59E0B");
                            SwitchCaptureSource(_currentSource, "gdi", new CaptureProfile(GetSelectedFps(), _appSettings.Quality, 1.0, GetSelectedCodec(), 0, 0, _appSettings.BitrateMbps));
                        }
                    });
                };
            }
            return captureService;
        }

        private async void SelectSourceButton_Click(object? sender, RoutedEventArgs e)
        {
            var picker = new SourcePickerWindow(_currentSource.Id);
            var selected = await picker.ShowDialog<CaptureSource?>(this);
            if (selected != null)
            {
                SetCaptureSource(selected);
            }
        }

        private void SetCaptureSource(CaptureSource source)
        {
            _currentSource = source;
            UpdateSourceUI();
            InitializeResolutionOptions(_currentSource);
            SaveCurrentSettings();

            _inputSimulatorService?.SetTargetSource(_currentSource);

            if (_isHosting)
            {
                int fps = GetSelectedFps();
                string engine = GetSelectedEngine();
                string codec = GetSelectedCodec();
                int sliderVal = (int)(this.FindControl<Slider>("QualitySlider")?.Value ?? (codec == "h264" ? 8 : 75));
                int quality = codec == "h264" ? _appSettings.Quality : sliderVal;
                int bitrateMbps = codec == "h264" ? sliderVal : _appSettings.BitrateMbps;
                GetSelectedResolution(out int targetW, out int targetH, out double scale);

                SwitchCaptureSource(source, engine, new CaptureProfile(fps, quality, scale, codec, targetW, targetH, bitrateMbps));

                bool shareAudio = this.FindControl<CheckBox>("AudioShareCheckBox")?.IsChecked ?? true;
                if (shareAudio && _audioCaptureService != null)
                {
                    int targetPid = GetTargetAudioProcessId();
                    _audioCaptureService.StartCapture(targetPid);
                }

                if (_controlServer.IsRunning && _allowRemoteControl)
                {
                    BroadcastHostSourceCatalog();
                }

                UpdateStatus($"Transmitiendo: {source.Name}", "#22C55E");
            }
        }

        private void UpdateSourceUI()
        {
            var sourceNameText = this.FindControl<TextBlock>("SelectedSourceNameText");
            if (sourceNameText != null)
            {
                sourceNameText.Text = _currentSource.Name;
            }

            var sourceSubText = this.FindControl<TextBlock>("SelectedSourceSubText");
            if (sourceSubText != null)
            {
                sourceSubText.Text = _currentSource.Type switch
                {
                    CaptureSourceType.Window => "AplicaciÃ³n",
                    CaptureSourceType.AllScreens => "Escritorio extendido",
                    _ => _currentSource.IsPrimary ? "Principal" : "Secundaria"
                };
            }

            if (!_isViewing)
            {
                var canvasSourceTitle = this.FindControl<TextBlock>("CanvasSourceTitleText");
                if (canvasSourceTitle != null)
                {
                    canvasSourceTitle.Text = _currentSource.Type == CaptureSourceType.Screen && _currentSource.IsPrimary
                        ? $"{_currentSource.Name} (Principal)"
                        : _currentSource.Name;
                }
            }

            var badgeText = this.FindControl<TextBlock>("SourceTypeBadge");
            if (badgeText != null)
            {
                badgeText.Text = _currentSource.Type switch
                {
                    CaptureSourceType.Window => "Ventana",
                    CaptureSourceType.AllScreens => "Escritorio",
                    _ => "Pantalla"
                };
            }

            string iconKey = _currentSource.Type == CaptureSourceType.Window ? "IconWindow" : "IconTv";
            if (this.TryFindResource(iconKey, out var res) && res is StreamGeometry geom)
            {
                var icon = this.FindControl<PathIcon>("SourceIcon");
                if (icon != null)
                {
                    icon.Data = geom;
                    icon.Foreground = new SolidColorBrush(Color.Parse("#CBD5E1"));
                }

                var canvasIcon = this.FindControl<PathIcon>("CanvasSourceIcon");
                if (canvasIcon != null)
                {
                    canvasIcon.Data = geom;
                }
            }
        }

        private void UpdateCanvasFooterAudioStatus()
        {
            bool shareAudio = this.FindControl<CheckBox>("AudioShareCheckBox")?.IsChecked ?? true;
            bool shareMic = this.FindControl<CheckBox>("MicShareCheckBox")?.IsChecked ?? false;

            var audioText = this.FindControl<TextBlock>("CanvasAudioStatusText");
            var audioIcon = this.FindControl<PathIcon>("CanvasAudioStatusIcon");
            if (audioText != null)
            {
                audioText.Text = shareAudio ? "Audio del sistema activado" : "Audio del sistema desactivado";
                audioText.Foreground = new SolidColorBrush(Color.Parse(shareAudio ? "#94A3B8" : "#64748B"));
            }
            if (audioIcon != null)
            {
                audioIcon.Foreground = new SolidColorBrush(Color.Parse(shareAudio ? "#22C55E" : "#64748B"));
            }

            var micText = this.FindControl<TextBlock>("CanvasMicStatusText");
            var micIcon = this.FindControl<PathIcon>("CanvasMicStatusIcon");
            if (micText != null)
            {
                micText.Text = shareMic ? "MicrÃ³fono activado" : "MicrÃ³fono desactivado";
                micText.Foreground = new SolidColorBrush(Color.Parse(shareMic ? "#94A3B8" : "#64748B"));
            }
            if (micIcon != null)
            {
                micIcon.Foreground = new SolidColorBrush(Color.Parse(shareMic ? "#22C55E" : "#64748B"));
            }
        }

        private bool _isUpdatingQualitySlider = false;

        private void UpdateQualitySliderForCodec(string codec)
        {
            var slider = this.FindControl<Slider>("QualitySlider");
            var title = this.FindControl<TextBlock>("QualityTitleLabel");
            var valueLabel = this.FindControl<TextBlock>("QualityValueLabel");
            var bitrateCombo = this.FindControl<ComboBox>("BitrateComboBox");
            if (slider == null || title == null || valueLabel == null) return;

            _isUpdatingQualitySlider = true;
            try
            {
                if (codec == "h264")
                {
                    title.Text = "Tasa de bits";
                    slider.Minimum = 2;
                    slider.Maximum = 25;
                    slider.SmallChange = 1;
                    slider.LargeChange = 5;
                    int val = Math.Clamp(_appSettings.BitrateMbps, 2, 25);
                    slider.Value = val;
                    valueLabel.Text = $"{val} Mbps";

                    if (bitrateCombo != null)
                    {
                        var options = new[] { 3, 5, 8, 12, 15, 20, 25 };
                        var items = new List<ComboBoxItem>();
                        int bestIdx = 0;
                        int bestDiff = int.MaxValue;
                        for (int i = 0; i < options.Length; i++)
                        {
                            items.Add(new ComboBoxItem { Content = $"{options[i]} Mbps", Tag = options[i].ToString() });
                            int diff = Math.Abs(options[i] - val);
                            if (diff < bestDiff)
                            {
                                bestDiff = diff;
                                bestIdx = i;
                            }
                        }
                        bitrateCombo.ItemsSource = items;
                        bitrateCombo.SelectedIndex = bestIdx;
                    }
                }
                else
                {
                    title.Text = "Calidad JPEG";
                    slider.Minimum = 20;
                    slider.Maximum = 95;
                    slider.SmallChange = 5;
                    slider.LargeChange = 10;
                    int val = Math.Clamp(_appSettings.Quality, 20, 95);
                    slider.Value = val;
                    valueLabel.Text = $"{val}%";

                    if (bitrateCombo != null)
                    {
                        var options = new[] { 40, 60, 75, 85, 95 };
                        var items = new List<ComboBoxItem>();
                        int bestIdx = 0;
                        int bestDiff = int.MaxValue;
                        for (int i = 0; i < options.Length; i++)
                        {
                            items.Add(new ComboBoxItem { Content = $"{options[i]}%", Tag = options[i].ToString() });
                            int diff = Math.Abs(options[i] - val);
                            if (diff < bestDiff)
                            {
                                bestDiff = diff;
                                bestIdx = i;
                            }
                        }
                        bitrateCombo.ItemsSource = items;
                        bitrateCombo.SelectedIndex = bestIdx;
                    }
                }
            }
            finally
            {
                _isUpdatingQualitySlider = false;
            }
        }

        public readonly record struct CaptureProfile(int Fps, int Quality, double Scale, string Codec, int TargetWidth = 0, int TargetHeight = 0, int BitrateMbps = 8);

        private void SwitchCaptureSource(CaptureSource source, string engine, in CaptureProfile profile)
        {
            _screenCaptureService.StopCapture();
            _screenCaptureService.FrameCaptured -= ScreenCaptureService_FrameCaptured;
            _screenCaptureService.Dispose();

            int fps = profile.Fps;
            int bitrateMbps = profile.BitrateMbps;

            _abrController.BaseFps = fps;
            _abrController.MaxBitrateMbps = Math.Max(bitrateMbps, _appSettings.MaxAdaptiveBitrateMbps);
            if (_abrController.IsEnabled && profile.Codec == "h264")
            {
                bitrateMbps = _abrController.CurrentBitrateMbps;
                fps = _abrController.TargetFps;
            }

            _screenCaptureService = CreateCaptureService(source, engine);
            _screenCaptureService.FrameCaptured += ScreenCaptureService_FrameCaptured;
            _screenCaptureService.StartCapture(fps, profile.Quality, profile.Scale, profile.Codec, profile.TargetWidth, profile.TargetHeight, bitrateMbps);
            UpdateAbrUI();
        }

        private void OnEngineChanged()
        {
            if (_isHosting)
            {
                int fps = GetSelectedFps();
                string engine = GetSelectedEngine();
                string codec = GetSelectedCodec();
                int sliderVal = (int)(this.FindControl<Slider>("QualitySlider")?.Value ?? (codec == "h264" ? 8 : 75));
                int quality = codec == "h264" ? _appSettings.Quality : sliderVal;
                int bitrateMbps = codec == "h264" ? sliderVal : _appSettings.BitrateMbps;
                GetSelectedResolution(out int targetW, out int targetH, out double scale);

                SwitchCaptureSource(_currentSource, engine, new CaptureProfile(fps, quality, scale, codec, targetW, targetH, bitrateMbps));
                string engineName = engine == "dxgi" ? "DirectX DXGI (GPU)" : "GDI BitBlt (CPU)";
                UpdateStatus($"Cambiado a motor {engineName} ({codec.ToUpper()}) en caliente", "#22C55E");
            }
        }

        private void UpdateAbrUI()
        {
            var qualityTitle = this.FindControl<TextBlock>("QualityTitleLabel");
            if (qualityTitle != null)
            {
                string codec = GetSelectedCodec();
                if (codec == "h264")
                {
                    if (_abrController.IsEnabled)
                    {
                        qualityTitle.Text = _isHosting 
                            ? $"Tasa de bits (ABR: {_abrController.CurrentBitrateMbps} Mbps)"
                            : "Tasa de bits (ABR auto)";
                    }
                    else
                    {
                        qualityTitle.Text = "Tasa de bits";
                    }
                }
                else
                {
                    qualityTitle.Text = "Calidad JPEG";
                }
            }
        }

        private void OnSettingChanged()
        {
            int fps = GetSelectedFps();
            string codec = GetSelectedCodec();
            int sliderVal = (int)(this.FindControl<Slider>("QualitySlider")?.Value ?? (codec == "h264" ? 8 : 75));
            GetSelectedResolution(out int targetW, out int targetH, out double scale);

            var fpsLabel = this.FindControl<TextBlock>("FpsValueLabel");
            if (fpsLabel != null)
            {
                fpsLabel.Text = $"{fps} fps";
            }

            var qualityLabel = this.FindControl<TextBlock>("QualityValueLabel");
            if (qualityLabel != null)
            {
                qualityLabel.Text = codec == "h264" ? $"{sliderVal} Mbps" : $"{sliderVal}%";
            }

            int quality = codec == "h264" ? _appSettings.Quality : sliderVal;
            int bitrateMbps = codec == "h264" ? sliderVal : _appSettings.BitrateMbps;

            if (_abrController.IsEnabled && codec == "h264")
            {
                _abrController.BaseFps = fps;
                _abrController.MaxBitrateMbps = Math.Max(bitrateMbps, _appSettings.MaxAdaptiveBitrateMbps);
                bitrateMbps = _abrController.CurrentBitrateMbps;
                fps = _abrController.TargetFps;
            }

            UpdateAbrUI();

            var scaleLabel = this.FindControl<TextBlock>("ScaleValueLabel");
            if (scaleLabel != null)
            {
                if (targetW > 0 && targetH > 0)
                {
                    scaleLabel.Text = $"{targetW} Ã— {targetH}";
                }
                else
                {
                    scaleLabel.Text = $"{scale * 100:0}%";
                }
            }

            int nativeW = (_currentSource != null && _currentSource.Width > 0) ? _currentSource.Width : 1920;
            int nativeH = (_currentSource != null && _currentSource.Height > 0) ? _currentSource.Height : 1080;
            UpdateResolutionLabels(nativeW, nativeH, targetW, targetH, scale);

            if (!_isApplyingPreset && !_isInitializingSettings)
            {
                _appSettings.TransmissionPreset = "custom";
                UpdatePresetButtonsHighlight("custom");
            }

            _screenCaptureService.UpdateSettings(fps, quality, scale, codec, targetW, targetH, bitrateMbps);
        }

        private void SaveCurrentSettings()
        {
            if (_isInitializingSettings)
                return;

            try
            {
                _appSettings.Engine = GetSelectedEngine();
                _appSettings.Codec = GetSelectedCodec();
                _appSettings.Fps = GetSelectedFps();
                string curCodec = GetSelectedCodec();
                int currentSliderVal = (int)(this.FindControl<Slider>("QualitySlider")?.Value ?? (curCodec == "h264" ? 8 : 75));
                if (curCodec == "h264")
                {
                    _appSettings.BitrateMbps = currentSliderVal;
                }
                else
                {
                    _appSettings.Quality = currentSliderVal;
                }

                var scaleCombo = this.FindControl<ComboBox>("ScaleComboBox");
                if (scaleCombo?.SelectedItem is ComboBoxItem scaleItem && scaleItem.Tag is string tag)
                {
                    _appSettings.ResolutionTag = tag;
                }

                _appSettings.ShareAudio = this.FindControl<CheckBox>("AudioShareCheckBox")?.IsChecked ?? true;
                _appSettings.ShareMicrophone = this.FindControl<CheckBox>("MicShareCheckBox")?.IsChecked ?? false;
                _appSettings.ViewerVoiceChatEnabled = this.FindControl<CheckBox>("ViewerVoiceChatCheckBox")?.IsChecked ?? true;
                _appSettings.AllowRemoteControl = this.FindControl<CheckBox>("AllowRemoteControlCheckBox")?.IsChecked ?? true;
                _appSettings.ClipboardSyncEnabled = this.FindControl<CheckBox>("ClipboardSyncCheckBox")?.IsChecked ?? true;
                _appSettings.EnableAdaptiveBitrate = this.FindControl<CheckBox>("AbrCheckBox")?.IsChecked ?? true;

                var streamAccessCombo = this.FindControl<ComboBox>("StreamAccessModeComboBox");
                if (streamAccessCombo?.SelectedItem is ComboBoxItem streamItem && streamItem.Tag is string streamTag)
                {
                    _appSettings.StreamAccessMode = streamTag;
                }

                var controlAccessCombo = this.FindControl<ComboBox>("ControlAccessModeComboBox");
                if (controlAccessCombo?.SelectedItem is ComboBoxItem controlItem && controlItem.Tag is string controlTag)
                {
                    _appSettings.ControlAccessMode = controlTag;
                }

                _appSettings.AudioVolume = _audioPlayerService?.Volume ?? 1.0f;
                _appSettings.AudioMuted = _audioPlayerService?.IsMuted ?? false;
                _appSettings.MicVolume = _micAudioPlayerService?.Volume ?? 1.0f;
                _appSettings.MicMuted = _micAudioPlayerService?.IsMuted ?? false;
                _appSettings.StretchMode = _currentStretchMode == Stretch.Fill ? "Fill" : "Uniform";
                _appSettings.CaptureSourceId = _currentSource.Id;
                _appSettings.CaptureSourceName = _currentSource.Name;

                _appSettings.TransmissionPreset = string.IsNullOrEmpty(_appSettings.TransmissionPreset) ? "balanced" : _appSettings.TransmissionPreset;
                _appSettings.ShowAdvancedSettings = this.FindControl<StackPanel>("AdvancedSettingsPanel")?.IsVisible ?? false;

                SettingsService.Save(_appSettings);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Settings] Error al guardar configuraciÃƒÂ³n actual: {ex.Message}");
            }
        }

        private static string FormatTransferRate(long bytes)
        {
            double kbPerSec = bytes / 1024.0;
            return kbPerSec >= 1024 ? $"{(kbPerSec / 1024.0):F1} MB/s" : $"{kbPerSec:F0} KB/s";
        }

        private void GetActiveRttAndQualityColor(out int rtt, out string qualityColor)
        {
            rtt = 0;
            if (_isHosting)
            {
                rtt = _streamServer.CurrentRttMs;
            }
            else if (_streamClient != null && _streamClient.IsConnected)
            {
                rtt = _streamClient.CurrentTelemetry.RttMs;
            }

            qualityColor = rtt <= 0 ? "#10B981"
                : rtt < 30 ? "#10B981"
                : rtt < 60 ? "#3B82F6"
                : rtt < 90 ? "#F59E0B"
                : "#EF4444";
        }

        private string FormatFpsInfo(long frames)
        {
            if (_isHosting)
                return $"Host: {frames} FPS";

            return _lastHostFps > 0
                ? $"Host: {_lastHostFps} FPS ({_lastHostFrameTimeMs} ms) • Red: {frames} FPS"
                : $"{frames} FPS";
        }

        private string FormatCodecTag()
        {
            if (!_isHosting || _screenCaptureService == null) return "";
            string encDesc = _screenCaptureService.ActiveEncoderDescription ?? _screenCaptureService.Codec.ToUpper();
            return _screenCaptureService.IsHardwareAccelerated ? $" • ⚡ {encDesc}" : $" • {encDesc}";
        }

        private string FormatAbrTag()
        {
            if (!_isHosting || !_abrController.IsEnabled) return "";
            string stateIcon = _abrController.CurrentState switch
            {
                AbrNetworkState.Optimal => "⚡",
                AbrNetworkState.Moderate => "⚖️",
                AbrNetworkState.Congested => "⚠️",
                AbrNetworkState.SevereCongestion => "🚨",
                _ => "⚡"
            };
            return $" • ABR: {_abrController.CurrentBitrateMbps} Mbps {stateIcon}";
        }

        private void MetricsTimer_Tick(object? sender, EventArgs e)
        {
            long frames = Interlocked.Exchange(ref _metricFramesCount, 0);
            long bytes = Interlocked.Exchange(ref _metricBytesCount, 0);

            var statsBadge = this.FindControl<Border>("StatsBadge");
            var statsText = this.FindControl<TextBlock>("StatsText");
            var statsDot = this.FindControl<Avalonia.Controls.Shapes.Ellipse>("StatsQualityDot");

            if (!_isHosting && (_streamClient == null || !_streamClient.IsConnected))
            {
                if (statsBadge != null) statsBadge.IsVisible = false;
                return;
            }

            if (statsBadge != null && statsText != null)
            {
                statsBadge.IsVisible = true;
                string transferRate = FormatTransferRate(bytes);
                GetActiveRttAndQualityColor(out int rtt, out string qualityColor);
                string rttTag = rtt > 0 ? $"RTT: {rtt} ms • " : "";

                if (statsDot != null && Avalonia.Media.Color.TryParse(qualityColor, out var parsedColor))
                {
                    statsDot.Fill = new Avalonia.Media.SolidColorBrush(parsedColor);
                }

                string fpsInfo = FormatFpsInfo(frames);
                string res = _lastFrameWidth > 0 ? $" • {_lastFrameWidth}x{_lastFrameHeight}" : "";
                string engineTag = _isHosting
                    ? (_screenCaptureService is DxgiScreenCaptureService ? " • DXGI GPU" : " • GDI CPU")
                    : "";
                string codecTag = FormatCodecTag();
                string audioTag = (_isHosting && _audioServer.IsRunning) || (_streamClient != null && _streamClient.IsConnected && _audioClient.IsConnected)
                    ? " • Audio"
                    : "";
                string controlTag = _controlClient.IsConnected && _remoteControlEnabled ? " • Control Activo" : "";
                string dropsTag = (_isHosting && _streamServer.DroppedFramesCount > 0) ? $" • Descartados: {_streamServer.DroppedFramesCount}" : "";
                string avSyncTag = (!_isHosting && _audioClient.IsConnected) ? $" • A/V: {_avSyncController.CurrentDriftMs} ms" : "";
                string abrTag = FormatAbrTag();

                statsText.Text = $"{rttTag}{fpsInfo} • {transferRate}{res}{engineTag}{codecTag}{abrTag}{audioTag}{avSyncTag}{controlTag}{dropsTag}";

                if (_isPopoutActive && _popoutWindow != null)
                {
                    _popoutWindow.UpdateMetrics(statsText.Text, qualityColor);
                }
            }

            if (_isViewing && _lastFrameWidth > 0 && _lastFrameHeight > 0)
            {
                var canvasSpecs = this.FindControl<TextBlock>("CanvasSpecsText");
                if (canvasSpecs != null)
                {
                    int displayFps = frames > 0 ? (int)frames : (_lastHostFps > 0 ? _lastHostFps : 0);
                    canvasSpecs.Text = $"{_lastFrameWidth} × {_lastFrameHeight} · {displayFps} fps";
                }
            }
        }
        private void ResetMetrics()
        {
            Interlocked.Exchange(ref _metricFramesCount, 0);
            Interlocked.Exchange(ref _metricBytesCount, 0);
            _lastFrameWidth = 0;
            _lastFrameHeight = 0;
            _lastHostFps = 0;
            _lastHostFrameTimeMs = 0;
            _avSyncController.Reset();

            var statsBadge = this.FindControl<Border>("StatsBadge");
            if (statsBadge != null)
            {
                statsBadge.IsVisible = false;
            }
        }

        #endregion

        #region Hosting Logic

        private void UpdateHostButtonsUI(bool isHosting)
        {
            var hostBtn = this.FindControl<Button>("HostButton");
            var hostBtnText = this.FindControl<TextBlock>("HostButtonText");
            var centerHostBtn = this.FindControl<Button>("CenterHostButton");
            var centerHostBtnText = this.FindControl<TextBlock>("CenterHostButtonText");

            var bg = new SolidColorBrush(Color.Parse(isHosting ? "#DC2626" : "#3B82F6"));
            string label = isHosting ? "Detener transmisiÃ³n" : "Transmitir mi pantalla";

            if (hostBtn != null) hostBtn.Background = bg;
            if (hostBtnText != null) hostBtnText.Text = label;

            if (centerHostBtn != null) centerHostBtn.Background = bg;
            if (centerHostBtnText != null) centerHostBtnText.Text = label;
        }

        private void UpdateStreamStateBadge()
        {
            var badgeText = this.FindControl<TextBlock>("StreamStateBadgeText");
            var badgeDot = this.FindControl<Ellipse>("StreamStateDot");

            if (_isHosting)
            {
                if (badgeText != null) badgeText.Text = "Transmitiendo";
                if (badgeDot != null) badgeDot.Fill = new SolidColorBrush(Color.Parse("#22C55E"));
            }
            else if (_isViewing)
            {
                if (badgeText != null) badgeText.Text = "Visualizando";
                if (badgeDot != null) badgeDot.Fill = new SolidColorBrush(Color.Parse("#3B82F6"));
            }
            else
            {
                if (badgeText != null) badgeText.Text = "Sin transmisiÃ³n";
                if (badgeDot != null) badgeDot.Fill = new SolidColorBrush(Color.Parse("#7C8B9E"));
            }
        }

        private async void HostButton_Click(object? sender, RoutedEventArgs e)
        {
            if (!_isHosting)
            {
                // Iniciar Host
                try
                {
                    if (_isViewing)
                    {
                        await HandleViewerDisconnectedAsync("Iniciando transmisiÃ³n local...");
                    }

                    _isHosting = true;
                    _isViewing = false;

                    int fps = GetSelectedFps();
                    string engine = GetSelectedEngine();
                    string codec = GetSelectedCodec();
                    int sliderVal = (int)(this.FindControl<Slider>("QualitySlider")?.Value ?? (codec == "h264" ? 8 : 75));
                    int quality = codec == "h264" ? _appSettings.Quality : sliderVal;
                    int bitrateMbps = codec == "h264" ? sliderVal : _appSettings.BitrateMbps;
                    GetSelectedResolution(out int targetW, out int targetH, out double scale);

                    // 1. Iniciar servidor de video (Puerto 5001)
                    await _streamServer.StartAsync(5001);
                    _abrController.Reset(initialBitrateMbps: bitrateMbps, baseFps: fps);
                    _abrController.MaxBitrateMbps = Math.Max(bitrateMbps, _appSettings.MaxAdaptiveBitrateMbps);
                    _abrController.MinBitrateMbps = _appSettings.MinAdaptiveBitrateMbps;
                    _abrController.IsEnabled = _appSettings.EnableAdaptiveBitrate;
                    UpdateAbrUI();
                    SwitchCaptureSource(_currentSource, engine, new CaptureProfile(fps, quality, scale, codec, targetW, targetH, bitrateMbps));
                    _inputSimulatorService?.SetTargetSource(_currentSource);

                    // 2. Iniciar servidor y captura de audio (Puerto 5002)
                    bool shareAudio = this.FindControl<CheckBox>("AudioShareCheckBox")?.IsChecked ?? true;
                    bool shareMic = this.FindControl<CheckBox>("MicShareCheckBox")?.IsChecked ?? false;
                    try
                    {
                        await _audioServer.StartAsync(5002);
                        if (shareAudio)
                        {
                            int targetPid = GetTargetAudioProcessId();
                            _audioCaptureService?.StartCapture(targetPid);
                        }
                        if (shareMic)
                        {
                            _micCaptureService?.StartCapture();
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Audio] Advertencia iniciando audio: {ex.Message}");
                    }

                    // 3. Iniciar servidor de control remoto (Puerto 5003) si estÃ¡ habilitado
                    if (_allowRemoteControl)
                    {
                        await _controlServer.StartAsync(5003);
                    }

                    // 4. Iniciar servidor de transferencia de archivos (Puerto 5004)
                    try
                    {
                        await _fileTransferServer.StartAsync(5004);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[FileTransfer] Advertencia iniciando servidor de archivos: {ex.Message}");
                    }

                    _discoveryService.StartHostingBroadcast();

                    ResetMetrics();

                    var placeholderTitle = this.FindControl<TextBlock>("PlaceholderTitleText");
                    var placeholderSubtitle = this.FindControl<TextBlock>("PlaceholderSubtitleText");
                    if (placeholderTitle != null) placeholderTitle.Text = "Transmitiendo tu pantalla";
                    if (placeholderSubtitle != null) placeholderSubtitle.Text = "Tu pantalla estÃ¡ siendo compartida en tu red local.\nLos equipos conectados la verÃ¡n en tiempo real.";

                    UpdateHostButtonsUI(true);
                    UpdateStreamStateBadge();

                    string engineName = engine == "dxgi" ? "DirectX DXGI" : "GDI BitBlt";
                    UpdateStatus($"Transmitiendo {_currentSource.Name} ({engineName}) Â· Esperando visores", "#22C55E");
                }
                catch (Exception ex)
                {
                    _isHosting = false;
                    UpdateStatus($"Error al iniciar transmisiÃ³n: {ex.Message}", "#EF4444");
                }
            }
            else
            {
                // Detener Host
                _screenCaptureService.StopCapture();
                _streamServer.Stop();

                _audioCaptureService?.StopCapture();
                _micCaptureService?.StopCapture();
                _audioServer.Stop();

                _controlServer.Stop();
                _fileTransferServer.Stop();

                var sendFileBtn = this.FindControl<Button>("SendFileButton");
                if (sendFileBtn != null) sendFileBtn.IsVisible = false;

                _discoveryService.StopHosting();

                _isHosting = false;
                ResetMetrics();
                UpdateAbrUI();

                var placeholderTitle = this.FindControl<TextBlock>("PlaceholderTitleText");
                var placeholderSubtitle = this.FindControl<TextBlock>("PlaceholderSubtitleText");
                if (placeholderTitle != null) placeholderTitle.Text = "Todo listo para compartir";
                if (placeholderSubtitle != null) placeholderSubtitle.Text = "Comparte una pantalla o una aplicaciÃ³n con otros equipos.\nTÃº eliges quÃ© mostrar y quiÃ©n puede controlarlo.";

                UpdateHostButtonsUI(false);
                UpdateStreamStateBadge();

                UpdateStatus("Sin conexiÃ³n activa", "#34D399");
            }
        }

        private async void ScreenCaptureService_FrameCaptured(object? sender, CapturedFrame frame)
        {
            if (_isHosting && _streamServer.IsRunning)
            {
                Interlocked.Increment(ref _metricFramesCount);
                Interlocked.Add(ref _metricBytesCount, frame.Data.Length);
                _lastFrameWidth = frame.Width;
                _lastFrameHeight = frame.Height;

                await _streamServer.BroadcastFrameAsync(frame);
            }
        }

        private void StreamServer_ClientConnected(object? sender, string clientId)
        {
            _screenCaptureService.RequestKeyframe();
            Dispatcher.UIThread.Post(() =>
            {
                int count = _streamServer.ConnectedClientsCount;
                var sendFileBtn = this.FindControl<Button>("SendFileButton");
                if (sendFileBtn != null && _isHosting)
                {
                    sendFileBtn.IsVisible = count > 0;
                }
                UpdateStatus($"Transmitiendo pantalla Â· {count} espectador(es) conectado(s)", "#22C55E");
            });
        }

        private void StreamServer_ClientDisconnected(object? sender, string clientId)
        {
            Dispatcher.UIThread.Post(() =>
            {
                int count = _streamServer.ConnectedClientsCount;
                if (_isHosting)
                {
                    var sendFileBtn = this.FindControl<Button>("SendFileButton");
                    if (sendFileBtn != null)
                    {
                        sendFileBtn.IsVisible = count > 0;
                    }

                    if (count > 0)
                    {
                        UpdateStatus($"Transmitiendo pantalla Â· {count} espectador(es) conectado(s)", "#22C55E");
                    }
                    else
                    {
                        UpdateStatus("Transmitiendo pantalla Â· Esperando visores", "#22C55E");
                    }
                }
            });
        }

        #endregion

        #region Discovery & Client Viewing Logic

        private void UpdateDevicesSectionUI()
        {
            var countText = this.FindControl<TextBlock>("DevicesCountText");
            if (countText != null)
            {
                countText.Text = Devices.Count == 1 ? "1 encontrado" : $"{Devices.Count} encontrados";
            }

            var placeholder = this.FindControl<Control>("NoDevicesPlaceholder");
            if (placeholder != null)
            {
                placeholder.IsVisible = Devices.Count == 0;
            }

            var list = this.FindControl<ListBox>("DevicesList");
            if (list != null)
            {
                list.IsVisible = Devices.Count > 0;
            }

            UpdateDevicesViewingState();
        }

        private void UpdateDevicesViewingState()
        {
            foreach (var d in Devices)
            {
                d.IsViewing = _isViewing && !string.IsNullOrEmpty(_currentConnectedDeviceIp) && d.IPAddress == _currentConnectedDeviceIp;
            }
        }

        private void StopDeviceSearch()
        {
            _searchTimer?.Stop();
            _searchTimer = null;
            _discoveryService.StopListening();

            var searchBtn = this.FindControl<Button>("SearchButton");
            var searchBtnText = this.FindControl<TextBlock>("SearchButtonText");
            if (searchBtnText != null)
            {
                searchBtnText.Text = "Buscar dispositivos";
            }
            if (searchBtn != null)
            {
                searchBtn.IsEnabled = true;
            }

            var localInfo = this.FindControl<TextBlock>("LocalInfoText");
            if (localInfo != null)
            {
                localInfo.Text = Devices.Count > 0
                    ? $"Listo para transmitir o recibir Â· {Devices.Count} detectado(s)"
                    : "Listo para transmitir o recibir";
            }
        }

        private void SearchButton_Click(object? sender, RoutedEventArgs e)
        {
            _searchTimer?.Stop();
            _discoveryService.StartListening();

            var searchBtn = this.FindControl<Button>("SearchButton");
            var searchBtnText = this.FindControl<TextBlock>("SearchButtonText");
            if (searchBtnText != null)
            {
                searchBtnText.Text = "Buscando en LAN...";
            }
            if (searchBtn != null)
            {
                searchBtn.IsEnabled = false;
            }

            var localInfo = this.FindControl<TextBlock>("LocalInfoText");
            if (localInfo != null)
            {
                localInfo.Text = "Buscando equipos en tu red local (UDP 5000)...";
            }

            // Auto-detener bÃºsqueda despuÃ©s de 10 segundos para no quedar en bucle
            _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            _searchTimer.Tick += (s, args) =>
            {
                StopDeviceSearch();
            };
            _searchTimer.Start();
        }

        private void DiscoveryService_OnDeviceDiscovered(object? sender, DeviceInfo device)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                var existing = Devices.FirstOrDefault(d => d.IPAddress == device.IPAddress);
                if (existing != null)
                {
                    existing.Name = device.Name;
                    existing.LastSeen = device.LastSeen;
                    existing.IsRemoteControlAllowed = device.IsRemoteControlAllowed;
                }
                else
                {
                    Devices.Add(device);
                }

                UpdateDevicesSectionUI();

                if (_streamClient.IsConnected && _currentConnectedDeviceIp == device.IPAddress)
                {
                    _currentDeviceAllowsRemoteControl = device.IsRemoteControlAllowed;

                    var remoteControlBtn = this.FindControl<ToggleButton>("RemoteControlButton");
                    if (remoteControlBtn != null)
                    {
                        remoteControlBtn.IsVisible = _currentDeviceAllowsRemoteControl;
                        if (!_currentDeviceAllowsRemoteControl) remoteControlBtn.IsChecked = false;
                    }

                    var gameModeBtn = this.FindControl<ToggleButton>("GameModeButton");
                    if (gameModeBtn != null)
                    {
                        gameModeBtn.IsVisible = _currentDeviceAllowsRemoteControl;
                        if (!_currentDeviceAllowsRemoteControl) gameModeBtn.IsChecked = false;
                    }

                    var switchMonitorBtn = this.FindControl<Button>("SwitchMonitorButton");
                    if (switchMonitorBtn != null)
                    {
                        switchMonitorBtn.IsVisible = _currentDeviceAllowsRemoteControl;
                    }

                    _popoutWindow?.SetRemoteControlAllowed(_currentDeviceAllowsRemoteControl);
                }
            });
        }

        private async void ConnectDevice_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is DeviceInfo device)
            {
                if (device.IsViewing)
                {
                    return;
                }
                await ConnectToDeviceAsync(device);
            }
        }

        private async Task ConnectToDeviceAsync(DeviceInfo device)
        {
            StopDeviceSearch();
            _currentConnectedDeviceName = device.Name;
            _currentConnectedDeviceIp = device.IPAddress;
            _currentDeviceAllowsRemoteControl = device.IsRemoteControlAllowed;
            UpdateStatus($"Conectando a {device.Name} ({device.IPAddress}:5001)...", "#F59E0B");
            ResetMetrics();

            var placeholderTitle = this.FindControl<TextBlock>("PlaceholderTitleText");
            var placeholderSubtitle = this.FindControl<TextBlock>("PlaceholderSubtitleText");
            var placeholderActions = this.FindControl<StackPanel>("PlaceholderActionsPanel");
            if (placeholderTitle != null) placeholderTitle.Text = "Conectando al anfitriÃ³n...";
            if (placeholderSubtitle != null) placeholderSubtitle.Text = $"Estableciendo conexiÃ³n segura con {device.Name} ({device.IPAddress})...";
            if (placeholderActions != null) placeholderActions.IsVisible = false;

            try
            {
                // 1. Conectar flujo de video
                await _streamClient.ConnectAsync(device.IPAddress, 5001, _sslClientOptions);
                _isViewing = true;
                UpdateStreamStateBadge();
                UpdateDevicesViewingState();

                var canvasSourceTitle = this.FindControl<TextBlock>("CanvasSourceTitleText");
                if (canvasSourceTitle != null)
                {
                    canvasSourceTitle.Text = $"Viendo: {device.Name}";
                }

                if (placeholderSubtitle != null)
                {
                    placeholderSubtitle.Text = $"Conectado a {device.Name}. Esperando primer fotograma...";
                }

                var fullscreenBtn = this.FindControl<Button>("FullscreenButton");
                if (fullscreenBtn != null)
                {
                    fullscreenBtn.IsVisible = true;
                }

                var stretchBtn = this.FindControl<Button>("StretchModeButton");
                if (stretchBtn != null)
                {
                    stretchBtn.IsVisible = true;
                }

                var popoutBtn = this.FindControl<Button>("PopoutWindowButton");
                if (popoutBtn != null)
                {
                    popoutBtn.IsVisible = true;
                }

                var disconnectBtn = this.FindControl<Button>("DisconnectButton");
                if (disconnectBtn != null)
                {
                    disconnectBtn.IsVisible = true;
                }

                var audioPanel = this.FindControl<Border>("AudioControlPanel");
                if (audioPanel != null)
                {
                    audioPanel.IsVisible = true;
                }
                UpdateAudioUI();

                var micPanel = this.FindControl<Border>("MicControlPanel");
                if (micPanel != null)
                {
                    micPanel.IsVisible = true;
                }
                UpdateMicUI();

                var viewerVoicePanel = this.FindControl<Border>("ViewerVoiceChatPanel");
                if (viewerVoicePanel != null)
                {
                    viewerVoicePanel.IsVisible = true;
                }
                UpdateViewerVoiceChatUI(_isViewerVoiceChatActive);

                var remoteControlBtn = this.FindControl<ToggleButton>("RemoteControlButton");
                if (remoteControlBtn != null)
                {
                    remoteControlBtn.IsVisible = _currentDeviceAllowsRemoteControl;
                    remoteControlBtn.IsChecked = false;
                    remoteControlBtn.IsEnabled = true;
                    ToolTip.SetTip(remoteControlBtn, "Habilitar interacciÃ³n con mouse y teclado");
                }

                var gameModeBtn = this.FindControl<ToggleButton>("GameModeButton");
                if (gameModeBtn != null)
                {
                    gameModeBtn.IsVisible = _currentDeviceAllowsRemoteControl;
                    gameModeBtn.IsChecked = false;
                    gameModeBtn.IsEnabled = true;
                }

                var switchMonitorBtn = this.FindControl<Button>("SwitchMonitorButton");
                if (switchMonitorBtn != null)
                {
                    switchMonitorBtn.IsVisible = _currentDeviceAllowsRemoteControl;
                    switchMonitorBtn.IsEnabled = true;
                }

                var sendFileBtn = this.FindControl<Button>("SendFileButton");
                if (sendFileBtn != null)
                {
                    sendFileBtn.IsVisible = true;
                    sendFileBtn.IsEnabled = true;
                }

                var handshake = (_streamClient as TcpStreamClient)?.ActiveSessionHandshake;

                // 2. Conectar flujo de audio en segundo plano sin demorar la visualizaciÃ³n de video
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var audioCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await _audioClient.ConnectAsync(device.IPAddress, 5002, _sslClientOptions, handshake?.SessionId, handshake?.AudioToken, audioCts.Token);
                        if (_isViewerVoiceChatActive)
                        {
                            _micCaptureService?.StartCapture();
                            Console.WriteLine("[Viewer] MicrÃ³fono de retorno conectado e iniciado.");
                        }
                        Dispatcher.UIThread.Post(UpdateAudioUI);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[AudioClient] Audio no disponible en el host: {ex.Message}");
                    }
                });

                // 3. Conectar canal de transferencia de archivos en segundo plano
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var fileCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await _fileTransferClient.ConnectAsync(device.IPAddress, 5004, _sslClientOptions, handshake?.SessionId, handshake?.FileTransferToken, fileCts.Token);
                        Console.WriteLine($"[Viewer] Canal de transferencia de archivos conectado a {device.IPAddress}:5004.");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[FileTransferClient] Canal de archivos no disponible en el host: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                string detail = ex.InnerException != null ? $"{ex.Message} ({ex.InnerException.Message})" : ex.Message;
                HandleViewerDisconnected($"No se pudo conectar a {device.Name}: {detail}");
                Console.WriteLine($"[ConnectError] No se pudo conectar a {device.Name}: {ex}");
            }
        }

        private void StreamClient_FrameReceived(object? sender, CapturedFrame frame)
        {
            Interlocked.Increment(ref _metricFramesCount);
            Interlocked.Add(ref _metricBytesCount, frame.Data.Length);
            _lastFrameWidth = frame.Width;
            _lastFrameHeight = frame.Height;
            _lastHostFps = frame.HostFps;
            _lastHostFrameTimeMs = frame.HostFrameTimeMs;
            _avSyncController.OnVideoFrameReceived(frame.Timestamp);

            try
            {
                if (frame.Format == "h264")
                {
                    HandleH264FrameReceived(frame);
                }
                else
                {
                    HandleJpegFrameReceived(frame);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StreamViewer] Error renderizando frame: {ex.Message}");
            }
        }

        private void HandleH264FrameReceived(CapturedFrame frame)
        {
            if (_h264Decoder == null || !_h264Decoder.TryDecodeFrame(frame.Data, frame.Width, frame.Height, out byte[] bgraPixels, out int decW, out int decH))
            {
                return;
            }

            lock (_bitmapLock)
            {
                if (_latestDecodedBgra == null || _latestDecodedBgra.Length != bgraPixels.Length)
                {
                    _latestDecodedBgra = new byte[bgraPixels.Length];
                }
                Buffer.BlockCopy(bgraPixels, 0, _latestDecodedBgra, 0, bgraPixels.Length);
                _latestDecodedWidth = decW;
                _latestDecodedHeight = decH;
            }

            if (Interlocked.Exchange(ref _isUiRenderPending, 1) == 0)
            {
                Dispatcher.UIThread.Post(RenderLatestH264Frame, DispatcherPriority.Render);
            }
        }

        private void RenderLatestH264Frame()
        {
            Interlocked.Exchange(ref _isUiRenderPending, 0);
            try
            {
                lock (_bitmapLock)
                {
                    if (_latestDecodedBgra == null || _latestDecodedWidth <= 0 || _latestDecodedHeight <= 0)
                        return;

                    EnsureViewerWriteableBitmap(_latestDecodedWidth, _latestDecodedHeight);

                    using var fb = _viewerWriteableBitmap!.Lock();
                    int copyLength = Math.Min(_latestDecodedBgra.Length, fb.RowBytes * _latestDecodedHeight);
                    Marshal.Copy(_latestDecodedBgra, 0, fb.Address, copyLength);
                }

                ApplyViewerFrameToUi(_viewerWriteableBitmap, "H.264 GPU");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StreamViewer] Error renderizando frame H.264: {ex.Message}");
            }
        }

        private void EnsureViewerWriteableBitmap(int width, int height)
        {
            if (_viewerWriteableBitmap != null &&
                _viewerWriteableBitmap.PixelSize.Width == width &&
                _viewerWriteableBitmap.PixelSize.Height == height)
            {
                return;
            }

            _viewerWriteableBitmap?.Dispose();
            _viewerWriteableBitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);
        }

        private void HandleJpegFrameReceived(CapturedFrame frame)
        {
            using var ms = new MemoryStream(frame.Data);
            var bitmap = new Bitmap(ms);

            Dispatcher.UIThread.Post(() => ApplyViewerFrameToUi(bitmap, "JPEG"));
        }

        private void ApplyViewerFrameToUi(IImage? frameImage, string codecLabel)
        {
            if (frameImage == null) return;

            var screenImage = this.FindControl<Image>("ScreenImage");
            if (screenImage != null)
            {
                if (screenImage.Source != frameImage)
                {
                    screenImage.Source = frameImage;
                }
                screenImage.InvalidateVisual();
            }

            if (_isPopoutActive && _popoutWindow != null)
            {
                _popoutWindow.UpdateFrame(frameImage);
            }

            var placeholder = this.FindControl<StackPanel>("PlaceholderPanel");
            if (placeholder != null && placeholder.IsVisible)
            {
                placeholder.IsVisible = false;
            }

            UpdateStatus($"Recibiendo pantalla de {_currentConnectedDeviceName} ({codecLabel})", "#22C55E");
        }
        private void StreamClient_StatusChanged(object? sender, string status)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (status.StartsWith("Conectando", StringComparison.OrdinalIgnoreCase))
                {
                    UpdateStatus(status, "#F59E0B");
                    return;
                }

                if (!_streamClient.IsConnected)
                {
                    // Si Ã©ramos visores activos y se perdiÃ³ o finalizÃ³ la transmisiÃ³n
                    if (!_isHosting && _isViewing)
                    {
                        HandleViewerDisconnected(status);
                        return;
                    }

                    UpdateStatus(status, "#7C8B9E");
                    HideViewerControls();
                    return;
                }

                UpdateStatus(status, "#22C55E");

                var disconnectBtn = this.FindControl<Button>("DisconnectButton");
                if (disconnectBtn != null)
                {
                    disconnectBtn.IsVisible = true;
                }

                var audioPanel = this.FindControl<Border>("AudioControlPanel");
                if (audioPanel != null)
                {
                    audioPanel.IsVisible = true;
                }
                UpdateAudioUI();

                var micPanel = this.FindControl<Border>("MicControlPanel");
                if (micPanel != null)
                {
                    micPanel.IsVisible = true;
                }
                UpdateMicUI();

                var viewerVoicePanel = this.FindControl<Border>("ViewerVoiceChatPanel");
                if (viewerVoicePanel != null)
                {
                    viewerVoicePanel.IsVisible = true;
                }
                UpdateViewerVoiceChatUI(_isViewerVoiceChatActive);

                var remoteControlBtn = this.FindControl<ToggleButton>("RemoteControlButton");
                if (remoteControlBtn != null)
                {
                    remoteControlBtn.IsVisible = _currentDeviceAllowsRemoteControl;
                    if (!_currentDeviceAllowsRemoteControl) remoteControlBtn.IsChecked = false;
                }

                var gameModeBtn = this.FindControl<ToggleButton>("GameModeButton");
                if (gameModeBtn != null)
                {
                    gameModeBtn.IsVisible = _currentDeviceAllowsRemoteControl;
                    if (!_currentDeviceAllowsRemoteControl) gameModeBtn.IsChecked = false;
                }

                var switchMonitorBtn = this.FindControl<Button>("SwitchMonitorButton");
                if (switchMonitorBtn != null)
                {
                    switchMonitorBtn.IsVisible = _currentDeviceAllowsRemoteControl;
                }
            });
        }

        private async void DisconnectButton_Click(object? sender, RoutedEventArgs? e = null)
        {
            await HandleViewerDisconnectedAsync("Te has desconectado de la transmisiÃ³n.");
        }

        private void HandleViewerDisconnected(string reason)
        {
            Dispatcher.UIThread.Post(async () =>
            {
                await HandleViewerDisconnectedAsync(reason);
            });
        }

        private async Task HandleViewerDisconnectedAsync(string reason)
        {
            _isViewing = false;
            UpdateStreamStateBadge();
            ClosePopoutWindow();
            SetFullscreen(false);
            StopDeviceSearch();
            _currentDeviceAllowsRemoteControl = false;
            _currentConnectedDeviceIp = null;
            _currentConnectedDeviceName = null;
            UpdateDevicesViewingState();
            UpdateSourceUI();
            OnSettingChanged();

            try { await _streamClient.DisconnectAsync(); } catch { }
            try { await _audioClient.DisconnectAsync(); } catch { }
            try { await _controlClient.DisconnectAsync(); } catch { }
            try { await _fileTransferClient.DisconnectAsync(); } catch { }

            if (!_isHosting)
            {
                _micCaptureService?.StopCapture();
            }

            _audioPlayerService?.Stop();
            _micAudioPlayerService?.Stop();
            _remoteControlEnabled = false;

            if (_isGameMode)
            {
                _isGameMode = false;
                UpdateGameModeUI();
            }

            ResetMetrics();

            var screenImage = this.FindControl<Image>("ScreenImage");
            if (screenImage != null)
            {
                screenImage.Source = null;
            }

            _viewerWriteableBitmap?.Dispose();
            _viewerWriteableBitmap = null;

            var placeholder = this.FindControl<StackPanel>("PlaceholderPanel");
            if (placeholder != null)
            {
                placeholder.IsVisible = true;
            }

            var placeholderActions = this.FindControl<StackPanel>("PlaceholderActionsPanel");
            if (placeholderActions != null)
            {
                placeholderActions.IsVisible = true;
            }

            var placeholderTitle = this.FindControl<TextBlock>("PlaceholderTitleText");
            var placeholderSubtitle = this.FindControl<TextBlock>("PlaceholderSubtitleText");

            if (reason.Contains("finaliz", StringComparison.OrdinalIgnoreCase))
            {
                if (placeholderTitle != null) placeholderTitle.Text = "TransmisiÃ³n Finalizada";
                if (placeholderSubtitle != null) placeholderSubtitle.Text = "El anfitriÃ³n ha finalizado la transmisiÃ³n.\nPuedes iniciar tu propia transmisiÃ³n o conectarte a otro equipo.";
                UpdateStatus("El anfitriÃ³n ha finalizado la transmisiÃ³n", "#F59E0B");
            }
            else if (reason.Contains("perdida", StringComparison.OrdinalIgnoreCase) ||
                     reason.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                     reason.Contains("No se pudo", StringComparison.OrdinalIgnoreCase))
            {
                if (placeholderTitle != null) placeholderTitle.Text = "ConexiÃ³n Perdida";
                if (placeholderSubtitle != null) placeholderSubtitle.Text = reason;
                UpdateStatus(reason, "#EF4444");
            }
            else
            {
                if (placeholderTitle != null) placeholderTitle.Text = "Todo listo para compartir";
                if (placeholderSubtitle != null) placeholderSubtitle.Text = "Comparte una pantalla o una aplicaciÃ³n con otros equipos.\nTÃº eliges quÃ© mostrar y quiÃ©n puede controlarlo.";
                UpdateStatus("Sin conexiÃ³n activa", "#34D399");
            }

            HideViewerControls();
        }

        private void HideViewerControls()
        {
            var fullscreenBtn = this.FindControl<Button>("FullscreenButton");
            if (fullscreenBtn != null) fullscreenBtn.IsVisible = false;

            var stretchBtn = this.FindControl<Button>("StretchModeButton");
            if (stretchBtn != null) stretchBtn.IsVisible = false;

            var popoutBtn = this.FindControl<Button>("PopoutWindowButton");
            if (popoutBtn != null) popoutBtn.IsVisible = false;

            var disconnectBtn = this.FindControl<Button>("DisconnectButton");
            if (disconnectBtn != null) disconnectBtn.IsVisible = false;

            var viewerVoicePanel = this.FindControl<Border>("ViewerVoiceChatPanel");
            if (viewerVoicePanel != null) viewerVoicePanel.IsVisible = false;

            var remoteControlBtn = this.FindControl<ToggleButton>("RemoteControlButton");
            if (remoteControlBtn != null)
            {
                remoteControlBtn.IsVisible = false;
                remoteControlBtn.IsChecked = false;
            }

            var gameModeBtn = this.FindControl<ToggleButton>("GameModeButton");
            if (gameModeBtn != null)
            {
                gameModeBtn.IsVisible = false;
                gameModeBtn.IsChecked = false;
            }

            _currentRemoteMonitorLabel = "Pantalla";
            _remoteMonitorCatalog = null;
            _remoteSourceMenu?.Close();
            var switchMonitorBtn = this.FindControl<Button>("SwitchMonitorButton");
            if (switchMonitorBtn != null)
            {
                switchMonitorBtn.IsVisible = false;
            }
            var switchMonitorText = this.FindControl<TextBlock>("SwitchMonitorButtonText");
            if (switchMonitorText != null)
            {
                switchMonitorText.Text = "Pantalla";
            }

            var sendFileBtn = this.FindControl<Button>("SendFileButton");
            if (sendFileBtn != null)
            {
                sendFileBtn.IsVisible = false;
            }
        }

        #endregion

        #region Helper Methods

        private void UpdateStatus(string message, string hexColor)
        {
            var statusText = this.FindControl<TextBlock>("StatusText");
            if (statusText != null)
            {
                statusText.Text = message;
            }

            var indicator = this.FindControl<Ellipse>("StatusIndicator");
            if (indicator != null)
            {
                indicator.Fill = new SolidColorBrush(Color.Parse(hexColor));
            }

            var topNetIcon = this.FindControl<PathIcon>("TopNetworkStatusIcon");
            if (topNetIcon != null)
            {
                topNetIcon.Foreground = new SolidColorBrush(Color.Parse(hexColor));
            }
        }

        private static bool IsRunningAsAdministrator()
        {
            if (!OperatingSystem.IsWindows())
                return false;

            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private int GetTargetAudioProcessId()
        {
            if (_currentSource?.Type == CaptureSourceType.Window)
            {
                if (OperatingSystem.IsWindows())
                {
                    int resolvedPid = CaptureSourceDiscoveryService.ResolveEffectiveProcessId(_currentSource.Handle, _currentSource.ProcessId);
                    if (resolvedPid > 0)
                    {
                        _currentSource.ProcessId = resolvedPid;
                        return resolvedPid;
                    }
                }

                if (_currentSource.ProcessId > 0)
                    return _currentSource.ProcessId;
            }
            return 0;
        }

        #region Win32 P/Invoke for Mouse Centering and Screen Metrics

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        #endregion

        private async Task OpenRemoteSourceMenuAsync(Button? anchorButton)
        {
            if (!_isViewing || !_currentDeviceAllowsRemoteControl)
                return;

            anchorButton ??= this.FindControl<Button>("SwitchMonitorButton");
            if (anchorButton == null)
                return;

            if (_remoteSourceMenu == null)
            {
                _remoteSourceMenu = new ContextMenu
                {
                    Background = new SolidColorBrush(Color.Parse("#181D27")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#334155")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(6),
                    Placement = PlacementMode.BottomEdgeAlignedLeft
                };
            }

            PopulateRemoteSourceMenu();
            _remoteSourceMenu.Open(anchorButton);

            bool connected = await EnsureControlConnectedAsync();
            if (connected)
            {
                await _controlClient.RequestSourceCatalogAsync();
            }
        }

        private void PopulateRemoteSourceMenu()
        {
            if (_remoteSourceMenu == null)
                return;

            var items = new List<object>();

            var catalog = _remoteMonitorCatalog;
            if (catalog != null && catalog.Sources != null && catalog.Sources.Count > 0)
            {
                var screens = catalog.Sources.Where(s => s.Category == "screen").ToList();
                var windows = catalog.Sources.Where(s => s.Category == "window").ToList();

                if (screens.Count > 0)
                {
                    items.Add(new MenuItem
                    {
                        Header = "PANTALLAS DEL ANFITRIÃ“N",
                        IsEnabled = false,
                        FontSize = 10,
                        FontWeight = FontWeight.Bold,
                        Foreground = new SolidColorBrush(Color.Parse("#38BDF8"))
                    });

                    foreach (var s in screens)
                    {
                        bool isActive = s.Id == catalog.ActiveSourceId ||
                                        (!string.IsNullOrEmpty(s.Name) && s.Name == _currentRemoteMonitorLabel);
                        string prefix = isActive ? "âœ“ ðŸ–¥ï¸ " : "    ðŸ–¥ï¸ ";
                        string subtitle = !string.IsNullOrWhiteSpace(s.Subtitle) ? $" ({s.Subtitle})" : string.Empty;

                        var menuItem = new MenuItem
                        {
                            Header = $"{prefix}{s.Name}{subtitle}",
                            FontWeight = isActive ? FontWeight.Bold : FontWeight.Normal,
                            Foreground = new SolidColorBrush(Color.Parse(isActive ? "#38BDF8" : "#F1F5F9")),
                            Tag = s.Id
                        };
                        string capturedId = s.Id;
                        menuItem.Click += async (_, _) => await SelectRemoteSourceAsync(capturedId);
                        items.Add(menuItem);
                    }
                }

                if (windows.Count > 0)
                {
                    if (items.Count > 0)
                    {
                        items.Add(new Separator { Background = new SolidColorBrush(Color.Parse("#263042")), Margin = new Thickness(0, 4) });
                    }

                    items.Add(new MenuItem
                    {
                        Header = "VENTANAS / APLICACIONES",
                        IsEnabled = false,
                        FontSize = 10,
                        FontWeight = FontWeight.Bold,
                        Foreground = new SolidColorBrush(Color.Parse("#A78BFA"))
                    });

                    foreach (var w in windows)
                    {
                        bool isActive = w.Id == catalog.ActiveSourceId ||
                                        (!string.IsNullOrEmpty(w.Name) && w.Name == _currentRemoteMonitorLabel);
                        string prefix = isActive ? "âœ“ ðŸªŸ " : "    ðŸªŸ ";
                        string displayName = w.Name.Length > 45 ? w.Name[..42] + "..." : w.Name;

                        var menuItem = new MenuItem
                        {
                            Header = $"{prefix}{displayName}",
                            FontWeight = isActive ? FontWeight.Bold : FontWeight.Normal,
                            Foreground = new SolidColorBrush(Color.Parse(isActive ? "#A78BFA" : "#E2E8F0")),
                            Tag = w.Id
                        };
                        string capturedId = w.Id;
                        menuItem.Click += async (_, _) => await SelectRemoteSourceAsync(capturedId);
                        items.Add(menuItem);
                    }
                }
            }
            else
            {
                items.Add(new MenuItem
                {
                    Header = _controlClient.IsConnected
                        ? "â³ Consultando pantallas y ventanas del anfitriÃ³n..."
                        : "â³ Conectando control remoto para listar fuentes...",
                    IsEnabled = false,
                    Foreground = new SolidColorBrush(Color.Parse("#94A3B8"))
                });
            }

            items.Add(new Separator { Background = new SolidColorBrush(Color.Parse("#263042")), Margin = new Thickness(0, 4) });

            var cycleItem = new MenuItem
            {
                Header = "â­ Alternar siguiente fuente (Ctrl+Alt+M)",
                Foreground = new SolidColorBrush(Color.Parse("#38BDF8")),
                FontWeight = FontWeight.SemiBold
            };
            cycleItem.Click += async (_, _) => await RequestRemoteMonitorSwitchAsync();
            items.Add(cycleItem);

            var refreshItem = new MenuItem
            {
                Header = "ðŸ”„ Actualizar lista de fuentes",
                Foreground = new SolidColorBrush(Color.Parse("#CBD5E1"))
            };
            refreshItem.Click += async (_, _) =>
            {
                if (await EnsureControlConnectedAsync())
                {
                    await _controlClient.RequestSourceCatalogAsync();
                }
            };
            items.Add(refreshItem);

            _remoteSourceMenu.ItemsSource = items;
        }

        private async Task SelectRemoteSourceAsync(string sourceId)
        {
            if (string.IsNullOrWhiteSpace(sourceId) || !_isViewing || !_currentDeviceAllowsRemoteControl)
                return;

            bool connected = await EnsureControlConnectedAsync();
            if (!connected)
                return;

            await _controlClient.SendSelectSourceAsync(sourceId);
        }

        private async Task RequestRemoteMonitorSwitchAsync()
        {
            if (!_isViewing || !_currentDeviceAllowsRemoteControl)
                return;

            bool connected = await EnsureControlConnectedAsync();
            if (!connected)
                return;

            await _controlClient.SendSwitchMonitorAsync(-1);
        }

        private void BroadcastHostSourceCatalog()
        {
            if (!OperatingSystem.IsWindows())
                return;

            Dispatcher.UIThread.Post(() =>
            {
                if (!OperatingSystem.IsWindows() || !_isHosting || !_allowRemoteControl)
                    return;

                try
                {
                    var rawScreens = CaptureSourceDiscoveryService.GetScreenSources();
                    var orderedScreens = rawScreens
                        .Where(s => s.Type == CaptureSourceType.Screen)
                        .Concat(rawScreens.Where(s => s.Type == CaptureSourceType.AllScreens))
                        .ToList();

                    int currentPid = Environment.ProcessId;
                    var windows = CaptureSourceDiscoveryService.GetWindowSources()
                        .Where(w => w.ProcessId != currentPid)
                        .Take(15)
                        .ToList();

                    var dto = new RemoteMonitorCatalogDto
                    {
                        ActiveSourceId = _currentSource.Id,
                        ActiveSourceName = _currentSource.Name,
                        Sources = new List<RemoteSourceItemDto>()
                    };

                    foreach (var s in orderedScreens)
                    {
                        dto.Sources.Add(new RemoteSourceItemDto
                        {
                            Id = s.Id,
                            Name = s.Name,
                            Subtitle = s.Subtitle,
                            Category = "screen"
                        });
                    }

                    foreach (var w in windows)
                    {
                        dto.Sources.Add(new RemoteSourceItemDto
                        {
                            Id = w.Id,
                            Name = w.Name,
                            Subtitle = w.Subtitle,
                            Category = "window"
                        });
                    }

                    string json = JsonSerializer.Serialize(dto);
                    _ = _controlServer.BroadcastMonitorInfoAsync(json);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ControlHost] Error enviando catÃ¡logo de fuentes: {ex.Message}");
                    _ = _controlServer.BroadcastMonitorInfoAsync(_currentSource.Name);
                }
            });
        }

        private void HandleRemoteSelectSourceOnHost(string sourceId)
        {
            if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(sourceId))
                return;

            Dispatcher.UIThread.Post(() =>
            {
                if (!OperatingSystem.IsWindows() || !_isHosting || !_allowRemoteControl)
                    return;

                var allSources = CaptureSourceDiscoveryService.GetScreenSources()
                    .Concat(CaptureSourceDiscoveryService.GetWindowSources())
                    .ToList();

                var matched = allSources.FirstOrDefault(s => s.Id == sourceId);
                if (matched == null)
                    return;

                SetCaptureSource(matched);
                Console.WriteLine($"[ControlHost] Fuente seleccionada remotamente: {matched.Name} ({matched.Id})");
            });
        }

        private void HandleRemoteMonitorSwitchOnHost(int requestedIndex)
        {
            if (!OperatingSystem.IsWindows())
                return;

            Dispatcher.UIThread.Post(() =>
            {
                if (!OperatingSystem.IsWindows() || !_isHosting || !_allowRemoteControl)
                    return;

                var rawSources = CaptureSourceDiscoveryService.GetScreenSources();
                if (rawSources.Count == 0)
                    return;

                // Ordenar primero monitores individuales (Pantalla 1, Pantalla 2...) y al final "Todas las pantallas"
                var candidateSources = rawSources
                    .Where(s => s.Type == CaptureSourceType.Screen)
                    .Concat(rawSources.Where(s => s.Type == CaptureSourceType.AllScreens))
                    .ToList();

                // Si el anfitriÃ³n solo tiene 1 pantalla fÃ­sica, incluir tambiÃ©n las ventanas abiertas
                // para que el botÃ³n/atajo permita alternar entre Pantalla 1 y las aplicaciones abiertas
                if (candidateSources.Count <= 1)
                {
                    int currentPid = Environment.ProcessId;
                    var openWindows = CaptureSourceDiscoveryService.GetWindowSources()
                        .Where(w => w.ProcessId != currentPid)
                        .Take(12)
                        .ToList();
                    candidateSources.AddRange(openWindows);
                }

                if (candidateSources.Count == 0)
                    return;

                int nextIndex = 0;
                if (requestedIndex >= 0 && requestedIndex < candidateSources.Count)
                {
                    nextIndex = requestedIndex;
                }
                else
                {
                    int currentIdx = candidateSources.FindIndex(s => s.Id == _currentSource.Id);
                    nextIndex = currentIdx >= 0 ? (currentIdx + 1) % candidateSources.Count : 0;
                }

                var targetSource = candidateSources[nextIndex];
                SetCaptureSource(targetSource);
                Console.WriteLine($"[ControlHost] Fuente alternada remotamente a: {targetSource.Name} ({targetSource.Id})");
            });
        }

        private void ApplyRemoteMonitorInfo(string monitorPayload)
        {
            if (string.IsNullOrWhiteSpace(monitorPayload))
                return;

            Dispatcher.UIThread.Post(() =>
            {
                string monitorLabel = monitorPayload;

                if (monitorPayload.TrimStart().StartsWith("{", StringComparison.Ordinal))
                {
                    try
                    {
                        var catalog = JsonSerializer.Deserialize<RemoteMonitorCatalogDto>(monitorPayload);
                        if (catalog != null)
                        {
                            _remoteMonitorCatalog = catalog;
                            if (!string.IsNullOrWhiteSpace(catalog.ActiveSourceName))
                            {
                                monitorLabel = catalog.ActiveSourceName;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ControlViewer] Error parseando catÃ¡logo de monitores: {ex.Message}");
                    }
                }

                _currentRemoteMonitorLabel = monitorLabel;

                string shortLabel = monitorLabel.Length > 22 ? monitorLabel[..19] + "..." : monitorLabel;
                var switchBtnText = this.FindControl<TextBlock>("SwitchMonitorButtonText");
                if (switchBtnText != null)
                {
                    switchBtnText.Text = shortLabel;
                }

                _popoutWindow?.SetActiveMonitorLabel(shortLabel);

                if (_remoteSourceMenu != null && _remoteSourceMenu.IsOpen)
                {
                    PopulateRemoteSourceMenu();
                }

                if (_isViewing)
                {
                    var canvasTitle = this.FindControl<TextBlock>("CanvasSourceTitleText");
                    if (canvasTitle != null)
                    {
                        canvasTitle.Text = !string.IsNullOrEmpty(_currentConnectedDeviceName)
                            ? $"{_currentConnectedDeviceName} Â· {monitorLabel}"
                            : monitorLabel;
                    }

                    UpdateStatus($"Viendo {monitorLabel} de {_currentConnectedDeviceName}", "#22C55E");
                }
            });
        }

        private void ApplyRemoteClipboardText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    if (text == _lastLocalClipboardText)
                        return;

                    _lastNetworkClipboardText = text;
                    _lastLocalClipboardText = text;

                    var topLevel = TopLevel.GetTopLevel(this);
                    if (topLevel?.Clipboard != null)
                    {
                        await topLevel.Clipboard.SetTextAsync(text);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ClipboardSync] Error aplicando texto remoto al portapapeles: {ex.Message}");
                }
            });
        }

        private async void ClipboardSyncTimer_Tick(object? sender, EventArgs e)
        {
            if (_isPollingClipboard || !_allowClipboardSync)
                return;

            bool canSyncAsHost = _isHosting && _allowRemoteControl && _controlServer.IsRunning;
            bool canSyncAsViewer = _isViewing && _remoteControlEnabled && _controlClient.IsConnected;

            if (!canSyncAsHost && !canSyncAsViewer)
                return;

            _isPollingClipboard = true;
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel?.Clipboard == null)
                    return;

                string? currentText = await topLevel.Clipboard.TryGetTextAsync();
                if (string.IsNullOrEmpty(currentText))
                    return;

                if (_lastLocalClipboardText == null)
                {
                    _lastLocalClipboardText = currentText;
                    return;
                }

                if (currentText == _lastLocalClipboardText || currentText == _lastNetworkClipboardText)
                {
                    _lastLocalClipboardText = currentText;
                    return;
                }

                _lastLocalClipboardText = currentText;
                _lastNetworkClipboardText = null;

                if (canSyncAsHost)
                {
                    await _controlServer.BroadcastClipboardTextAsync(currentText);
                }
                else if (canSyncAsViewer)
                {
                    await _controlClient.SendClipboardTextAsync(currentText);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ClipboardSync] Error leyendo/sincronizando portapapeles: {ex.Message}");
            }
            finally
            {
                _isPollingClipboard = false;
            }
        }

        private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
        {
            SaveCurrentSettings();
            ClosePopoutWindow();
            SetFullscreen(false);
            StopDeviceSearch();
            _headerAutoHideTimer.Stop();
            _metricsTimer.Stop();
            _clipboardSyncTimer.Stop();

            _h264Decoder?.Dispose();
            _viewerWriteableBitmap?.Dispose();

            _screenCaptureService.Dispose();
            _streamServer.Dispose();
            _streamClient.Dispose();

            _audioCaptureService?.Dispose();
            _micCaptureService?.Dispose();
            _audioPlayerService?.Dispose();
            _micAudioPlayerService?.Dispose();
            _audioServer.Dispose();
            _audioClient.Dispose();

            _controlServer.Dispose();
            _controlClient.Dispose();
            _inputSimulatorService?.Dispose();

            _fileTransferBannerTimer?.Stop();
            _activeTransferCts?.Cancel();
            _activeTransferCts?.Dispose();
            _fileTransferServer.Dispose();
            _fileTransferClient.Dispose();

            _discoveryService.StopHosting();
            _discoveryService.StopListening();
        }

        #region File Transfer Methods

        private void MainWindow_DragOver(object? sender, DragEventArgs e)
        {
            if ((_isHosting && _streamServer.ConnectedClientsCount > 0) || (_isViewing && _streamClient.IsConnected))
            {
                if (e.DataTransfer.Contains(DataFormat.File))
                {
                    e.DragEffects = DragDropEffects.Copy;
                    return;
                }
            }
            e.DragEffects = DragDropEffects.None;
        }

        private async void MainWindow_Drop(object? sender, DragEventArgs e)
        {
            if (!((_isHosting && _streamServer.ConnectedClientsCount > 0) || (_isViewing && _streamClient.IsConnected)))
            {
                return;
            }

            if (e.DataTransfer.TryGetFiles() is { } files)
            {
                foreach (var f in files)
                {
                    var path = f.Path.LocalPath;
                    if (File.Exists(path))
                    {
                        await SendFileToPeerAsync(path);
                    }
                }
            }
        }

        private async Task PickAndSendFilesAsync()
        {
            bool canSend = (_isHosting && _streamServer.ConnectedClientsCount > 0) || (_isViewing && _streamClient.IsConnected);
            if (!canSend)
            {
                return;
            }

            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel?.StorageProvider != null)
                {
                    var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                    {
                        Title = "Seleccionar archivo(s) para enviar",
                        AllowMultiple = true
                    });

                    if (files != null && files.Count > 0)
                    {
                        foreach (var file in files)
                        {
                            var localPath = file.Path.LocalPath;
                            if (File.Exists(localPath))
                            {
                                await SendFileToPeerAsync(localPath);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FileTransfer] Error abriendo selector de archivos: {ex.Message}");
            }
        }

        private async Task SendFileToPeerAsync(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return;

            _activeTransferCts?.Cancel();
            _activeTransferCts?.Dispose();
            _activeTransferCts = new CancellationTokenSource();

            try
            {
                if (_isHosting && _fileTransferServer.IsRunning)
                {
                    await _fileTransferServer.SendFileAsync(filePath, _activeTransferCts.Token);
                }
                else if (_isViewing && _fileTransferClient.IsConnected)
                {
                    await _fileTransferClient.SendFileAsync(filePath, _activeTransferCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[FileTransfer] EnvÃ­o de archivo cancelado por el usuario.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FileTransfer] Error enviando archivo '{filePath}': {ex.Message}");
                ShowFileTransferError(new FileTransferError
                {
                    TransferId = Guid.NewGuid().ToString(),
                    FileName = System.IO.Path.GetFileName(filePath),
                    ErrorMessage = ex.Message,
                    IsUpload = true
                });
            }
        }

        private void ShowFileTransferProgress(FileTransferProgress p)
        {
            _fileTransferBannerTimer?.Stop();

            var banner = this.FindControl<Border>("FileTransferBanner");
            if (banner != null) banner.IsVisible = true;

            var title = this.FindControl<TextBlock>("FileTransferBannerTitle");
            if (title != null)
            {
                title.Text = p.IsUpload ? "Enviando archivo..." : "Recibiendo archivo...";
                title.Foreground = Brushes.White;
            }

            var subtitle = this.FindControl<TextBlock>("FileTransferBannerSubtitle");
            if (subtitle != null)
            {
                double speedMb = p.SpeedBytesPerSec / (1024.0 * 1024.0);
                subtitle.Text = $"{p.FileName} â€” {p.Percent:0}% ({speedMb:0.1} MB/s)";
            }

            var progressBar = this.FindControl<ProgressBar>("FileTransferProgressBar");
            if (progressBar != null)
            {
                progressBar.IsVisible = true;
                progressBar.Value = p.Percent;
                progressBar.Foreground = new SolidColorBrush(Color.Parse("#10B981"));
            }

            var openFolderBtn = this.FindControl<Button>("FileTransferOpenFolderButton");
            if (openFolderBtn != null) openFolderBtn.IsVisible = false;

            var cancelBtn = this.FindControl<Button>("FileTransferCancelButton");
            if (cancelBtn != null) cancelBtn.IsVisible = true;

            var dismissBtn = this.FindControl<Button>("FileTransferDismissButton");
            if (dismissBtn != null) dismissBtn.IsVisible = false;
        }

        private readonly record struct TransferBannerConfig(
            string Title,
            string TitleColorHex,
            string Subtitle,
            bool ShowProgressBar = false,
            int ProgressVal = 0,
            string ProgressColorHex = "#10B981",
            bool ShowOpenFolder = false);

        private void ShowFileTransferStatusBanner(in TransferBannerConfig cfg)
        {
            _fileTransferBannerTimer?.Stop();

            var banner = this.FindControl<Border>("FileTransferBanner");
            if (banner != null) banner.IsVisible = true;

            var titleBlock = this.FindControl<TextBlock>("FileTransferBannerTitle");
            if (titleBlock != null)
            {
                titleBlock.Text = cfg.Title;
                titleBlock.Foreground = new SolidColorBrush(Color.Parse(cfg.TitleColorHex));
            }

            var subtitleBlock = this.FindControl<TextBlock>("FileTransferBannerSubtitle");
            if (subtitleBlock != null) subtitleBlock.Text = cfg.Subtitle;

            var progressBar = this.FindControl<ProgressBar>("FileTransferProgressBar");
            if (progressBar != null)
            {
                progressBar.IsVisible = cfg.ShowProgressBar;
                if (cfg.ShowProgressBar)
                {
                    progressBar.Value = cfg.ProgressVal;
                    progressBar.Foreground = new SolidColorBrush(Color.Parse(cfg.ProgressColorHex));
                }
            }

            var openFolderBtn = this.FindControl<Button>("FileTransferOpenFolderButton");
            if (openFolderBtn != null) openFolderBtn.IsVisible = cfg.ShowOpenFolder;

            var cancelBtn = this.FindControl<Button>("FileTransferCancelButton");
            if (cancelBtn != null) cancelBtn.IsVisible = false;

            var dismissBtn = this.FindControl<Button>("FileTransferDismissButton");
            if (dismissBtn != null) dismissBtn.IsVisible = true;

            _fileTransferBannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            _fileTransferBannerTimer.Tick += (s, e) =>
            {
                _fileTransferBannerTimer.Stop();
                HideFileTransferBanner();
            };
            _fileTransferBannerTimer.Start();
        }

        private void ShowFileTransferCompleted(FileTransferCompleted c)
        {
            _lastReceivedFilePath = c.FilePath;
            string title = c.IsUpload ? "Archivo enviado con éxito" : "Archivo recibido";
            double mb = c.FileSize / (1024.0 * 1024.0);
            string subtitle = $"{c.FileName} ({mb:0.1} MB) — Completado";
            bool canOpen = !string.IsNullOrEmpty(c.FilePath) && File.Exists(c.FilePath);

            ShowFileTransferStatusBanner(new TransferBannerConfig(
                title, "#34D399", subtitle,
                ShowProgressBar: true, ProgressVal: 100, ProgressColorHex: "#10B981", ShowOpenFolder: canOpen));
        }

        private void ShowFileTransferError(FileTransferError err)
        {
            ShowFileTransferStatusBanner(new TransferBannerConfig(
                "Error en transferencia", "#F87171", $"{err.FileName}: {err.ErrorMessage}"));
        }
        private void CancelActiveFileTransfer()
        {
            try
            {
                _activeTransferCts?.Cancel();
            }
            catch { }
            HideFileTransferBanner();
        }

        private void HideFileTransferBanner()
        {
            _fileTransferBannerTimer?.Stop();
            var banner = this.FindControl<Border>("FileTransferBanner");
            if (banner != null)
            {
                banner.IsVisible = false;
            }
        }

        private void OpenDownloadsFolder()
        {
            try
            {
                string? targetPath = _lastReceivedFilePath;
                if (!string.IsNullOrEmpty(targetPath) && File.Exists(targetPath))
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        Process.Start("explorer.exe", $"/select,\"{targetPath}\"");
                        return;
                    }
                    string? dir = System.IO.Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = dir,
                            UseShellExecute = true
                        });
                        return;
                    }
                }
                else
                {
                    string downloads = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "LanScreenShare");
                    if (Directory.Exists(downloads))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = downloads,
                            UseShellExecute = true
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FileTransfer] Error abriendo carpeta: {ex.Message}");
            }
        }

        #endregion

        #endregion
    }
}
