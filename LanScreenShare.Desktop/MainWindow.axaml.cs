using System;
using System.Collections.ObjectModel;
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
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;
using LanScreenShare.Media;
using LanScreenShare.Network;
using LanScreenShare.Network.Streaming;

namespace LanScreenShare.Desktop
{
    public partial class MainWindow : Window
    {
        private readonly DiscoveryService _discoveryService;
        private IScreenCaptureService _screenCaptureService;
        private readonly IStreamServer _streamServer;
        private readonly IStreamClient _streamClient;

        // Servicios de streaming de audio
        private readonly IAudioCaptureService? _audioCaptureService;
        private readonly IAudioPlayerService? _audioPlayerService;
        private readonly TcpAudioServer _audioServer;
        private readonly TcpAudioClient _audioClient;

        // Servicios de control remoto (Mouse y Teclado)
        private readonly IInputSimulatorService? _inputSimulatorService;
        private readonly TcpControlServer _controlServer;
        private readonly TcpControlClient _controlClient;
        private bool _remoteControlEnabled;
        private volatile bool _allowRemoteControl = true;
        private bool _isGameMode;
        private bool _isRecenteringMouse;
        private DispatcherTimer? _gameModeBannerTimer;

        private bool _isHosting;
        private string? _currentConnectedDeviceName;

        // Pantalla completa
        private bool _isFullscreen;
        private WindowState _previousWindowState = WindowState.Normal;

        // Búsqueda de dispositivos
        private DispatcherTimer? _searchTimer;

        // Control de acceso y aprobación de conexiones
        private TaskCompletionSource<bool>? _streamApprovalTcs;
        private TaskCompletionSource<bool>? _controlApprovalTcs;

        private bool RequireStreamApproval =>
            (this.FindControl<ComboBox>("StreamAccessModeComboBox")?.SelectedItem as ComboBoxItem)?.Tag as string == "ask";

        private string RemoteControlMode =>
            (this.FindControl<ComboBox>("ControlAccessModeComboBox")?.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";

        // Decodificador de video por hardware (H.264)
        private readonly IVideoDecoderService? _h264Decoder;
        private WriteableBitmap? _viewerWriteableBitmap;
        private readonly object _bitmapLock = new();

        // Estadísticas de flujo en tiempo real
        private long _metricFramesCount;
        private long _metricBytesCount;
        private int _lastFrameWidth;
        private int _lastFrameHeight;
        private readonly DispatcherTimer _metricsTimer;

        public ObservableCollection<DeviceInfo> Devices { get; } = new();

        public MainWindow()
        {
            InitializeComponent();

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    _h264Decoder = new MediaFoundationH264DecoderService();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MainWindow] Decodificador H.264 no disponible: {ex.Message}");
                }
            }

            _discoveryService = new DiscoveryService();
            _discoveryService.OnDeviceDiscovered += DiscoveryService_OnDeviceDiscovered;

            // Iniciar por defecto con el motor DXGI (acelerado por GPU)
            _screenCaptureService = new DxgiScreenCaptureService();
            _screenCaptureService.FrameCaptured += ScreenCaptureService_FrameCaptured;

            _streamServer = new TcpStreamServer();
            _streamServer.ClientConnected += StreamServer_ClientConnected;
            _streamServer.ClientDisconnected += StreamServer_ClientDisconnected;

            _streamClient = new TcpStreamClient();
            _streamClient.FrameReceived += StreamClient_FrameReceived;
            _streamClient.StatusChanged += StreamClient_StatusChanged;

            // Inicialización de subsistema de Audio
            _audioServer = new TcpAudioServer();
            _audioClient = new TcpAudioClient();

            // Inicialización de subsistema de Control Remoto
            _controlServer = new TcpControlServer();
            _controlClient = new TcpControlClient();

            if (OperatingSystem.IsWindows())
            {
                _audioCaptureService = new WasapiAudioCaptureService();
                _audioPlayerService = new WasapiAudioPlayerService();
                _inputSimulatorService = new WindowsInputSimulatorService();

                _audioCaptureService.AudioCaptured += async (s, chunk) =>
                {
                    if (_isHosting && _audioServer.IsRunning)
                    {
                        await _audioServer.BroadcastAudioAsync(chunk);
                    }
                };

                _audioClient.AudioReceived += (s, chunk) =>
                {
                    _audioPlayerService.PlayChunk(chunk);
                };
            }

            var allowCb = this.FindControl<CheckBox>("AllowRemoteControlCheckBox");
            if (allowCb != null)
            {
                _allowRemoteControl = allowCb.IsChecked == true;
                allowCb.IsCheckedChanged += (s, e) =>
                {
                    _allowRemoteControl = allowCb.IsChecked == true;
                    Console.WriteLine($"[ControlHost] Permiso de control remoto: {_allowRemoteControl}");
                };
            }

