using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
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

        private bool _isHosting;
        private string? _currentConnectedDeviceName;

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

            if (OperatingSystem.IsWindows())
            {
                _audioCaptureService = new WasapiAudioCaptureService();
                _audioPlayerService = new WasapiAudioPlayerService();

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

            // Controles de ajuste de transmisión
            var engineCombo = this.FindControl<ComboBox>("EngineComboBox");
            if (engineCombo != null)
            {
                engineCombo.SelectionChanged += (s, e) => OnEngineChanged();
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

            this.Closing += MainWindow_Closing;
        }

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

                SwitchCaptureEngine(engine, fps, quality, scale);
                string engineName = engine == "dxgi" ? "DirectX DXGI (GPU)" : "GDI BitBlt (CPU)";
                UpdateStatus($"Cambiado a motor {engineName} en caliente", "#22C55E");
            }
        }

        private void SwitchCaptureEngine(string engine, int fps, int quality, double scale)
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
            _screenCaptureService.StartCapture(fps, quality, scale);
        }

        private void OnSettingChanged()
        {
            int fps = GetSelectedFps();
            int quality = (int)(this.FindControl<Slider>("QualitySlider")?.Value ?? 75);
            double scale = GetSelectedScale();

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

            _screenCaptureService.UpdateSettings(fps, quality, scale);
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

                    string audioTag = (_isHosting && _audioServer.IsRunning) || (_streamClient.IsConnected && _audioClient.IsConnected)
                        ? " • 🔊 Audio"
                        : "";

                    statsText.Text = $"{frames} FPS • {transferRate}{res}{engineTag}{audioTag}";
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

                    // 1. Iniciar servidor de video (Puerto 5001)
                    await _streamServer.StartAsync(5001);
                    SwitchCaptureEngine(engine, fps, quality, scale);

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

        private void SearchButton_Click(object? sender, RoutedEventArgs e)
        {
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

                    UpdateStatus($"Recibiendo pantalla de {_currentConnectedDeviceName}", "#22C55E");
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
            });
        }

        private async void DisconnectButton_Click(object? sender, RoutedEventArgs e)
        {
            await _streamClient.DisconnectAsync();
            await _audioClient.DisconnectAsync();
            _audioPlayerService?.Stop();

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

        private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
        {
            _metricsTimer.Stop();

            _screenCaptureService.Dispose();
            _streamServer.Dispose();
            _streamClient.Dispose();

            _audioCaptureService?.Dispose();
            _audioPlayerService?.Dispose();
            _audioServer.Dispose();
            _audioClient.Dispose();

            _discoveryService.StopHosting();
            _discoveryService.StopListening();
        }

        #endregion
    }
}
