using System;
using System.Collections.Generic;
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
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LanScreenShare.Core.Security;
using LanScreenShare.Network.Security;
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
        private readonly TcpStreamServer _streamServer;
        private readonly TcpStreamClient _streamClient;

        // Seguridad, autoridad de sesiones e identidad mTLS
        private readonly IIdentityKeyStorage _identityKeyStorage;
        private readonly IPairedDeviceStore _pairedDeviceStore;
        private readonly ISessionAuthority _sessionAuthority;
        private X509Certificate2? _identityCertificate;
        private SslServerAuthenticationOptions? _sslServerOptions;
        private SslClientAuthenticationOptions? _sslClientOptions;

        // Servicios de streaming de audio
        private readonly IAudioCaptureService? _audioCaptureService;
        private readonly IAudioPlayerService? _audioPlayerService;
        private readonly IAudioCaptureService? _micCaptureService;
        private readonly IAudioPlayerService? _micAudioPlayerService;
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
        private bool _isViewing;
        private string? _currentConnectedDeviceName;
        private string? _currentConnectedDeviceIp;
        private bool _currentDeviceAllowsRemoteControl;

        // Pantalla completa y barra superior auto-ocultable
        private bool _isFullscreen;
        private WindowState _previousWindowState = WindowState.Normal;
        private readonly DispatcherTimer _headerAutoHideTimer;
        private bool _isMouseOverHeaderBar;

        // Ventana emergente (Pop-out) y modo de ajuste de pantalla
        private StreamPopoutWindow? _popoutWindow;
        private bool _isPopoutActive;
        private Stretch _currentStretchMode = Stretch.Uniform;

        // BÃºsqueda de dispositivos
        private DispatcherTimer? _searchTimer;

        // Control de acceso y aprobaciÃ³n de conexiones
        private volatile bool _requireStreamApproval = false;
        private volatile string _remoteControlMode = "auto";
        private TaskCompletionSource<bool>? _streamApprovalTcs;
        private TaskCompletionSource<bool>? _controlApprovalTcs;

        // Decodificador de video por hardware (H.264)
        private readonly IVideoDecoderService? _h264Decoder;
        private WriteableBitmap? _viewerWriteableBitmap;
        private readonly object _bitmapLock = new();

        // EstadÃ­sticas de flujo en tiempo real
        private long _metricFramesCount;
        private long _metricBytesCount;
        private int _lastFrameWidth;
        private int _lastFrameHeight;
        private int _lastHostFps;
        private int _lastHostFrameTimeMs;
        private readonly DispatcherTimer _metricsTimer;
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

            if (OperatingSystem.IsWindows())
            {
                var availableScreens = CaptureSourceDiscoveryService.GetScreenSources();
                CaptureSource? matched = null;

                if (!string.IsNullOrEmpty(_appSettings.CaptureSourceId) && !_appSettings.CaptureSourceId.StartsWith("window:"))
                {
                    matched = availableScreens.FirstOrDefault(s => s.Id == _appSettings.CaptureSourceId);
                }

                if (matched != null)
                {
                    _currentSource = matched;
                }
                else if (availableScreens.Count > 0)
                {
                    _currentSource = availableScreens.FirstOrDefault(s => s.IsPrimary) ?? availableScreens[0];
                }
            }

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

            // Iniciar con la fuente y motor guardados en configuraciÃ³n
            _screenCaptureService = CreateCaptureService(_currentSource, _appSettings.Engine);

            _screenCaptureService.FrameCaptured += ScreenCaptureService_FrameCaptured;

            IInputSimulatorService? inputSimulator = null;
            if (OperatingSystem.IsWindows())
            {
                inputSimulator = new WindowsInputSimulatorService();
                _inputSimulatorService = inputSimulator;
            }

            // InicializaciÃ³n de subsistema de Seguridad, Autoridad de Sesiones y mTLS
            _identityKeyStorage = new WindowsDpapiIdentityKeyStorage();
            _pairedDeviceStore = new JsonPairedDeviceStore();
            _sessionAuthority = new SessionAuthority(_pairedDeviceStore);

            try
            {
                _identityCertificate = _identityKeyStorage.GetOrCreateIdentityCertificateAsync().GetAwaiter().GetResult();
                _sslServerOptions = TlsConfigurationHelper.CreateServerOptions(_identityCertificate, _pairedDeviceStore, OnProvisionalPairingRequested);
                _sslClientOptions = TlsConfigurationHelper.CreateClientOptions(_identityCertificate, "LanScreenShareHost", _pairedDeviceStore, OnClientProvisionalPairing);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Security] Error inicializando identidad mTLS: {ex.Message}");
            }

            _streamServer = new TcpStreamServer(_sessionAuthority, _sslServerOptions, _pairedDeviceStore);
            _streamServer.PairingApprovalCallback = (fingerprint, deviceName, sasFormatted) => RequestPairingApprovalAsync(deviceName, fingerprint, sasFormatted);
            _streamServer.ClientConnected += StreamServer_ClientConnected;
            _streamServer.ClientDisconnected += StreamServer_ClientDisconnected;

            _streamClient = new TcpStreamClient();
            _streamClient.FrameReceived += StreamClient_FrameReceived;
            _streamClient.StatusChanged += StreamClient_StatusChanged;

            // InicializaciÃ³n de subsistema de Audio
            _audioServer = new TcpAudioServer(_sessionAuthority, _sslServerOptions);
            _audioClient = new TcpAudioClient();

            // InicializaciÃ³n de subsistema de Control Remoto
            _controlServer = new TcpControlServer(_sessionAuthority, _inputSimulatorService, _sslServerOptions);
            _controlClient = new TcpControlClient();

            if (OperatingSystem.IsWindows())
            {
                _audioCaptureService = new WasapiAudioCaptureService();
                _audioPlayerService = new WasapiAudioPlayerService();
                _micCaptureService = new WasapiMicrophoneCaptureService();
                _micAudioPlayerService = new WasapiAudioPlayerService();
                // Input simulator ya inicializado previamente para TcpControlServer

                _audioCaptureService.AudioCaptured += async (s, chunk) =>
                {
                    if (_isHosting && _audioServer.IsRunning)
                    {
                        await _audioServer.BroadcastAudioAsync(chunk);
                    }
                };

                _micCaptureService.AudioCaptured += async (s, chunk) =>
                {
                    if (_isHosting && _audioServer.IsRunning)
                    {
                        await _audioServer.BroadcastAudioAsync(chunk);
                    }
                };

                _audioClient.AudioReceived += (s, chunk) =>
                {
                    if (chunk.Source == AudioSourceType.Microphone)
                    {
                        _micAudioPlayerService?.PlayChunk(chunk);
                    }
                    else
                    {
                        _audioPlayerService?.PlayChunk(chunk);
                    }
                };
            }

            _discoveryService.IsRemoteControlAllowedProvider = () => _allowRemoteControl;

            var allowCb = this.FindControl<CheckBox>("AllowRemoteControlCheckBox");
            var controlAccessPanel = this.FindControl<StackPanel>("ControlAccessModePanel");

            if (allowCb != null)
            {
                allowCb.IsChecked = _appSettings.AllowRemoteControl;
                _allowRemoteControl = _appSettings.AllowRemoteControl;
                if (controlAccessPanel != null)
                {
                    controlAccessPanel.IsVisible = _allowRemoteControl;
                }

                allowCb.IsCheckedChanged += (s, e) =>
                {
                    _allowRemoteControl = allowCb.IsChecked == true;
                    if (controlAccessPanel != null)
                    {
                        controlAccessPanel.IsVisible = _allowRemoteControl;
                    }

                    if (_isHosting)
                    {
                        if (_allowRemoteControl)
                        {
                            if (!_controlServer.IsRunning)
                            {
                                _ = _controlServer.StartAsync(5003);
                            }
                        }
                        else
                        {
                            _controlServer.Stop();
                        }
                    }
                    Console.WriteLine($"[ControlHost] Permiso de control remoto: {_allowRemoteControl}");
                    SaveCurrentSettings();
                };
            }

            _controlServer.InputReceived += (s, input) =>
            {
                if (_allowRemoteControl)
                {
                    // Inyección gestionada y rastreada directamente por TcpControlServer
                    // _inputSimulatorService?.SimulateInput(input);
                }
            };

            var devicesList = this.FindControl<ListBox>("DevicesList");
            if (devicesList != null)
            {
                devicesList.ItemsSource = Devices;
            }
            Devices.CollectionChanged += (s, e) => UpdateDevicesSectionUI();
            UpdateDevicesSectionUI();

            var hostButton = this.FindControl<Button>("HostButton");
            if (hostButton != null)
            {
                hostButton.Click += HostButton_Click;
            }

            var centerHostButton = this.FindControl<Button>("CenterHostButton");
            if (centerHostButton != null)
            {
                centerHostButton.Click += HostButton_Click;
            }

            var centerSelectSourceBtn = this.FindControl<Button>("CenterSelectSourceButton");
            if (centerSelectSourceBtn != null)
            {
                centerSelectSourceBtn.Click += SelectSourceButton_Click;
            }

            var helpButton = this.FindControl<Button>("HelpButton");
            if (helpButton != null)
            {
                helpButton.Click += (s, e) =>
                {
                    UpdateStatus("Ayuda: Presiona 'Transmitir mi pantalla' o 'Buscar dispositivos' en tu red.", "#3B82F6");
                };
            }

            var searchButton = this.FindControl<Button>("SearchButton");
            if (searchButton != null)
            {
                searchButton.Click += SearchButton_Click;
            }

            var collapseLeftBtn = this.FindControl<Button>("CollapseLeftSidebarButton");
            if (collapseLeftBtn != null)
            {
                collapseLeftBtn.Click += (s, e) => SetLeftSidebarCollapsed(true);
            }

            var expandLeftBtn = this.FindControl<Button>("ExpandLeftSidebarButton");
            if (expandLeftBtn != null)
            {
                expandLeftBtn.Click += (s, e) => SetLeftSidebarCollapsed(false);
            }

            var collapseRightBtn = this.FindControl<Button>("CollapseRightSidebarButton");
            if (collapseRightBtn != null)
            {
                collapseRightBtn.Click += (s, e) => SetRightSidebarCollapsed(true);
            }

            var expandRightBtn = this.FindControl<Button>("ExpandRightSidebarButton");
            if (expandRightBtn != null)
            {
                expandRightBtn.Click += (s, e) => SetRightSidebarCollapsed(false);
            }

            var disconnectButton = this.FindControl<Button>("DisconnectButton");
            if (disconnectButton != null)
            {
                disconnectButton.Click += DisconnectButton_Click;
            }

            var audioMuteButton = this.FindControl<Button>("AudioMuteButton");
            if (audioMuteButton != null)
            {
                audioMuteButton.Click += (s, e) => ToggleMute();
            }

            var volumeSlider = this.FindControl<Slider>("AudioVolumeSlider");
            if (volumeSlider != null)
            {
                volumeSlider.Value = Math.Clamp((int)Math.Round(_appSettings.AudioVolume * 100), 0, 100);
                volumeSlider.PropertyChanged += (s, e) =>
                {
                    if (e.Property.Name == nameof(Slider.Value))
                    {
                        SetAudioVolume((float)Math.Clamp(volumeSlider.Value / 100.0, 0.0, 1.0));
                    }
                };
            }

            if (_audioPlayerService != null)
            {
                _audioPlayerService.Volume = _appSettings.AudioVolume;
                _audioPlayerService.IsMuted = _appSettings.AudioMuted;
                UpdateAudioUI();
            }

            var micMuteButton = this.FindControl<Button>("MicMuteButton");
            if (micMuteButton != null)
            {
                micMuteButton.Click += (s, e) => ToggleMicMute();
            }

            var micVolumeSlider = this.FindControl<Slider>("MicVolumeSlider");
            if (micVolumeSlider != null)
            {
                micVolumeSlider.Value = Math.Clamp((int)Math.Round(_appSettings.MicVolume * 100), 0, 100);
                micVolumeSlider.PropertyChanged += (s, e) =>
                {
                    if (e.Property.Name == nameof(Slider.Value))
                    {
                        SetMicVolume((float)Math.Clamp(micVolumeSlider.Value / 100.0, 0.0, 1.0));
                    }
                };
            }

            if (_micAudioPlayerService != null)
            {
                _micAudioPlayerService.Volume = _appSettings.MicVolume;
                _micAudioPlayerService.IsMuted = _appSettings.MicMuted;
                UpdateMicUI();
            }

            var micShareCb = this.FindControl<CheckBox>("MicShareCheckBox");
            if (micShareCb != null)
            {
                micShareCb.IsChecked = _appSettings.ShareMicrophone;
                micShareCb.IsCheckedChanged += async (s, e) =>
                {
                    bool isChecked = micShareCb.IsChecked == true;
                    _appSettings.ShareMicrophone = isChecked;
                    UpdateCanvasFooterAudioStatus();
                    SaveCurrentSettings();

                    if (_isHosting)
                    {
                        if (isChecked)
                        {
                            try
                            {
                                await _audioServer.StartAsync(5002);
                                _micCaptureService?.StartCapture();
                                Console.WriteLine("[Host] Captura de micrófono iniciada dinámicamente.");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[Host] Error activando micrófono: {ex.Message}");
                            }
                        }
                        else
                        {
                            _micCaptureService?.StopCapture();
                            Console.WriteLine("[Host] Captura de micrófono detenida dinámicamente.");
                        }
                    }
                };
            }

            var remoteControlBtn = this.FindControl<ToggleButton>("RemoteControlButton");
            if (remoteControlBtn != null)
            {
                remoteControlBtn.IsCheckedChanged += async (s, e) =>
                {
                    if (remoteControlBtn.IsChecked == true)
                    {
                        bool connected = await EnsureControlConnectedAsync();
                        if (!connected)
                        {
                            remoteControlBtn.IsChecked = false;
                            _remoteControlEnabled = false;
                            return;
                        }

                        _remoteControlEnabled = true;
                        remoteControlBtn.Background = new SolidColorBrush(Color.Parse("#2563EB"));

                        var screenImg = this.FindControl<Image>("ScreenImage");
                        screenImg?.Focus();
                    }
                    else
                    {
                        _remoteControlEnabled = false;
                        remoteControlBtn.Background = new SolidColorBrush(Color.Parse("#27272A"));

                        if (_isGameMode)
                        {
                            _isGameMode = false;
                            UpdateGameModeUI();
                        }
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

            // Controles de Pantalla Completa y Barra Superior Auto-Ocultable
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

            this.AddHandler(InputElement.PointerMovedEvent, MainWindow_GlobalPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

            var fullscreenBtn = this.FindControl<Button>("FullscreenButton");
            if (fullscreenBtn != null)
            {
                fullscreenBtn.Click += (s, e) => ToggleFullscreen();
            }

            _currentStretchMode = _appSettings.StretchMode == "Fill" ? Stretch.Fill : Stretch.Uniform;
            var stretchBtn = this.FindControl<Button>("StretchModeButton");
            if (stretchBtn != null)
            {
                stretchBtn.Click += (s, e) => ToggleStretchMode();
            }
            var initialScreenImage = this.FindControl<Image>("ScreenImage");
            if (initialScreenImage != null)
            {
                initialScreenImage.Stretch = _currentStretchMode;
            }
            var initialStretchBtnText = this.FindControl<TextBlock>("StretchModeButtonText");
            if (initialStretchBtnText != null)
            {
                initialStretchBtnText.Text = _currentStretchMode == Stretch.Uniform ? "Ajustar" : "Llenar";
            }

            var popoutBtn = this.FindControl<Button>("PopoutWindowButton");
            if (popoutBtn != null)
            {
                popoutBtn.Click += (s, e) => OpenPopoutWindow();
            }

            var reattachBtn = this.FindControl<Button>("ReattachStreamButton");
            if (reattachBtn != null)
            {
                reattachBtn.Click += (s, e) => ClosePopoutWindow();
            }

            var exitFloatBtn = this.FindControl<Button>("ExitFullscreenFloatButton");
            if (exitFloatBtn != null)
            {
                exitFloatBtn.Click += (s, e) => SetFullscreen(false);
            }

            // Modos de acceso y aprobaciÃ³n de conexiones
            var streamAccessCombo = this.FindControl<ComboBox>("StreamAccessModeComboBox");
            if (streamAccessCombo != null)
            {
                for (int i = 0; i < streamAccessCombo.Items.Count; i++)
                {
                    if (streamAccessCombo.Items[i] is ComboBoxItem item && item.Tag as string == _appSettings.StreamAccessMode)
                    {
                        streamAccessCombo.SelectedIndex = i;
                        break;
                    }
                }
                _requireStreamApproval = (streamAccessCombo.SelectedItem as ComboBoxItem)?.Tag as string == "ask";
                streamAccessCombo.SelectionChanged += (s, e) =>
                {
                    _requireStreamApproval = (streamAccessCombo.SelectedItem as ComboBoxItem)?.Tag as string == "ask";
                    Console.WriteLine($"[AccessHost] AprobaciÃ³n de stream requerida: {_requireStreamApproval}");
                    SaveCurrentSettings();
                };
            }

            var controlAccessCombo = this.FindControl<ComboBox>("ControlAccessModeComboBox");
            if (controlAccessCombo != null)
            {
                for (int i = 0; i < controlAccessCombo.Items.Count; i++)
                {
                    if (controlAccessCombo.Items[i] is ComboBoxItem item && item.Tag as string == _appSettings.ControlAccessMode)
                    {
                        controlAccessCombo.SelectedIndex = i;
                        break;
                    }
                }
                _remoteControlMode = (controlAccessCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
                controlAccessCombo.SelectionChanged += (s, e) =>
                {
                    _remoteControlMode = (controlAccessCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
                    Console.WriteLine($"[AccessHost] Modo de control remoto: {_remoteControlMode}");
                    SaveCurrentSettings();
                };
            }

            // AprobaciÃ³n de conexiÃ³n de espectadores (Streaming)
            _streamServer.ClientApprovalCallback = async (clientId) =>
            {
                if (!_requireStreamApproval)
                    return true;

                return await RequestStreamApprovalAsync(clientId);
            };

            var approveStreamBtn = this.FindControl<Button>("ApproveStreamButton");
            if (approveStreamBtn != null) approveStreamBtn.Click += (s, e) => _streamApprovalTcs?.TrySetResult(true);

            var rejectStreamBtn = this.FindControl<Button>("RejectStreamButton");
            if (rejectStreamBtn != null) rejectStreamBtn.Click += (s, e) => _streamApprovalTcs?.TrySetResult(false);

            // AprobaciÃ³n de control remoto
            _controlServer.ClientApprovalCallback = async (clientId) =>
            {
                if (!_allowRemoteControl)
                    return false;

                if (_remoteControlMode == "auto")
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
                screenImage.DoubleTapped += (s, e) =>
                {
                    // Si el control remoto o el modo juego estÃ¡n activos, no alternar pantalla completa
                    // para permitir abrir carpetas, archivos y hacer clics rÃ¡pidos en juegos del anfitriÃ³n
                    if (_remoteControlEnabled || _isGameMode)
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

            // Selector de fuente de transmisiÃ³n (Pantalla completa / Ventana de app)
            var selectSourceBtn = this.FindControl<Button>("SelectSourceButton");
            if (selectSourceBtn != null)
            {
                selectSourceBtn.Click += SelectSourceButton_Click;
            }
            UpdateSourceUI();

            // Controles de ajuste de transmisiÃ³n
            var engineCombo = this.FindControl<ComboBox>("EngineComboBox");
            if (engineCombo != null)
            {
                for (int i = 0; i < engineCombo.Items.Count; i++)
                {
                    if (engineCombo.Items[i] is ComboBoxItem item && item.Tag as string == _appSettings.Engine)
                    {
                        engineCombo.SelectedIndex = i;
                        break;
                    }
                }
                engineCombo.SelectionChanged += (s, e) =>
                {
                    OnEngineChanged();
                    SaveCurrentSettings();
                };
            }

            var codecCombo = this.FindControl<ComboBox>("CodecComboBox");
            if (codecCombo != null)
            {
                for (int i = 0; i < codecCombo.Items.Count; i++)
                {
                    if (codecCombo.Items[i] is ComboBoxItem item && item.Tag as string == _appSettings.Codec)
                    {
                        codecCombo.SelectedIndex = i;
                        break;
                    }
                }
                codecCombo.SelectionChanged += (s, e) =>
                {
                    string selCodec = GetSelectedCodec();
                    UpdateQualitySliderForCodec(selCodec);
                    OnSettingChanged();
                    SaveCurrentSettings();
                };
            }

            var fpsCombo = this.FindControl<ComboBox>("FpsComboBox");
            if (fpsCombo != null)
            {
                for (int i = 0; i < fpsCombo.Items.Count; i++)
                {
                    if (fpsCombo.Items[i] is ComboBoxItem item && item.Tag as string == _appSettings.Fps.ToString())
                    {
                        fpsCombo.SelectedIndex = i;
                        break;
                    }
                }
                fpsCombo.SelectionChanged += (s, e) =>
                {
                    OnSettingChanged();
                    SaveCurrentSettings();
                };
            }

            var qualitySlider = this.FindControl<Slider>("QualitySlider");
            if (qualitySlider != null)
            {
                UpdateQualitySliderForCodec(GetSelectedCodec());
                qualitySlider.PropertyChanged += (s, e) =>
                {
                    if (_isUpdatingQualitySlider) return;
                    if (e.Property.Name == nameof(Slider.Value))
                    {
                        OnSettingChanged();
                        SaveCurrentSettings();
                    }
                };
            }

            var bitrateCombo = this.FindControl<ComboBox>("BitrateComboBox");
            if (bitrateCombo != null)
            {
                bitrateCombo.SelectionChanged += (s, e) =>
                {
                    if (_isUpdatingQualitySlider) return;
                    if (bitrateCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag && int.TryParse(tag, out int val))
                    {
                        var slider = this.FindControl<Slider>("QualitySlider");
                        if (slider != null && (int)slider.Value != val)
                        {
                            slider.Value = val;
                        }
                        else
                        {
                            OnSettingChanged();
                            SaveCurrentSettings();
                        }
                    }
                };
            }

            InitializeResolutionOptions();

            var scaleCombo = this.FindControl<ComboBox>("ScaleComboBox");
            if (scaleCombo != null)
            {
                scaleCombo.SelectionChanged += (s, e) =>
                {
                    OnSettingChanged();
                    SaveCurrentSettings();
                };
            }

            var audioShareCb = this.FindControl<CheckBox>("AudioShareCheckBox");
            // Presets y Ajustes Avanzados
            var presetQualityBtn = this.FindControl<Button>("PresetQualityButton");
            if (presetQualityBtn != null) presetQualityBtn.Click += (s, e) => ApplyTransmissionPreset("quality");
            var presetBalancedBtn = this.FindControl<Button>("PresetBalancedButton");
            if (presetBalancedBtn != null) presetBalancedBtn.Click += (s, e) => ApplyTransmissionPreset("balanced");
            var presetSpeedBtn = this.FindControl<Button>("PresetSpeedButton");
            if (presetSpeedBtn != null) presetSpeedBtn.Click += (s, e) => ApplyTransmissionPreset("speed");
            var toggleAdvBtn = this.FindControl<Button>("ToggleAdvancedSettingsButton");
            if (toggleAdvBtn != null)
            {
                toggleAdvBtn.Click += (s, e) =>
                {
                    _appSettings.ShowAdvancedSettings = !_appSettings.ShowAdvancedSettings;
                    UpdateAdvancedSettingsVisibility();
                    SaveCurrentSettings();
                };
            }
            UpdateAdvancedSettingsVisibility();
            if (audioShareCb != null)
            {
                audioShareCb.IsChecked = _appSettings.ShareAudio;
                audioShareCb.IsCheckedChanged += async (s, e) =>
                {
                    bool isChecked = audioShareCb.IsChecked == true;
                    _appSettings.ShareAudio = isChecked;
                    UpdateCanvasFooterAudioStatus();
                    SaveCurrentSettings();

                    if (_isHosting)
                    {
                        if (isChecked)
                        {
                            try
                            {
                                await _audioServer.StartAsync(5002);
                                int targetPid = GetTargetAudioProcessId();
                                _audioCaptureService?.StartCapture(targetPid);
                                Console.WriteLine($"[Host] Captura de audio iniciada dinámicamente (PID {targetPid}).");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[Host] Error activando audio: {ex.Message}");
                            }
                        }
                        else
                        {
                            _audioCaptureService?.StopCapture();
                            Console.WriteLine("[Host] Captura de audio detenida dinámicamente.");
                        }
                    }
                };
            }
            UpdateCanvasFooterAudioStatus();

            // Temporizador de métricas (1 segundo)
            _metricsTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _metricsTimer.Tick += MetricsTimer_Tick;
            _metricsTimer.Start();

            // Indicador de permisos de Administrador para control de juegos/launchers
            UpdateAdminStatusUI();

            this.Closing += MainWindow_Closing;

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
                    // Mantener visible mientras el cursor estÃ© interactuando con la barra
                    _headerAutoHideTimer.Stop();
                    _headerAutoHideTimer.Start();
                }
                else if (pos.Y > headerHeight + 25)
                {
                    // Si el cursor baja a la zona de transmisiÃ³n, ocultar la barra
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
            _isFullscreen = fullscreen;
            var sidebar = this.FindControl<Border>("SidebarBorder");
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

        private void ToggleMute()
        {
            if (_audioPlayerService != null)
            {
                _audioPlayerService.IsMuted = !_audioPlayerService.IsMuted;
                if (!_audioPlayerService.IsMuted && _audioPlayerService.Volume <= 0.01f)
                {
                    _audioPlayerService.Volume = 0.5f;
                }
                UpdateAudioUI();
                SaveCurrentSettings();
            }
        }

        private void SetAudioVolume(float vol)
        {
            if (_audioPlayerService != null)
            {
                _audioPlayerService.Volume = vol;
                if (vol <= 0.01f)
                {
                    _audioPlayerService.IsMuted = true;
                }
                else if (_audioPlayerService.IsMuted)
                {
                    _audioPlayerService.IsMuted = false;
                }
                UpdateAudioUI();
                SaveCurrentSettings();
            }
        }

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

        private void ToggleMicMute()
        {
            if (_micAudioPlayerService != null)
            {
                _micAudioPlayerService.IsMuted = !_micAudioPlayerService.IsMuted;
                if (!_micAudioPlayerService.IsMuted && _micAudioPlayerService.Volume <= 0.01f)
                {
                    _micAudioPlayerService.Volume = 0.5f;
                }
                UpdateMicUI();
                SaveCurrentSettings();
            }
        }

        private void SetMicVolume(float vol)
        {
            if (_micAudioPlayerService != null)
            {
                _micAudioPlayerService.Volume = vol;
                if (vol <= 0.01f)
                {
                    _micAudioPlayerService.IsMuted = true;
                }
                else if (_micAudioPlayerService.IsMuted)
                {
                    _micAudioPlayerService.IsMuted = false;
                }
                UpdateMicUI();
                SaveCurrentSettings();
            }
        }

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
            // La autorización interactiva y verificación SAS se ejecuta en el canal de aplicación en TcpStreamServer.
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
                if (subtitle != null) subtitle.Text = "Compara este cÃ³digo fuera de banda antes de aprobar:";
                if (sasText != null)
                {
                    sasText.Text = $"CÃ³digo SAS: {sasFormatted}";
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

        private async Task<bool> RequestStreamApprovalAsync(string clientId)
        {
            _streamApprovalTcs?.TrySetResult(false);
            var tcs = new TaskCompletionSource<bool>();
            _streamApprovalTcs = tcs;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var banner = this.FindControl<Border>("StreamApprovalBanner");
                var subtitle = this.FindControl<TextBlock>("StreamApprovalSubtitle");
                if (subtitle != null) subtitle.Text = $"El dispositivo '{clientId}' solicita ver tu pantalla.";
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
                    var banner = this.FindControl<Border>("StreamApprovalBanner");
                    if (banner != null) banner.IsVisible = false;
                });
            }
        }

        private async Task<bool> RequestControlApprovalAsync(string clientId)
        {
            _controlApprovalTcs?.TrySetResult(false);
            var tcs = new TaskCompletionSource<bool>();
            _controlApprovalTcs = tcs;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var banner = this.FindControl<Border>("ControlApprovalBanner");
                var subtitle = this.FindControl<TextBlock>("ControlApprovalSubtitle");
                if (subtitle != null) subtitle.Text = $"El dispositivo '{clientId}' solicita controlar tu mouse y teclado.";
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
                    var banner = this.FindControl<Border>("ControlApprovalBanner");
                    if (banner != null) banner.IsVisible = false;
                });
            }
        }

        private async Task<bool> EnsureControlConnectedAsync()
        {
            if (_controlClient.IsConnected)
                return true;

            if (string.IsNullOrEmpty(_currentConnectedDeviceIp))
                return false;

            UpdateStatus("Solicitando acceso de control remoto al anfitriÃ³n...", "#F59E0B");

            try
            {
                var handshake = (_streamClient as TcpStreamClient)?.ActiveSessionHandshake;
                await _controlClient.ConnectAsync(_currentConnectedDeviceIp, 5003, _sslClientOptions, handshake?.SessionId, handshake?.ControlToken);
                Console.WriteLine($"[ControlViewer] Conectado a canal de control remoto en {_currentConnectedDeviceIp}:5003");
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
                    bannerText.Text = "Modo Juego 3D Activo â€” Presiona Ctrl + Alt + L para liberar el mouse";
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
                    bannerText.Text = "Mouse Liberado â€” Presiona Ctrl + Alt + L para recapturar";
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

        private void ScreenImage_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!_remoteControlEnabled || !_controlClient.IsConnected)
                return;

            if (IsPointerOverFullscreenHeader(e))
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

            if (IsPointerOverFullscreenHeader(e))
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

            // 1. Resolución nativa como primera opción
            items.Add(new ComboBoxItem
            {
                Content = $"{nativeW} × {nativeH} · Nativa",
                Tag = $"{nativeW}x{nativeH}"
            });

            // 2. Si es configuración multi-pantalla extendida (aspecto muy ancho > 2.2)
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
                            Content = $"{scaledW} × {scaledH} · {rLabel}",
                            Tag = $"{scaledW}x{scaledH}"
                        });
                    }
                }
            }
            else
            {
                // Filtrar resoluciones menores compatibles con la relación de aspecto
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
                            Content = $"{w} × {h} · {label}",
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
            if (!OperatingSystem.IsWindows())
            {
                return new WindowsScreenCaptureService();
            }

            if (source.Type == CaptureSourceType.Window && source.Handle != IntPtr.Zero)
            {
                return new WindowCaptureService(source.Handle);
            }

            if (source.Type == CaptureSourceType.AllScreens || engine == "gdi")
            {
                var gdi = new WindowsScreenCaptureService();
                if (source.Type == CaptureSourceType.AllScreens)
                {
                    gdi.SetTargetBounds(0, 0, 0, 0, allScreens: true);
                }
                else
                {
                    gdi.SetTargetBounds(source.X, source.Y, source.Width, source.Height, allScreens: false);
                }
                return gdi;
            }

            var dxgi = new DxgiScreenCaptureService
            {
                MonitorIndex = source.MonitorIndex,
                DeviceName = source.DeviceName,
                TargetX = source.X,
                TargetY = source.Y,
                TargetSourceWidth = source.Width,
                TargetSourceHeight = source.Height
            };

            dxgi.CaptureFailed += (s, ex) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_isHosting && _screenCaptureService is DxgiScreenCaptureService)
                    {
                        UpdateStatus($"DXGI no disponible en esta pantalla ({ex.Message}), cambiando a GDI...", "#F59E0B");
                        SwitchCaptureSource(_currentSource, "gdi", GetSelectedFps(), _appSettings.Quality, 1.0, GetSelectedCodec(), 0, 0, _appSettings.BitrateMbps);
                    }
                });
            };

            return dxgi;
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

                SwitchCaptureSource(source, engine, fps, quality, scale, codec, targetW, targetH, bitrateMbps);

                bool shareAudio = this.FindControl<CheckBox>("AudioShareCheckBox")?.IsChecked ?? true;
                if (shareAudio && _audioCaptureService != null)
                {
                    int targetPid = GetTargetAudioProcessId();
                    _audioCaptureService.StartCapture(targetPid);
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
                    CaptureSourceType.Window => "Aplicación",
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
                micText.Text = shareMic ? "Micrófono activado" : "Micrófono desactivado";
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

        private void SwitchCaptureSource(CaptureSource source, string engine, int fps, int quality, double scale, string codec, int targetW = 0, int targetH = 0, int bitrateMbps = 8)
        {
            _screenCaptureService.StopCapture();
            _screenCaptureService.FrameCaptured -= ScreenCaptureService_FrameCaptured;
            _screenCaptureService.Dispose();

            _screenCaptureService = CreateCaptureService(source, engine);
            _screenCaptureService.FrameCaptured += ScreenCaptureService_FrameCaptured;
            _screenCaptureService.StartCapture(fps, quality, scale, codec, targetW, targetH, bitrateMbps);
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

                SwitchCaptureSource(_currentSource, engine, fps, quality, scale, codec, targetW, targetH, bitrateMbps);
                string engineName = engine == "dxgi" ? "DirectX DXGI (GPU)" : "GDI BitBlt (CPU)";
                UpdateStatus($"Cambiado a motor {engineName} ({codec.ToUpper()}) en caliente", "#22C55E");
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

            var qualityTitle = this.FindControl<TextBlock>("QualityTitleLabel");
            if (qualityTitle != null)
            {
                qualityTitle.Text = codec == "h264" ? "Tasa de bits" : "Calidad JPEG";
            }

            int quality = codec == "h264" ? _appSettings.Quality : sliderVal;
            int bitrateMbps = codec == "h264" ? sliderVal : _appSettings.BitrateMbps;

            var scaleLabel = this.FindControl<TextBlock>("ScaleValueLabel");
            if (scaleLabel != null)
            {
                if (targetW > 0 && targetH > 0)
                {
                    scaleLabel.Text = $"{targetW} × {targetH}";
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
                _appSettings.AllowRemoteControl = this.FindControl<CheckBox>("AllowRemoteControlCheckBox")?.IsChecked ?? true;

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
                Console.WriteLine($"[Settings] Error al guardar configuraciÃ³n actual: {ex.Message}");
            }
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

                    string fpsInfo = _isHosting
                        ? $"Host: {frames} FPS"
                        : (_lastHostFps > 0
                            ? $"Host: {_lastHostFps} FPS ({_lastHostFrameTimeMs} ms) â€¢ Red: {frames} FPS"
                            : $"{frames} FPS");

                    string res = _lastFrameWidth > 0 ? $" â€¢ {_lastFrameWidth}x{_lastFrameHeight}" : "";
                    string engineTag = _isHosting
                        ? (_screenCaptureService is DxgiScreenCaptureService ? " â€¢ DXGI GPU" : " â€¢ GDI CPU")
                        : "";

                    string codecTag = _isHosting
                        ? $" â€¢ {_screenCaptureService.Codec.ToUpper()}"
                        : "";

                    string audioTag = (_isHosting && _audioServer.IsRunning) || (_streamClient.IsConnected && _audioClient.IsConnected)
                        ? " â€¢ Audio"
                        : "";

                    string controlTag = _controlClient.IsConnected && _remoteControlEnabled
                        ? " â€¢ Control Activo"
                        : "";

                    statsText.Text = $"{fpsInfo} â€¢ {transferRate}{res}{engineTag}{codecTag}{audioTag}{controlTag}";

                    if (_isPopoutActive && _popoutWindow != null)
                    {
                        _popoutWindow.UpdateMetrics(statsText.Text);
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
            _lastHostFps = 0;
            _lastHostFrameTimeMs = 0;

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
            string label = isHosting ? "Detener transmisión" : "Transmitir mi pantalla";

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
                if (badgeText != null) badgeText.Text = "Sin transmisión";
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
                        await HandleViewerDisconnectedAsync("Iniciando transmisión local...");
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
                    SwitchCaptureSource(_currentSource, engine, fps, quality, scale, codec, targetW, targetH, bitrateMbps);
                    _inputSimulatorService?.SetTargetSource(_currentSource);

                    // 2. Iniciar servidor y captura de audio (Puerto 5002) si está habilitado
                    bool shareAudio = this.FindControl<CheckBox>("AudioShareCheckBox")?.IsChecked ?? true;
                    bool shareMic = this.FindControl<CheckBox>("MicShareCheckBox")?.IsChecked ?? false;
                    if (shareAudio || shareMic)
                    {
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
                    }

                    // 3. Iniciar servidor de control remoto (Puerto 5003) si está habilitado
                    if (_allowRemoteControl)
                    {
                        await _controlServer.StartAsync(5003);
                    }

                    _discoveryService.StartHostingBroadcast();

                    ResetMetrics();

                    var placeholderTitle = this.FindControl<TextBlock>("PlaceholderTitleText");
                    var placeholderSubtitle = this.FindControl<TextBlock>("PlaceholderSubtitleText");
                    if (placeholderTitle != null) placeholderTitle.Text = "Transmitiendo tu pantalla";
                    if (placeholderSubtitle != null) placeholderSubtitle.Text = "Tu pantalla está siendo compartida en tu red local.\nLos equipos conectados la verán en tiempo real.";

                    UpdateHostButtonsUI(true);
                    UpdateStreamStateBadge();

                    string engineName = engine == "dxgi" ? "DirectX DXGI" : "GDI BitBlt";
                    UpdateStatus($"Transmitiendo {_currentSource.Name} ({engineName}) · Esperando visores", "#22C55E");
                }
                catch (Exception ex)
                {
                    _isHosting = false;
                    UpdateStatus($"Error al iniciar transmisión: {ex.Message}", "#EF4444");
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

                _discoveryService.StopHosting();

                _isHosting = false;
                ResetMetrics();

                var placeholderTitle = this.FindControl<TextBlock>("PlaceholderTitleText");
                var placeholderSubtitle = this.FindControl<TextBlock>("PlaceholderSubtitleText");
                if (placeholderTitle != null) placeholderTitle.Text = "Todo listo para compartir";
                if (placeholderSubtitle != null) placeholderSubtitle.Text = "Comparte una pantalla o una aplicación con otros equipos.\nTú eliges qué mostrar y quién puede controlarlo.";

                UpdateHostButtonsUI(false);
                UpdateStreamStateBadge();

                UpdateStatus("Sin conexión activa", "#34D399");
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
                UpdateStatus($"Transmitiendo pantalla · {count} espectador(es) conectado(s)", "#22C55E");
            });
        }

        private void StreamServer_ClientDisconnected(object? sender, string clientId)
        {
            Dispatcher.UIThread.Post(() =>
            {
                int count = _streamServer.ConnectedClientsCount;
                if (_isHosting)
                {
                    UpdateStatus($"Transmitiendo pantalla · {count} espectador(es) conectado(s)", "#22C55E");
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
                    ? $"Listo para transmitir o recibir · {Devices.Count} detectado(s)"
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
            if (placeholderTitle != null) placeholderTitle.Text = "Conectando al anfitrión...";
            if (placeholderSubtitle != null) placeholderSubtitle.Text = $"Estableciendo conexión segura con {device.Name} ({device.IPAddress})...";
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

                var handshake = (_streamClient as TcpStreamClient)?.ActiveSessionHandshake;

                // 2. Conectar flujo de audio en paralelo
                try
                {
                    await _audioClient.ConnectAsync(device.IPAddress, 5002, _sslClientOptions, handshake?.SessionId, handshake?.AudioToken);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AudioClient] Audio no disponible en el host: {ex.Message}");
                }

                // El control remoto NO se conecta automáticamente aquí para no solicitar permisos antes de tiempo.
                // Se conecta únicamente a demanda cuando el visor hace clic en 'Control' o 'Modo 3D'.

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

                var remoteControlBtn = this.FindControl<ToggleButton>("RemoteControlButton");
                if (remoteControlBtn != null)
                {
                    remoteControlBtn.IsVisible = _currentDeviceAllowsRemoteControl;
                    remoteControlBtn.IsChecked = false;
                    remoteControlBtn.IsEnabled = true;
                    ToolTip.SetTip(remoteControlBtn, "Habilitar interacción con mouse y teclado");
                }

                var gameModeBtn = this.FindControl<ToggleButton>("GameModeButton");
                if (gameModeBtn != null)
                {
                    gameModeBtn.IsVisible = _currentDeviceAllowsRemoteControl;
                    gameModeBtn.IsChecked = false;
                    gameModeBtn.IsEnabled = true;
                }
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

                                if (_isPopoutActive && _popoutWindow != null && _viewerWriteableBitmap != null)
                                {
                                    _popoutWindow.UpdateFrame(_viewerWriteableBitmap);
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

                    if (_isPopoutActive && _popoutWindow != null)
                    {
                        _popoutWindow.UpdateFrame(bitmap);
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
                if (status.StartsWith("Conectando", StringComparison.OrdinalIgnoreCase))
                {
                    UpdateStatus(status, "#F59E0B");
                    return;
                }

                if (!_streamClient.IsConnected)
                {
                    // Si éramos visores activos y se perdió o finalizó la transmisión
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
            });
        }

        private async void DisconnectButton_Click(object? sender, RoutedEventArgs? e = null)
        {
            await HandleViewerDisconnectedAsync("Te has desconectado de la transmisión.");
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
                if (placeholderTitle != null) placeholderTitle.Text = "Transmisión Finalizada";
                if (placeholderSubtitle != null) placeholderSubtitle.Text = "El anfitrión ha finalizado la transmisión.\nPuedes iniciar tu propia transmisión o conectarte a otro equipo.";
                UpdateStatus("El anfitrión ha finalizado la transmisión", "#F59E0B");
            }
            else if (reason.Contains("perdida", StringComparison.OrdinalIgnoreCase) ||
                     reason.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                     reason.Contains("No se pudo", StringComparison.OrdinalIgnoreCase))
            {
                if (placeholderTitle != null) placeholderTitle.Text = "Conexión Perdida";
                if (placeholderSubtitle != null) placeholderSubtitle.Text = reason;
                UpdateStatus(reason, "#EF4444");
            }
            else
            {
                if (placeholderTitle != null) placeholderTitle.Text = "Todo listo para compartir";
                if (placeholderSubtitle != null) placeholderSubtitle.Text = "Comparte una pantalla o una aplicación con otros equipos.\nTú eliges qué mostrar y quién puede controlarlo.";
                UpdateStatus("Sin conexión activa", "#34D399");
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
                if (_currentSource.ProcessId > 0)
                    return _currentSource.ProcessId;

                if (_currentSource.Handle != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(_currentSource.Handle, out uint pid);
                    if (pid > 0)
                    {
                        _currentSource.ProcessId = (int)pid;
                        return (int)pid;
                    }
                }
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

        private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
        {
            SaveCurrentSettings();
            ClosePopoutWindow();
            SetFullscreen(false);
            StopDeviceSearch();
            _headerAutoHideTimer.Stop();
            _metricsTimer.Stop();

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

            _discoveryService.StopHosting();
            _discoveryService.StopListening();
        }

        #endregion
    }
}