            _controlServer.InputReceived += (s, input) =>
            {
                if (_allowRemoteControl)
                {
                    _inputSimulatorService?.SimulateInput(input);
                }
            };

            var devicesList = this.FindControl<ListBox>("DevicesList");
            if (devicesList != null)
            {
                devicesList.ItemsSource = Devices;
            }

            var hostButton = this.FindControl<Button>("HostButton");
            if (hostButton != null)
            {
                hostButton.Click += HostButton_Click;
            }

            var searchButton = this.FindControl<Button>("SearchButton");
            if (searchButton != null)
            {
                searchButton.Click += SearchButton_Click;
            }

            var disconnectButton = this.FindControl<Button>("DisconnectButton");
            if (disconnectButton != null)
            {
                disconnectButton.Click += DisconnectButton_Click;
            }

            var audioMuteButton = this.FindControl<Button>("AudioMuteButton");
            if (audioMuteButton != null)
            {
                audioMuteButton.Click += (s, e) =>
                {
                    if (_audioPlayerService != null)
                    {
                        _audioPlayerService.IsMuted = !_audioPlayerService.IsMuted;
                        audioMuteButton.Content = _audioPlayerService.IsMuted ? "🔇" : "🔊";
                    }
                };
            }

            var remoteControlBtn = this.FindControl<ToggleButton>("RemoteControlButton");
            if (remoteControlBtn != null)
            {
                remoteControlBtn.IsCheckedChanged += (s, e) =>
                {
                    _remoteControlEnabled = remoteControlBtn.IsChecked == true;
                    remoteControlBtn.Background = _remoteControlEnabled
                        ? new SolidColorBrush(Color.Parse("#2563EB"))
                        : new SolidColorBrush(Color.Parse("#27272A"));

                    var screenImg = this.FindControl<Image>("ScreenImage");
                    if (_remoteControlEnabled && screenImg != null)
                    {
                        screenImg.Focus();
                    }
                };
            }

            var gameModeBtn = this.FindControl<ToggleButton>("GameModeButton");
            if (gameModeBtn != null)
            {
                gameModeBtn.IsCheckedChanged += (s, e) =>
                {
                    if (_isGameMode != (gameModeBtn.IsChecked == true))
                    {
                        ToggleGameMode();
                    }
                };
            }

            this.KeyDown += MainWindow_KeyDown;

            // Controles de Pantalla Completa
            var fullscreenBtn = this.FindControl<Button>("FullscreenButton");
            if (fullscreenBtn != null)
            {
                fullscreenBtn.Click += (s, e) => ToggleFullscreen();
            }

            var exitFloatBtn = this.FindControl<Button>("ExitFullscreenFloatButton");
            if (exitFloatBtn != null)
            {
                exitFloatBtn.Click += (s, e) => SetFullscreen(false);
            }

            // Aprobación de conexión de espectadores (Streaming)
            _streamServer.ClientApprovalCallback = async (clientId) =>
            {
                if (!RequireStreamApproval)
                    return true;

                return await RequestStreamApprovalAsync(clientId);
            };

            var approveStreamBtn = this.FindControl<Button>("ApproveStreamButton");
            if (approveStreamBtn != null) approveStreamBtn.Click += (s, e) => _streamApprovalTcs?.TrySetResult(true);

            var rejectStreamBtn = this.FindControl<Button>("RejectStreamButton");
            if (rejectStreamBtn != null) rejectStreamBtn.Click += (s, e) => _streamApprovalTcs?.TrySetResult(false);

            // Aprobación de control remoto
            _controlServer.ClientApprovalCallback = async (clientId) =>
            {
                if (!_allowRemoteControl || RemoteControlMode == "disabled")
                    return false;

                if (RemoteControlMode == "auto")
                    return true;

                return await RequestControlApprovalAsync(clientId);
            };

            var approveControlBtn = this.FindControl<Button>("ApproveControlButton");
            if (approveControlBtn != null) approveControlBtn.Click += (s, e) => _controlApprovalTcs?.TrySetResult(true);

            var rejectControlBtn = this.FindControl<Button>("RejectControlButton");
            if (rejectControlBtn != null) rejectControlBtn.Click += (s, e) => _controlApprovalTcs?.TrySetResult(false);

            // Eventos de entrada sobre ScreenImage para control remoto
            var screenImage = this.FindControl<Image>("ScreenImage");
            if (screenImage != null)
            {
                screenImage.DoubleTapped += (s, e) => ToggleFullscreen();
                screenImage.PointerMoved += ScreenImage_PointerMoved;
                screenImage.PointerPressed += ScreenImage_PointerPressed;
                screenImage.PointerReleased += ScreenImage_PointerReleased;
                screenImage.PointerWheelChanged += ScreenImage_PointerWheelChanged;
                screenImage.KeyDown += ScreenImage_KeyDown;
                screenImage.KeyUp += ScreenImage_KeyUp;
            }

