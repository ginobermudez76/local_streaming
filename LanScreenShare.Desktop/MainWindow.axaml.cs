using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
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
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;
using LanScreenShare.Core.Security;
using LanScreenShare.Core.Services;
using LanScreenShare.Media;
using LanScreenShare.Network;
using LanScreenShare.Network.Security;
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
                OnSettingChanged();
            }

            _isInitializingSettings = false;
        }


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

        private void WireButtonClick(string name, EventHandler<RoutedEventArgs> handler)
        {
            var btn = this.FindControl<Button>(name);
            if (btn != null) btn.Click += handler;
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

        #endregion
    }
}
