using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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
        private readonly IScreenCaptureService _screenCaptureService;
        private readonly IStreamServer _streamServer;
        private readonly IStreamClient _streamClient;

        private bool _isHosting;
        private string? _currentConnectedDeviceName;

        public ObservableCollection<DeviceInfo> Devices { get; } = new();

        public MainWindow()
        {
            InitializeComponent();

            _discoveryService = new DiscoveryService();
            _discoveryService.OnDeviceDiscovered += DiscoveryService_OnDeviceDiscovered;

            _screenCaptureService = new WindowsScreenCaptureService();
            _screenCaptureService.FrameCaptured += ScreenCaptureService_FrameCaptured;

            _streamServer = new TcpStreamServer();
            _streamServer.ClientConnected += StreamServer_ClientConnected;
            _streamServer.ClientDisconnected += StreamServer_ClientDisconnected;

            _streamClient = new TcpStreamClient();
            _streamClient.FrameReceived += StreamClient_FrameReceived;
            _streamClient.StatusChanged += StreamClient_StatusChanged;

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

            this.Closing += MainWindow_Closing;
        }

        #region Hosting Logic

        private async void HostButton_Click(object? sender, RoutedEventArgs e)
        {
            var hostBtn = this.FindControl<Button>("HostButton");

            if (!_isHosting)
            {
                // Iniciar Host
                try
                {
                    await _streamServer.StartAsync(5001);
                    _screenCaptureService.StartCapture(targetFps: 30, quality: 75);
                    _discoveryService.StartHostingBroadcast();

                    _isHosting = true;

                    if (hostBtn != null)
                    {
                        hostBtn.Content = "Detener Transmisión";
                        hostBtn.Background = new SolidColorBrush(Color.Parse("#DC2626"));
                    }

                    UpdateStatus("Transmitiendo pantalla propia (Puerto 5001) - Esperando espectadores", "#22C55E");
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
                _discoveryService.StopHosting();

                _isHosting = false;

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

            try
            {
                await _streamClient.ConnectAsync(device.IPAddress, 5001);

                var disconnectBtn = this.FindControl<Button>("DisconnectButton");
                if (disconnectBtn != null)
                {
                    disconnectBtn.IsVisible = true;
                }
            }
            catch (Exception ex)
            {
                UpdateStatus($"No se pudo conectar a {device.Name}: {ex.Message}", "#EF4444");
            }
        }

        private void StreamClient_FrameReceived(object? sender, CapturedFrame frame)
        {
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

                    UpdateStatus($"Recibiendo pantalla de {_currentConnectedDeviceName} ({frame.Width}x{frame.Height})", "#22C55E");
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
            });
        }

        private async void DisconnectButton_Click(object? sender, RoutedEventArgs e)
        {
            await _streamClient.DisconnectAsync();

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
            _screenCaptureService.Dispose();
            _streamServer.Dispose();
            _streamClient.Dispose();
            _discoveryService.StopHosting();
            _discoveryService.StopListening();
        }

        #endregion
    }
}