            // Controles de ajuste de transmisión
            var engineCombo = this.FindControl<ComboBox>("EngineComboBox");
            if (engineCombo != null)
            {
                engineCombo.SelectionChanged += (s, e) => OnEngineChanged();
            }

            var codecCombo = this.FindControl<ComboBox>("CodecComboBox");
            if (codecCombo != null)
            {
                codecCombo.SelectionChanged += (s, e) => OnSettingChanged();
            }

            var fpsCombo = this.FindControl<ComboBox>("FpsComboBox");
            if (fpsCombo != null)
            {
                fpsCombo.SelectionChanged += (s, e) => OnSettingChanged();
            }

            var qualitySlider = this.FindControl<Slider>("QualitySlider");
            if (qualitySlider != null)
            {
                qualitySlider.PropertyChanged += (s, e) =>
                {
                    if (e.Property.Name == nameof(Slider.Value))
                    {
                        OnSettingChanged();
                    }
                };
            }

            var scaleCombo = this.FindControl<ComboBox>("ScaleComboBox");
            if (scaleCombo != null)
            {
                scaleCombo.SelectionChanged += (s, e) => OnSettingChanged();
            }

            // Temporizador de métricas (1 segundo)
            _metricsTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _metricsTimer.Tick += MetricsTimer_Tick;
            _metricsTimer.Start();

            // Indicador de permisos de Administrador para control de juegos/launchers
            var adminStatusBorder = this.FindControl<Border>("AdminStatusBorder");
            var adminStatusText = this.FindControl<TextBlock>("AdminStatusText");
            if (adminStatusBorder != null && adminStatusText != null)
            {
                if (IsRunningAsAdministrator())
                {
                    adminStatusBorder.Background = new SolidColorBrush(Color.Parse("#14532D"));
                    adminStatusText.Text = "🛡️ Modo Administrador Activo (Control de juegos y apps elevadas)";
                    adminStatusText.Foreground = new SolidColorBrush(Color.Parse("#4ADE80"));
                    ToolTip.SetTip(adminStatusBorder, "Permisos elevados activos: Puede interactuar libremente con launchers de juegos, Administrador de Tareas y ventanas administrativas.");
                }
                else
                {
                    adminStatusBorder.Background = new SolidColorBrush(Color.Parse("#451A03"));
                    adminStatusText.Text = "⚠️ Modo Estándar: Ejecuta como Administrador para controlar launchers y juegos";
                    adminStatusText.Foreground = new SolidColorBrush(Color.Parse("#FBBF24"));
                    ToolTip.SetTip(adminStatusBorder, "Windows restringe el control remoto sobre ventanas elevadas como launchers o juegos. Abre la aplicación con 'Ejecutar como Administrador' para habilitarlo.");
                }
            }

            this.Closing += MainWindow_Closing;
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

            double scaleX = controlBounds.Width / imageSize.Width;
            double scaleY = controlBounds.Height / imageSize.Height;
            double scale = Math.Min(scaleX, scaleY);

            double renderedW = imageSize.Width * scale;
            double renderedH = imageSize.Height * scale;

            double offsetX = (controlBounds.Width - renderedW) / 2.0;
            double offsetY = (controlBounds.Height - renderedH) / 2.0;

            var pos = e.GetPosition(screenImage);

            if (pos.X < offsetX || pos.X > offsetX + renderedW ||
                pos.Y < offsetY || pos.Y > offsetY + renderedH)
            {
                return false;
            }

