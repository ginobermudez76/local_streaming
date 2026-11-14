using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
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
    public partial class MainWindow
    {
        private static CaptureSource CreateDefaultScreenSource() =>
            new CaptureSource { Id = "screen:0", Name = "Pantalla Principal", Type = CaptureSourceType.Screen, IsPrimary = true };

        private static CaptureSource ResolveInitialCaptureSource(string? sourceId)
        {
            if (!OperatingSystem.IsWindows())
            {
                return CreateDefaultScreenSource();
            }

            var availableScreens = CaptureSourceDiscoveryService.GetScreenSources();
            if (!string.IsNullOrEmpty(sourceId) && !sourceId.StartsWith("window:"))
            {
                var matched = availableScreens.FirstOrDefault(s => s.Id == sourceId);
                if (matched != null) return matched;
            }

            if (availableScreens.Count > 0)
            {
                return availableScreens.FirstOrDefault(s => s.IsPrimary) ?? availableScreens[0];
            }

            return CreateDefaultScreenSource();
        }

        private static MediaFoundationH264DecoderService? TryCreateH264Decoder()
        {
            if (!OperatingSystem.IsWindows()) return null;
            try
            {
                return new MediaFoundationH264DecoderService();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MainWindow] Decodificador H.264 no disponible: {ex.Message}");
                return null;
            }
        }

        private static IInputSimulatorService CreateInputSimulator() =>
            OperatingSystem.IsWindows()
                ? new WindowsInputSimulatorService()
                : new NullInputSimulatorService();

        private void InitializeCaptureAndDiscovery()
        {
            _currentSource = ResolveInitialCaptureSource(_appSettings.CaptureSourceId);
            _h264Decoder = TryCreateH264Decoder();

            _discoveryService = new DiscoveryService();
            _discoveryService.OnDeviceDiscovered += DiscoveryService_OnDeviceDiscovered;

            _screenCaptureService = CreateCaptureService(_currentSource, _appSettings.Engine);
            _screenCaptureService.FrameCaptured += ScreenCaptureService_FrameCaptured;
            _screenCaptureService.SecureDesktopChanged += ScreenCaptureService_SecureDesktopChanged;
            _inputSimulatorService = CreateInputSimulator();
        }

        private bool ShouldApplyAbrBitrate(int newBitrate) =>
            _isHosting && _screenCaptureService != null && _screenCaptureService.BitrateMbps != newBitrate;

        private bool ShouldApplyAbrFps(int newFps) =>
            _isHosting && _screenCaptureService != null && _screenCaptureService.TargetFps != newFps;

        private void InitializeStreamingSubsystem()
        {
            _streamServer = new TcpStreamServer(_sessionAuthority, _sslServerOptions, _pairedDeviceStore);
            _streamServer.PairingApprovalCallback = (fingerprint, deviceName, sasFormatted) => RequestPairingApprovalAsync(deviceName, fingerprint, sasFormatted);
            _streamServer.ClientConnected += StreamServer_ClientConnected;
            _streamServer.ClientDisconnected += StreamServer_ClientDisconnected;
            _streamServer.FrameDropped += (s, e) =>
            {
                _screenCaptureService?.RequestKeyframe();
                _abrController.RegisterHostDrop();
            };
            _streamServer.TelemetryUpdated += (s, telemetry) =>
            {
                _abrController.RegisterTelemetry(telemetry);
            };

            _abrController.BitrateChanged += (newBitrate, reason) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (ShouldApplyAbrBitrate(newBitrate))
                    {
                        _screenCaptureService!.BitrateMbps = newBitrate;
                    }
                    UpdateAbrUI();
                });
            };

            _abrController.FpsChanged += (newFps, reason) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (ShouldApplyAbrFps(newFps))
                    {
                        _screenCaptureService!.TargetFps = newFps;
                    }
                });
            };

            _abrController.KeyframeRequested += () =>
            {
                _screenCaptureService?.RequestKeyframe();
            };

            _streamClient = new TcpStreamClient();
            _streamClient.FrameReceived += StreamClient_FrameReceived;
            _streamClient.StatusChanged += StreamClient_StatusChanged;
        }

        private bool CanSendViewerVoiceChat() =>
            _isViewing && _audioClient.IsConnected && _isViewerVoiceChatActive;

        private async Task BroadcastCapturedAudioAsync(CapturedAudioChunk chunk, string label)
        {
            if (_isHosting && _audioServer.IsRunning)
            {
                try
                {
                    await _audioServer.BroadcastAudioAsync(chunk);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Host] Error emitiendo {label}: {ex.Message}");
                }
            }
        }

        private async Task SendViewerMicVoiceAsync(CapturedAudioChunk chunk)
        {
            if (!CanSendViewerVoiceChat()) return;
            try
            {
                chunk.Source = AudioSourceType.ViewerMicrophone;
                await _audioClient.SendAudioChunkAsync(chunk);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Viewer] Error enviando voz del espectador: {ex.Message}");
            }
        }

        private void RouteReceivedClientAudio(CapturedAudioChunk chunk)
        {
            if (chunk.Source == AudioSourceType.Microphone || chunk.Source == AudioSourceType.ViewerMicrophone)
            {
                _micAudioPlayerService?.PlayChunk(chunk);
                return;
            }

            if (_avSyncController.ProcessAudioChunk(chunk.Timestamp, out _) == AvSyncAction.DropStaleAudio)
            {
                return;
            }

            _streamClient?.UpdateAvDrift(_avSyncController.CurrentDriftMs);
            _audioPlayerService?.PlayChunk(chunk);
        }

        private void InitializeAudioSubsystem()
        {
            _audioServer = new TcpAudioServer(_sessionAuthority, _sslServerOptions);
            _audioClient = new TcpAudioClient();

            _audioCaptureService = AudioServiceFactory.CreateAudioCapture();
            _audioPlayerService = AudioServiceFactory.CreateAudioPlayer();
            _micCaptureService = AudioServiceFactory.CreateMicCapture();
            _micAudioPlayerService = AudioServiceFactory.CreateAudioPlayer();

            _audioCaptureService.AudioCaptured += async (_, chunk) => await BroadcastCapturedAudioAsync(chunk, "audio");
            _micCaptureService.AudioCaptured += async (_, chunk) =>
            {
                if (_isHosting && _audioServer.IsRunning)
                {
                    await BroadcastCapturedAudioAsync(chunk, "micrófono");
                }
                else
                {
                    await SendViewerMicVoiceAsync(chunk);
                }
            };

            _audioServer.ClientAudioReceived += (_, chunk) =>
            {
                if (_allowViewerVoiceChat) _micAudioPlayerService?.PlayChunk(chunk);
            };

            _audioClient.AudioReceived += (_, chunk) => RouteReceivedClientAudio(chunk);
        }

        private void InitializeNavigationAndActionButtons()
        {
            WireButtonClick("HostButton", HostButton_Click);
            WireButtonClick("CenterHostButton", HostButton_Click);
            WireButtonClick("CenterSelectSourceButton", SelectSourceButton_Click);
            WireButtonClick("SearchButton", SearchButton_Click);
            WireButtonClick("DisconnectButton", DisconnectButton_Click);

            WireButtonClick("HelpButton", (s, e) =>
                UpdateStatus("Ayuda: Presiona 'Transmitir mi pantalla' o 'Buscar dispositivos' en tu red.", "#3B82F6"));

            var switchMonitorBtn = this.FindControl<Button>("SwitchMonitorButton");
            if (switchMonitorBtn != null)
            {
                switchMonitorBtn.Click += async (s, e) => await OpenRemoteSourceMenuAsync(switchMonitorBtn);
            }
        }

        private void InitializeSidebarToggleButtons()
        {
            WireButtonClick("CollapseLeftSidebarButton", (s, e) => SetLeftSidebarCollapsed(true));
            WireButtonClick("ExpandLeftSidebarButton", (s, e) => SetLeftSidebarCollapsed(false));
            WireButtonClick("CollapseRightSidebarButton", (s, e) => SetRightSidebarCollapsed(true));
            WireButtonClick("ExpandRightSidebarButton", (s, e) => SetRightSidebarCollapsed(false));
        }

        private async Task HandleRemoteControlToggleAsync(ToggleButton remoteControlBtn)
        {
            if (remoteControlBtn.IsChecked == true)
            {
                await EnableRemoteControlFromUiAsync(remoteControlBtn);
            }
            else
            {
                DisableRemoteControlFromUi(remoteControlBtn);
            }
        }

        private async Task EnableRemoteControlFromUiAsync(ToggleButton remoteControlBtn)
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
            this.FindControl<Image>("ScreenImage")?.Focus();
        }

        private void DisableRemoteControlFromUi(ToggleButton remoteControlBtn)
        {
            _remoteControlEnabled = false;
            remoteControlBtn.Background = new SolidColorBrush(Color.Parse("#27272A"));
            if (_isGameMode)
            {
                _isGameMode = false;
                UpdateGameModeUI();
            }
        }

        private void InitializeViewerToggleButtons()
        {
            var remoteControlBtn = this.FindControl<ToggleButton>("RemoteControlButton");
            if (remoteControlBtn != null)
            {
                remoteControlBtn.IsCheckedChanged += async (s, e) => await HandleRemoteControlToggleAsync(remoteControlBtn);
            }

            var gameModeBtn = this.FindControl<ToggleButton>("GameModeButton");
            if (gameModeBtn != null)
            {
                gameModeBtn.IsCheckedChanged += (s, e) =>
                {
                    if (_isGameMode != (gameModeBtn.IsChecked == true)) ToggleGameMode();
                };
            }
        }

        private void InitializeOverlaysAutoHide()
        {
            _headerAutoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _headerAutoHideTimer.Tick += OnHeaderAutoHideTick;
            WireHeaderBarPointerEvents();

            _footerAutoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            _footerAutoHideTimer.Tick += OnFooterAutoHideTick;
            WireFooterBarPointerEvents();
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
            if (!_isFullscreen || _isMouseOverFooterBar) return;
            var footer = this.FindControl<Border>("CanvasCardFooter");
            if (footer != null) footer.IsVisible = false;
            _footerAutoHideTimer.Stop();
        }

        private void WireHeaderBarPointerEvents()
        {
            var headerBar = this.FindControl<Border>("StreamHeaderBar");
            if (headerBar == null) return;

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

        private void WireFooterBarPointerEvents()
        {
            var footerBar = this.FindControl<Border>("CanvasCardFooter");
            if (footerBar == null) return;

            footerBar.PointerEntered += (s, e) =>
            {
                _isMouseOverFooterBar = true;
                _footerAutoHideTimer.Stop();
            };
            footerBar.PointerExited += (s, e) =>
            {
                _isMouseOverFooterBar = false;
                if (_isFullscreen)
                {
                    _footerAutoHideTimer.Stop();
                    _footerAutoHideTimer.Start();
                }
            };
        }

        private void InitializeFullscreenAndPopoutControls()
        {
            InitializeOverlaysAutoHide();
            this.AddHandler(InputElement.PointerMovedEvent, MainWindow_GlobalPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

            WireButtonClick("FullscreenButton", (s, e) => ToggleFullscreen());
            WireButtonClick("ExitFullscreenFloatButton", (s, e) => SetFullscreen(false));
            WireButtonClick("PopoutWindowButton", (s, e) => OpenPopoutWindow());
            WireButtonClick("ReattachStreamButton", (s, e) => ClosePopoutWindow());

            _currentStretchMode = _appSettings.StretchMode == "Fill" ? Stretch.Fill : Stretch.Uniform;
            WireButtonClick("StretchModeButton", (s, e) => ToggleStretchMode());

            var initialScreenImage = this.FindControl<Image>("ScreenImage");
            if (initialScreenImage != null) initialScreenImage.Stretch = _currentStretchMode;

            var initialStretchBtnText = this.FindControl<TextBlock>("StretchModeButtonText");
            if (initialStretchBtnText != null)
            {
                initialStretchBtnText.Text = _currentStretchMode == Stretch.Uniform ? "Ajustar" : "Llenar";
            }
        }

        private void InitializeScreenImageEvents()
        {
            var screenImage = this.FindControl<Image>("ScreenImage");
            if (screenImage != null)
            {
                screenImage.DoubleTapped += (s, e) =>
                {
                    if (_remoteControlEnabled || _isGameMode) return;
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

        private void InitializeDisplayAndWindowControls()
        {
            var devicesList = this.FindControl<ListBox>("DevicesList");
            if (devicesList != null) devicesList.ItemsSource = Devices;
            Devices.CollectionChanged += (s, e) => UpdateDevicesSectionUI();
            UpdateDevicesSectionUI();

            this.KeyDown += MainWindow_KeyDown;

            InitializeNavigationAndActionButtons();
            InitializeSidebarToggleButtons();
            InitializeViewerToggleButtons();
            InitializeFullscreenAndPopoutControls();
            InitializeScreenImageEvents();
        }

        private readonly record struct VolumeControlBinding(
            string MuteButtonName,
            string SliderName,
            float InitialVolume,
            bool InitialMuted,
            IAudioPlayerService? Service,
            Action ToggleMute,
            Action<float> SetVolume,
            Action UpdateUi);

        private void ConfigureVolumeSlider(VolumeControlBinding binding)
        {
            WireButtonClick(binding.MuteButtonName, (s, e) => binding.ToggleMute());
            var slider = this.FindControl<Slider>(binding.SliderName);
            if (slider != null)
            {
                slider.Value = Math.Clamp((int)Math.Round(binding.InitialVolume * 100), 0, 100);
                slider.PropertyChanged += (s, e) =>
                {
                    if (e.Property.Name == nameof(Slider.Value))
                    {
                        binding.SetVolume((float)Math.Clamp(slider.Value / 100.0, 0.0, 1.0));
                    }
                };
            }

            if (binding.Service != null)
            {
                binding.Service.Volume = binding.InitialVolume;
                binding.Service.IsMuted = binding.InitialMuted;
                binding.UpdateUi();
            }
        }

        private async Task UpdateHostAudioCaptureStateAsync(bool isChecked, string label, Action startAction, Action stopAction)
        {
            if (!_isHosting) return;
            if (isChecked)
            {
                try
                {
                    await _audioServer.StartAsync(5002);
                    startAction();
                    Console.WriteLine($"[Host] Captura de {label} iniciada dinámicamente.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Host] Error activando {label}: {ex.Message}");
                }
            }
            else
            {
                stopAction();
                Console.WriteLine($"[Host] Captura de {label} detenida dinámicamente.");
            }
        }

        private void WireAudioShareCheckbox(string checkboxName, bool initialChecked, Action<bool> updateSetting, Func<bool, Task> updateState)
        {
            var cb = this.FindControl<CheckBox>(checkboxName);
            if (cb == null) return;

            cb.IsChecked = initialChecked;
            cb.IsCheckedChanged += async (s, e) =>
            {
                bool isChecked = cb.IsChecked == true;
                updateSetting(isChecked);
                UpdateCanvasFooterAudioStatus();
                SaveCurrentSettings();
                await updateState(isChecked);
            };
        }

        private void InitializeViewerVoiceChatToggles()
        {
            var viewerVoiceCb = this.FindControl<CheckBox>("ViewerVoiceChatCheckBox");
            if (viewerVoiceCb != null)
            {
                viewerVoiceCb.IsChecked = _appSettings.ViewerVoiceChatEnabled;
                _allowViewerVoiceChat = _appSettings.ViewerVoiceChatEnabled;
                viewerVoiceCb.IsCheckedChanged += (s, e) =>
                {
                    bool allowed = viewerVoiceCb.IsChecked == true;
                    _allowViewerVoiceChat = allowed;
                    _appSettings.ViewerVoiceChatEnabled = allowed;
                    if (!allowed) _micAudioPlayerService?.ClearBuffer();
                    SaveCurrentSettings();
                };
            }

            var viewerVoiceChatToggle = this.FindControl<ToggleButton>("ViewerVoiceChatToggle");
            if (viewerVoiceChatToggle != null)
            {
                viewerVoiceChatToggle.IsChecked = true;
                _isViewerVoiceChatActive = true;
                viewerVoiceChatToggle.IsCheckedChanged += (s, e) =>
                {
                    bool active = viewerVoiceChatToggle.IsChecked == true;
                    _isViewerVoiceChatActive = active;
                    UpdateViewerVoiceChatUI(active);
                    UpdateViewerMicCapture(active);
                };
            }
        }

        private void UpdateViewerMicCapture(bool active)
        {
            if (!_isViewing || !_audioClient.IsConnected) return;
            if (active)
            {
                _micCaptureService?.StartCapture();
                Console.WriteLine("[Viewer] Micrófono activado hacia el anfitrión.");
            }
            else
            {
                _micCaptureService?.StopCapture();
                Console.WriteLine("[Viewer] Micrófono silenciado hacia el anfitrión.");
            }
        }

        private void InitializeAudioControls()
        {
            ConfigureVolumeSlider(new VolumeControlBinding(
                "AudioMuteButton", "AudioVolumeSlider",
                _appSettings.AudioVolume, _appSettings.AudioMuted,
                _audioPlayerService, ToggleMute, SetAudioVolume, UpdateAudioUI));

            ConfigureVolumeSlider(new VolumeControlBinding(
                "MicMuteButton", "MicVolumeSlider",
                _appSettings.MicVolume, _appSettings.MicMuted,
                _micAudioPlayerService, ToggleMicMute, SetMicVolume, UpdateMicUI));

            WireAudioShareCheckbox("MicShareCheckBox", _appSettings.ShareMicrophone,
                v => _appSettings.ShareMicrophone = v,
                v => UpdateHostAudioCaptureStateAsync(v, "micrófono", () => _micCaptureService?.StartCapture(), () => _micCaptureService?.StopCapture()));

            WireAudioShareCheckbox("AudioShareCheckBox", _appSettings.ShareAudio,
                v => _appSettings.ShareAudio = v,
                v => UpdateHostAudioCaptureStateAsync(v, "audio", () => _audioCaptureService?.StartCapture(GetTargetAudioProcessId()), () => _audioCaptureService?.StopCapture()));

            InitializeViewerVoiceChatToggles();
            UpdateCanvasFooterAudioStatus();
        }

        private void InitializeTimersAndLifecycle()
        {
            _clipboardSyncTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(750)
            };
            _clipboardSyncTimer.Tick += ClipboardSyncTimer_Tick;
            _clipboardSyncTimer.Start();

            _metricsTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _metricsTimer.Tick += MetricsTimer_Tick;
            _metricsTimer.Start();

            UpdateAdminStatusUI();

            this.Closing += MainWindow_Closing;
        }
    }
}
