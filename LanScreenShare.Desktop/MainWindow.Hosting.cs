using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using LanScreenShare.Core.Models;
using LanScreenShare.Media;

namespace LanScreenShare.Desktop
{
    public partial class MainWindow
    {
        #region Hosting Logic

        private bool CanHostHandleRemoteSourceCommands =>
            OperatingSystem.IsWindows() && _isHosting && _allowRemoteControl;

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

        private readonly record struct StreamBadgeVisuals(string Text, string ColorHex);

        private void UpdateStreamStateBadge()
        {
            var (text, colorHex) = _isHosting
                ? new StreamBadgeVisuals("Transmitiendo", "#22C55E")
                : _isViewing
                    ? new StreamBadgeVisuals("Visualizando", "#3B82F6")
                    : new StreamBadgeVisuals("Sin transmisión", "#7C8B9E");

            var badgeText = this.FindControl<TextBlock>("StreamStateBadgeText");
            if (badgeText != null) badgeText.Text = text;

            var badgeDot = this.FindControl<Ellipse>("StreamStateDot");
            if (badgeDot != null) badgeDot.Fill = new SolidColorBrush(Color.Parse(colorHex));
        }

        private async void HostButton_Click(object? sender, RoutedEventArgs e)
        {
            if (!_isHosting)
            {
                await StartHostingAsync();
            }
            else
            {
                StopHosting();
            }
        }

        private CaptureProfile PrepareHostingCaptureProfile(out string engine)
        {
            engine = GetSelectedEngine();
            return BuildCurrentCaptureProfile();
        }

        private void InitializeAdaptiveBitrate(int bitrateMbps, int fps)
        {
            _abrController.Reset(initialBitrateMbps: bitrateMbps, baseFps: fps);
            _abrController.MaxBitrateMbps = Math.Max(bitrateMbps, _appSettings.MaxAdaptiveBitrateMbps);
            _abrController.MinBitrateMbps = _appSettings.MinAdaptiveBitrateMbps;
            _abrController.IsEnabled = _appSettings.EnableAdaptiveBitrate;
            UpdateAbrUI();
        }

        private async Task StartAuxiliaryHostingServersAsync()
        {
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

            if (_allowRemoteControl)
            {
                await _controlServer.StartAsync(5003);
            }

            try
            {
                await _fileTransferServer.StartAsync(5004);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FileTransfer] Advertencia iniciando servidor de archivos: {ex.Message}");
            }
        }

        private void SetPlaceholderHostingText()
        {
            var placeholderTitle = this.FindControl<TextBlock>("PlaceholderTitleText");
            var placeholderSubtitle = this.FindControl<TextBlock>("PlaceholderSubtitleText");
            if (placeholderTitle != null) placeholderTitle.Text = "Transmitiendo tu pantalla";
            if (placeholderSubtitle != null) placeholderSubtitle.Text = "Tu pantalla está siendo compartida en tu red local.\nLos equipos conectados la verán en tiempo real.";
        }

        private void SetPlaceholderIdleText()
        {
            var placeholderTitle = this.FindControl<TextBlock>("PlaceholderTitleText");
            var placeholderSubtitle = this.FindControl<TextBlock>("PlaceholderSubtitleText");
            if (placeholderTitle != null) placeholderTitle.Text = "Todo listo para compartir";
            if (placeholderSubtitle != null) placeholderSubtitle.Text = "Comparte una pantalla o una aplicación con otros equipos.\nTú eliges qué mostrar y quién puede controlarlo.";
        }

        private async Task StartHostingAsync()
        {
            try
            {
                if (_isViewing)
                {
                    await HandleViewerDisconnectedAsync("Iniciando transmisión local...");
                }

                _isHosting = true;
                _isViewing = false;

                var profile = PrepareHostingCaptureProfile(out string engine);
                await _streamServer.StartAsync(5001);
                InitializeAdaptiveBitrate(profile.BitrateMbps, profile.Fps);

                SwitchCaptureSource(_currentSource, engine, profile);
                _inputSimulatorService?.SetTargetSource(_currentSource);

                await StartAuxiliaryHostingServersAsync();
                _discoveryService.StartHostingBroadcast();

                ResetMetrics();
                SetPlaceholderHostingText();
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

        private void StopHosting()
        {
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

            SetPlaceholderIdleText();
            UpdateHostButtonsUI(false);
            UpdateStreamStateBadge();

            UpdateStatus("Sin conexión activa", "#34D399");
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
                UpdateStatus($"Transmitiendo pantalla · {count} espectador(es) conectado(s)", "#22C55E");
            });
        }

        private void StreamServer_ClientDisconnected(object? sender, string clientId)
        {
            Dispatcher.UIThread.Post(() =>
            {
                int count = _streamServer.ConnectedClientsCount;
                if (!_isHosting)
                    return;

                var sendFileBtn = this.FindControl<Button>("SendFileButton");
                if (sendFileBtn != null)
                {
                    sendFileBtn.IsVisible = count > 0;
                }

                if (count > 0)
                {
                    UpdateStatus($"Transmitiendo pantalla · {count} espectador(es) conectado(s)", "#22C55E");
                }
                else
                {
                    UpdateStatus("Transmitiendo pantalla · Esperando visores", "#22C55E");
                }
            });
        }

        private void BroadcastHostSourceCatalog()
        {
            if (!OperatingSystem.IsWindows())
                return;

            Dispatcher.UIThread.Post(() =>
            {
                if (!CanHostHandleRemoteSourceCommands)
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
                    Console.WriteLine($"[ControlHost] Error enviando catálogo de fuentes: {ex.Message}");
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
                if (!CanHostHandleRemoteSourceCommands)
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
                if (!CanHostHandleRemoteSourceCommands)
                    return;

                var rawSources = CaptureSourceDiscoveryService.GetScreenSources();
                if (rawSources.Count == 0)
                    return;

                var candidateSources = rawSources
                    .Where(s => s.Type == CaptureSourceType.Screen)
                    .Concat(rawSources.Where(s => s.Type == CaptureSourceType.AllScreens))
                    .ToList();

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

                int nextIndex = (requestedIndex >= 0 && requestedIndex < candidateSources.Count)
                    ? requestedIndex
                    : GetNextSourceIndex(candidateSources);

                var targetSource = candidateSources[nextIndex];
                SetCaptureSource(targetSource);
                Console.WriteLine($"[ControlHost] Fuente alternada remotamente a: {targetSource.Name} ({targetSource.Id})");
            });
        }

        private int GetNextSourceIndex(List<CaptureSource> candidateSources)
        {
            int currentIdx = candidateSources.FindIndex(s => s.Id == _currentSource.Id);
            return currentIdx >= 0 ? (currentIdx + 1) % candidateSources.Count : 0;
        }

        #endregion
    }
}