            normX = (float)Math.Clamp((pos.X - offsetX) / renderedW, 0.0, 1.0);
            normY = (float)Math.Clamp((pos.Y - offsetY) / renderedH, 0.0, 1.0);
            return true;
        }

        private void MainWindow_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.L &&
                e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
                e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                ToggleGameMode();
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

        private void ToggleFullscreen()
        {
            SetFullscreen(!_isFullscreen);
        }

        private void SetFullscreen(bool fullscreen)
        {
            _isFullscreen = fullscreen;
            var sidebar = this.FindControl<Border>("SidebarBorder");
            var rootGrid = this.FindControl<Grid>("RootGrid");
            var fullscreenBtn = this.FindControl<Button>("FullscreenButton");
            var floatExitBtn = this.FindControl<Button>("ExitFullscreenFloatButton");

            if (_isFullscreen)
            {
                _previousWindowState = this.WindowState;
                this.WindowState = WindowState.FullScreen;

                if (sidebar != null) sidebar.IsVisible = false;
                if (rootGrid != null) rootGrid.ColumnDefinitions = new ColumnDefinitions("0,*");
                if (fullscreenBtn != null) fullscreenBtn.Content = "🗗 Salir";
                if (floatExitBtn != null) floatExitBtn.IsVisible = true;
            }
            else
            {
                this.WindowState = _previousWindowState == WindowState.FullScreen ? WindowState.Normal : _previousWindowState;

                if (sidebar != null) sidebar.IsVisible = true;
                if (rootGrid != null) rootGrid.ColumnDefinitions = new ColumnDefinitions("310,*");
                if (fullscreenBtn != null) fullscreenBtn.Content = "⛶ Pantalla Completa";
                if (floatExitBtn != null) floatExitBtn.IsVisible = false;
            }
        }

        private async Task<bool> RequestStreamApprovalAsync(string clientId)
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                _streamApprovalTcs?.TrySetResult(false);
                _streamApprovalTcs = new TaskCompletionSource<bool>();

                var banner = this.FindControl<Border>("StreamApprovalBanner");
                var subtitle = this.FindControl<TextBlock>("StreamApprovalSubtitle");
                if (subtitle != null) subtitle.Text = $"El dispositivo '{clientId}' solicita ver tu pantalla.";
                if (banner != null) banner.IsVisible = true;

                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    _streamApprovalTcs?.TrySetResult(false);
                };
                timer.Start();

                try
                {
                    return await _streamApprovalTcs.Task;
                }
                finally
                {
                    timer.Stop();
                    if (banner != null) banner.IsVisible = false;
                }
            });
        }

        private async Task<bool> RequestControlApprovalAsync(string clientId)
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                _controlApprovalTcs?.TrySetResult(false);
                _controlApprovalTcs = new TaskCompletionSource<bool>();

                var banner = this.FindControl<Border>("ControlApprovalBanner");
                var subtitle = this.FindControl<TextBlock>("ControlApprovalSubtitle");
                if (subtitle != null) subtitle.Text = $"El dispositivo '{clientId}' solicita controlar tu mouse y teclado.";
                if (banner != null) banner.IsVisible = true;

                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(25) };
                timer.Tick += (s, e) =>
                {
                    timer.Stop();
                    _controlApprovalTcs?.TrySetResult(false);
                };
                timer.Start();

                try
                {
                    return await _controlApprovalTcs.Task;
                }
                finally
                {
                    timer.Stop();
                    if (banner != null) banner.IsVisible = false;
                }
            });
        }

        private void ToggleGameMode()
        {
            if (!_controlClient.IsConnected)
                return;

            _isGameMode = !_isGameMode;
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

                _gameModeBannerTimer?.Stop();
                if (banner != null && bannerText != null)
                {
                    banner.IsVisible = true;
                    bannerText.Text = "🎯 Modo Juego 3D Activo — Presiona Ctrl + Alt + L para liberar el mouse";
                    bannerText.Foreground = new SolidColorBrush(Color.Parse("#38BDF8"));
                }
            }
            else
            {
                if (screenImage != null)
                {
                    screenImage.Cursor = Cursor.Default;
                }

                if (banner != null && bannerText != null)
                {
                    banner.IsVisible = true;
                    bannerText.Text = "🔓 Mouse Liberado — Presiona Ctrl + Alt + L para recapturar";
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

        private void ScreenImage_PointerMoved(object? sender, PointerEventArgs e)
        {
            if (!_remoteControlEnabled || !_controlClient.IsConnected)
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

        private void ScreenImage_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!_remoteControlEnabled || !_controlClient.IsConnected)
                return;

            var screenImage = this.FindControl<Image>("ScreenImage");
            screenImage?.Focus();

            var point = e.GetCurrentPoint(screenImage);
            var props = point.Properties;

            RemoteMouseButton button = props.IsLeftButtonPressed ? RemoteMouseButton.Left
                : props.IsRightButtonPressed ? RemoteMouseButton.Right
                : props.IsMiddleButtonPressed ? RemoteMouseButton.Middle
                : RemoteMouseButton.None;

            if (button == RemoteMouseButton.None)
                return;

            if (_isGameMode)
            {
                _ = _controlClient.SendInputAsync(new RemoteInputMessage
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
                _ = _controlClient.SendInputAsync(new RemoteInputMessage
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
            if (!_remoteControlEnabled || !_controlClient.IsConnected)
                return;

            var screenImage = this.FindControl<Image>("ScreenImage");
            var point = e.GetCurrentPoint(screenImage);
            var props = point.Properties;

            RemoteMouseButton button = props.PointerUpdateKind == PointerUpdateKind.LeftButtonReleased ? RemoteMouseButton.Left
                : props.PointerUpdateKind == PointerUpdateKind.RightButtonReleased ? RemoteMouseButton.Right
                : props.PointerUpdateKind == PointerUpdateKind.MiddleButtonReleased ? RemoteMouseButton.Middle
                : RemoteMouseButton.Left;

            if (_isGameMode)
            {
                _ = _controlClient.SendInputAsync(new RemoteInputMessage
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
                _ = _controlClient.SendInputAsync(new RemoteInputMessage
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
            if (!_remoteControlEnabled || !_controlClient.IsConnected)
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

            if (e.Key == Key.L &&
                e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
                e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                ToggleGameMode();
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
            if (e.Key == Key.L &&
                e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
                e.KeyModifiers.HasFlag(KeyModifiers.Alt))
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
            if (key >= Key.A && key <= Key.Z)
                return (int)key - (int)Key.A + 0x41;

            if (key >= Key.D0 && key <= Key.D9)
                return (int)key - (int)Key.D0 + 0x30;

            if (key >= Key.NumPad0 && key <= Key.NumPad9)
                return (int)key - (int)Key.NumPad0 + 0x60;

            if (key >= Key.F1 && key <= Key.F12)
                return (int)key - (int)Key.F1 + 0x70;

            return key switch
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
        }

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
            if (combo?.SelectedItem is ComboBoxItem item && item.Tag is string tag && int.TryParse(tag, out int fps))
            {
                return fps;
            }
            return 30;
        }

        private double GetSelectedScale()
        {
            var combo = this.FindControl<ComboBox>("ScaleComboBox");
            if (combo?.SelectedItem is ComboBoxItem item && item.Tag is string tag &&
                double.TryParse(tag, NumberStyles.Any, CultureInfo.InvariantCulture, out double scale))
            {
                return scale;
            }
            return 1.0;
        }

        private void OnEngineChanged()
        {
            if (_isHosting)
            {
                int fps = GetSelectedFps();
                int quality = (int)(this.FindControl<Slider>("QualitySlider")?.Value ?? 75);
                double scale = GetSelectedScale();
                string engine = GetSelectedEngine();
                string codec = GetSelectedCodec();

                SwitchCaptureEngine(engine, fps, quality, scale, codec);
                string engineName = engine == "dxgi" ? "DirectX DXGI (GPU)" : "GDI BitBlt (CPU)";
                UpdateStatus($"Cambiado a motor {engineName} ({codec.ToUpper()}) en caliente", "#22C55E");
            }
        }

        private void SwitchCaptureEngine(string engine, int fps, int quality, double scale, string codec)
        {
            _screenCaptureService.StopCapture();
            _screenCaptureService.FrameCaptured -= ScreenCaptureService_FrameCaptured;
            _screenCaptureService.Dispose();

            if (engine == "dxgi")
            {
                _screenCaptureService = new DxgiScreenCaptureService();
            }
            else
            {
                _screenCaptureService = new WindowsScreenCaptureService();
            }

            _screenCaptureService.FrameCaptured += ScreenCaptureService_FrameCaptured;
            _screenCaptureService.StartCapture(fps, quality, scale, codec);
        }

        private void OnSettingChanged()
        {
            int fps = GetSelectedFps();
            int quality = (int)(this.FindControl<Slider>("QualitySlider")?.Value ?? 75);
            double scale = GetSelectedScale();
            string codec = GetSelectedCodec();

            var fpsLabel = this.FindControl<TextBlock>("FpsValueLabel");
            if (fpsLabel != null)
            {
                fpsLabel.Text = $"{fps} FPS";
            }

            var qualityLabel = this.FindControl<TextBlock>("QualityValueLabel");
            if (qualityLabel != null)
            {
                qualityLabel.Text = $"{quality}%";
            }

            var scaleLabel = this.FindControl<TextBlock>("ScaleValueLabel");
            if (scaleLabel != null)
            {
                scaleLabel.Text = $"{scale * 100:0}%";
            }

            _screenCaptureService.UpdateSettings(fps, quality, scale, codec);
        }

        private void MetricsTimer_Tick(object? sender, EventArgs e)
        {
            long frames = Interlocked.Exchange(ref _metricFramesCount, 0);
            long bytes = Interlocked.Exchange(ref _metricBytesCount, 0);

            var statsBadge = this.FindControl<Border>("StatsBadge");
            var statsText = this.FindControl<TextBlock>("StatsText");

            if (_isHosting || (_streamClient != null && _streamClient.IsConnected))
            {
                if (statsBadge != null && statsText != null)
                {
                    statsBadge.IsVisible = true;
                    double kbPerSec = bytes / 1024.0;
                    string transferRate = kbPerSec >= 1024
                        ? $"{(kbPerSec / 1024.0):F1} MB/s"
                        : $"{kbPerSec:F0} KB/s";

                    string res = _lastFrameWidth > 0 ? $" • {_lastFrameWidth}x{_lastFrameHeight}" : "";
                    string engineTag = _isHosting
                        ? (_screenCaptureService is DxgiScreenCaptureService ? " • DXGI GPU" : " • GDI CPU")
                        : "";

                    string codecTag = _isHosting
                        ? $" • {_screenCaptureService.Codec.ToUpper()}"
                        : "";

                    string audioTag = (_isHosting && _audioServer.IsRunning) || (_streamClient.IsConnected && _audioClient.IsConnected)
                        ? " • 🔊 Audio"
                        : "";

                    string controlTag = _controlClient.IsConnected && _remoteControlEnabled
                        ? " • 🎮 Control Activo"
                        : "";

                    statsText.Text = $"{frames} FPS • {transferRate}{res}{engineTag}{codecTag}{audioTag}{controlTag}";
                }
            }
            else
            {
                if (statsBadge != null)
                {
                    statsBadge.IsVisible = false;
                }
            }
        }

        private void ResetMetrics()
        {
            Interlocked.Exchange(ref _metricFramesCount, 0);
            Interlocked.Exchange(ref _metricBytesCount, 0);
            _lastFrameWidth = 0;
            _lastFrameHeight = 0;

            var statsBadge = this.FindControl<Border>("StatsBadge");
            if (statsBadge != null)
            {
                statsBadge.IsVisible = false;
            }
        }

        #endregion

        #region Hosting Logic

        private async void HostButton_Click(object? sender, RoutedEventArgs e)
        {
            var hostBtn = this.FindControl<Button>("HostButton");

            if (!_isHosting)
            {
                // Iniciar Host
                try
                {
                    int fps = GetSelectedFps();
                    int quality = (int)(this.FindControl<Slider>("QualitySlider")?.Value ?? 75);
                    double scale = GetSelectedScale();
                    string engine = GetSelectedEngine();
                    string codec = GetSelectedCodec();

                    // 1. Iniciar servidor de video (Puerto 5001)
                    await _streamServer.StartAsync(5001);
                    SwitchCaptureEngine(engine, fps, quality, scale, codec);

                    // 2. Iniciar servidor y captura de audio (Puerto 5002) si está habilitado
                    bool shareAudio = this.FindControl<CheckBox>("AudioShareCheckBox")?.IsChecked ?? true;
                    if (shareAudio)
                    {
                        try
                        {
                            await _audioServer.StartAsync(5002);
                            _audioCaptureService?.StartCapture();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Audio] Advertencia iniciando audio: {ex.Message}");
                        }
                    }

                    // 3. Iniciar servidor de control remoto (Puerto 5003)
                    await _controlServer.StartAsync(5003);

                    _discoveryService.StartHostingBroadcast();

                    _isHosting = true;
                    ResetMetrics();

                    if (hostBtn != null)
                    {
                        hostBtn.Content = "Detener Transmisión";
                        hostBtn.Background = new SolidColorBrush(Color.Parse("#DC2626"));
                    }

                    string engineName = engine == "dxgi" ? "DirectX DXGI (GPU)" : "GDI BitBlt (CPU)";
                    string audioStatus = shareAudio ? " + Audio WASAPI" : "";
                    UpdateStatus($"Transmitiendo pantalla con {engineName}{audioStatus} - Esperando espectadores", "#22C55E");
                }
                catch (Exception ex)
                {
                    UpdateStatus($"Error al iniciar transmisión: {ex.Message}", "#EF4444");
                }
            }
            else
            {
                // Detener Host
                _screenCaptureService.StopCapture();
                _streamServer.Stop();

                _audioCaptureService?.StopCapture();
                _audioServer.Stop();

                _controlServer.Stop();

                _discoveryService.StopHosting();

                _isHosting = false;
                ResetMetrics();

                if (hostBtn != null)
                {
                    hostBtn.Content = "Transmitir mi pantalla";
                    hostBtn.Background = new SolidColorBrush(Color.Parse("#3B82F6"));
                }

                UpdateStatus("Transmisión detenida", "#71717A");
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
            Dispatcher.UIThread.Post(() =>
            {
                int count = _streamServer.ConnectedClientsCount;
                UpdateStatus($"Transmitiendo pantalla activa - {count} espectador(es) conectado(s)", "#22C55E");
            });
        }

        private void StreamServer_ClientDisconnected(object? sender, string clientId)
        {
            Dispatcher.UIThread.Post(() =>
            {
                int count = _streamServer.ConnectedClientsCount;
                if (_isHosting)
                {
                    UpdateStatus($"Transmitiendo pantalla activa - {count} espectador(es) conectado(s)", "#22C55E");
                }
            });
        }

        #endregion

        #region Discovery & Client Viewing Logic

        private void StopDeviceSearch()
        {
            _searchTimer?.Stop();
            _searchTimer = null;
            _discoveryService.StopListening();

            var searchBtn = this.FindControl<Button>("SearchButton");
            if (searchBtn != null)
            {
                searchBtn.Content = "Buscar dispositivos";
                searchBtn.IsEnabled = true;
            }

            var localInfo = this.FindControl<TextBlock>("LocalInfoText");
            if (localInfo != null)
            {
                localInfo.Text = Devices.Count > 0
                    ? $"Detectados {Devices.Count} dispositivo(s)"
                    : "Listo para buscar";
            }
        }

        private void SearchButton_Click(object? sender, RoutedEventArgs e)
        {
            _searchTimer?.Stop();
            _discoveryService.StartListening();

            if (sender is Button searchBtn)
            {
                searchBtn.Content = "Buscando en LAN...";
                searchBtn.IsEnabled = false;
            }

            var localInfo = this.FindControl<TextBlock>("LocalInfoText");
            if (localInfo != null)
            {
                localInfo.Text = "Escuchando anuncios en puerto UDP 5000...";
            }

            // Auto-detener búsqueda después de 10 segundos para no quedar en bucle
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
                }
                else
                {
                    Devices.Add(device);
                }
            });
        }

        private async void ConnectDevice_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is DeviceInfo device)
            {
                await ConnectToDeviceAsync(device);
            }
        }

        private async Task ConnectToDeviceAsync(DeviceInfo device)
        {
            StopDeviceSearch();
            _currentConnectedDeviceName = device.Name;
            UpdateStatus($"Conectando a {device.Name} ({device.IPAddress}:5001)...", "#F59E0B");
            ResetMetrics();

            try
            {
                // 1. Conectar flujo de video
                await _streamClient.ConnectAsync(device.IPAddress, 5001);

                // 2. Conectar flujo de audio en paralelo
                try
                {
                    await _audioClient.ConnectAsync(device.IPAddress, 5002);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AudioClient] Audio no disponible en el host: {ex.Message}");
                }

                // 3. Conectar canal de control remoto
                try
                {
                    await _controlClient.ConnectAsync(device.IPAddress, 5003);
                    Console.WriteLine($"[ControlViewer] Conectado a canal de control remoto en {device.IPAddress}:5003");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ControlClient] Control remoto no disponible: {ex.Message}");
                }

                var fullscreenBtn = this.FindControl<Button>("FullscreenButton");
                if (fullscreenBtn != null)
                {
                    fullscreenBtn.IsVisible = true;
                }

                var disconnectBtn = this.FindControl<Button>("DisconnectButton");
                if (disconnectBtn != null)
                {
                    disconnectBtn.IsVisible = true;
                }

                var muteBtn = this.FindControl<Button>("AudioMuteButton");
                if (muteBtn != null)
                {
                    muteBtn.IsVisible = true;
                    muteBtn.Content = (_audioPlayerService?.IsMuted ?? false) ? "🔇" : "🔊";
                }

                var remoteControlBtn = this.FindControl<ToggleButton>("RemoteControlButton");
                if (remoteControlBtn != null)
                {
                    remoteControlBtn.IsVisible = true;
                    remoteControlBtn.IsChecked = false;
                    remoteControlBtn.IsEnabled = _controlClient.IsConnected;
                    ToolTip.SetTip(remoteControlBtn, _controlClient.IsConnected
                        ? "Habilitar interacción con mouse y teclado"
                        : "Control remoto no disponible (puerto 5003 no respondió en el host)");
                }

                var gameModeBtn = this.FindControl<ToggleButton>("GameModeButton");
                if (gameModeBtn != null)
                {
                    gameModeBtn.IsVisible = true;
                    gameModeBtn.IsChecked = false;
                    gameModeBtn.IsEnabled = _controlClient.IsConnected;
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"No se pudo conectar a {device.Name}: {ex.Message}", "#EF4444");
            }
        }

        private void StreamClient_FrameReceived(object? sender, CapturedFrame frame)
        {
            Interlocked.Increment(ref _metricFramesCount);
            Interlocked.Add(ref _metricBytesCount, frame.Data.Length);
            _lastFrameWidth = frame.Width;
            _lastFrameHeight = frame.Height;

            try
            {
                if (frame.Format == "h264" && _h264Decoder != null)
                {
                    if (_h264Decoder.TryDecodeFrame(frame.Data, frame.Width, frame.Height, out byte[] bgraPixels, out int decW, out int decH))
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            try
                            {
                                lock (_bitmapLock)
                                {
                                    if (_viewerWriteableBitmap == null ||
                                        _viewerWriteableBitmap.PixelSize.Width != decW ||
                                        _viewerWriteableBitmap.PixelSize.Height != decH)
                                    {
                                        _viewerWriteableBitmap?.Dispose();
                                        _viewerWriteableBitmap = new WriteableBitmap(
                                            new PixelSize(decW, decH),
                                            new Vector(96, 96),
                                            PixelFormat.Bgra8888,
                                            AlphaFormat.Premul);
                                    }

                                    using (var fb = _viewerWriteableBitmap.Lock())
                                    {
                                        int copyLength = Math.Min(bgraPixels.Length, fb.RowBytes * decH);
                                        Marshal.Copy(bgraPixels, 0, fb.Address, copyLength);
                                    }
                                }

                                var screenImage = this.FindControl<Image>("ScreenImage");
                                if (screenImage != null)
                                {
                                    screenImage.Source = _viewerWriteableBitmap;
                                    screenImage.InvalidateVisual();
                                }

                                var placeholder = this.FindControl<StackPanel>("PlaceholderPanel");
                                if (placeholder != null && placeholder.IsVisible)
                                {
                                    placeholder.IsVisible = false;
                                }

                                UpdateStatus($"Recibiendo pantalla de {_currentConnectedDeviceName} (H.264 GPU)", "#22C55E");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[StreamViewer] Error renderizando frame H.264: {ex.Message}");
                            }
                        });
                        return;
                    }
                }

                using var ms = new MemoryStream(frame.Data);
                var bitmap = new Bitmap(ms);

                Dispatcher.UIThread.Post(() =>
                {
                    var screenImage = this.FindControl<Image>("ScreenImage");
                    if (screenImage != null)
                    {
                        screenImage.Source = bitmap;
                    }

                    var placeholder = this.FindControl<StackPanel>("PlaceholderPanel");
                    if (placeholder != null && placeholder.IsVisible)
                    {
                        placeholder.IsVisible = false;
                    }

                    UpdateStatus($"Recibiendo pantalla de {_currentConnectedDeviceName} (JPEG)", "#22C55E");
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[StreamViewer] Error renderizando frame: {ex.Message}");
            }
        }

        private void StreamClient_StatusChanged(object? sender, string status)
        {
            Dispatcher.UIThread.Post(() =>
            {
                UpdateStatus(status, _streamClient.IsConnected ? "#22C55E" : "#71717A");

                var disconnectBtn = this.FindControl<Button>("DisconnectButton");
                if (disconnectBtn != null)
                {
                    disconnectBtn.IsVisible = _streamClient.IsConnected;
                }

                var muteBtn = this.FindControl<Button>("AudioMuteButton");
                if (muteBtn != null)
                {
                    muteBtn.IsVisible = _streamClient.IsConnected;
                }

                var remoteControlBtn = this.FindControl<ToggleButton>("RemoteControlButton");
                if (remoteControlBtn != null)
                {
                    remoteControlBtn.IsVisible = _streamClient.IsConnected;
                }

                var gameModeBtn = this.FindControl<ToggleButton>("GameModeButton");
                if (gameModeBtn != null)
                {
                    gameModeBtn.IsVisible = _streamClient.IsConnected;
                    if (!_streamClient.IsConnected) gameModeBtn.IsChecked = false;
                }
            });
        }

        private async void DisconnectButton_Click(object? sender, RoutedEventArgs e)
        {
            SetFullscreen(false);
            StopDeviceSearch();

            await _streamClient.DisconnectAsync();
            await _audioClient.DisconnectAsync();
            await _controlClient.DisconnectAsync();

            _audioPlayerService?.Stop();
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

            var placeholder = this.FindControl<StackPanel>("PlaceholderPanel");
            if (placeholder != null)
            {
                placeholder.IsVisible = true;
            }

            var fullscreenBtn = this.FindControl<Button>("FullscreenButton");
            if (fullscreenBtn != null)
            {
                fullscreenBtn.IsVisible = false;
            }

            var disconnectBtn = this.FindControl<Button>("DisconnectButton");
            if (disconnectBtn != null)
            {
                disconnectBtn.IsVisible = false;
            }

            var muteBtn = this.FindControl<Button>("AudioMuteButton");
            if (muteBtn != null)
            {
                muteBtn.IsVisible = false;
            }

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

            UpdateStatus("Desconectado", "#71717A");
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

        #region Win32 P/Invoke for Mouse Centering

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

        private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
        {
            SetFullscreen(false);
            StopDeviceSearch();
            _metricsTimer.Stop();

            _h264Decoder?.Dispose();
            _viewerWriteableBitmap?.Dispose();

            _screenCaptureService.Dispose();
            _streamServer.Dispose();
            _streamClient.Dispose();

            _audioCaptureService?.Dispose();
            _audioPlayerService?.Dispose();
            _audioServer.Dispose();
            _audioClient.Dispose();

            _controlServer.Dispose();
            _controlClient.Dispose();
            _inputSimulatorService?.Dispose();

            _discoveryService.StopHosting();
            _discoveryService.StopListening();
        }

        #endregion
    }
}
