using System;
using System.IO;
using System.Linq;
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
        private void InitializeCaptureAndDiscovery()
        {
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

            _screenCaptureService = CreateCaptureService(_currentSource, _appSettings.Engine);
            _screenCaptureService.FrameCaptured += ScreenCaptureService_FrameCaptured;

            if (OperatingSystem.IsWindows())
            {
                _inputSimulatorService = new WindowsInputSimulatorService();
            }
            else
            {
                _inputSimulatorService = new NullInputSimulatorService();
            }
        }

        private void InitializeSecuritySubsystem()
        {
            _identityKeyStorage = IdentityKeyStorageFactory.Create();
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

        private void InitializeAudioSubsystem()
        {
            _audioServer = new TcpAudioServer(_sessionAuthority, _sslServerOptions);
            _audioClient = new TcpAudioClient();

            _audioCaptureService = AudioServiceFactory.CreateAudioCapture();
            _audioPlayerService = AudioServiceFactory.CreateAudioPlayer();
            _micCaptureService = AudioServiceFactory.CreateMicCapture();
            _micAudioPlayerService = AudioServiceFactory.CreateAudioPlayer();

            _audioCaptureService.AudioCaptured += async (s, chunk) =>
            {
                if (_isHosting && _audioServer.IsRunning)
                {
                    try
                    {
                        await _audioServer.BroadcastAudioAsync(chunk);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Host] Error emitiendo audio: {ex.Message}");
                    }
                }
            };

            _micCaptureService.AudioCaptured += async (s, chunk) =>
            {
                if (_isHosting && _audioServer.IsRunning)
                {
                    try
                    {
                        await _audioServer.BroadcastAudioAsync(chunk);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Host] Error emitiendo micrófono: {ex.Message}");
                    }
                }
                else if (CanSendViewerVoiceChat())
                {
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
            };

            _audioServer.ClientAudioReceived += (s, chunk) =>
            {
                if (_allowViewerVoiceChat && _micAudioPlayerService != null)
                {
                    _micAudioPlayerService.PlayChunk(chunk);
                }
            };

            _audioClient.AudioReceived += (s, chunk) =>
            {
                if (chunk.Source == AudioSourceType.Microphone || chunk.Source == AudioSourceType.ViewerMicrophone)
                {
                    _micAudioPlayerService?.PlayChunk(chunk);
                    return;
                }

                var syncAction = _avSyncController.ProcessAudioChunk(chunk.Timestamp, out int delayMs);
                if (syncAction == AvSyncAction.DropStaleAudio)
                {
                    return;
                }

                _streamClient?.UpdateAvDrift(_avSyncController.CurrentDriftMs);
                _audioPlayerService?.PlayChunk(chunk);
            };
        }

        private bool CanHostSyncClipboard() => _isHosting && _allowRemoteControl && _allowClipboardSync;
        private bool CanViewerSyncClipboard() => _isViewing && _remoteControlEnabled && _allowClipboardSync;

        private void InitializeControlSubsystem()
        {
            _controlServer = new TcpControlServer(_sessionAuthority, _inputSimulatorService, _sslServerOptions);
            _controlClient = new TcpControlClient();

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

            var clipboardSyncCb = this.FindControl<CheckBox>("ClipboardSyncCheckBox");
            if (clipboardSyncCb != null)
            {
                clipboardSyncCb.IsChecked = _appSettings.ClipboardSyncEnabled;
                _allowClipboardSync = _appSettings.ClipboardSyncEnabled;
                clipboardSyncCb.IsCheckedChanged += (s, e) =>
                {
                    bool enabled = clipboardSyncCb.IsChecked == true;
                    _allowClipboardSync = enabled;
                    _appSettings.ClipboardSyncEnabled = enabled;
                    SaveCurrentSettings();
                };
            }

            _controlServer.ClipboardTextReceived += (s, text) =>
            {
                if (CanHostSyncClipboard())
                {
                    ApplyRemoteClipboardText(text);
                }
            };

            _controlClient.ClipboardTextReceived += (s, text) =>
            {
                if (CanViewerSyncClipboard())
                {
                    ApplyRemoteClipboardText(text);
                }
            };

            _controlServer.ClientConnected += (s, clientId) =>
            {
                if (_isHosting && _allowRemoteControl)
                {
                    BroadcastHostSourceCatalog();
                }
            };

            _controlServer.SourceCatalogRequested += (s, e) =>
            {
                if (_isHosting && _allowRemoteControl)
                {
                    BroadcastHostSourceCatalog();
                }
            };

            _controlServer.SwitchMonitorRequested += (s, requestedIndex) =>
            {
                if (_isHosting && _allowRemoteControl)
                {
                    HandleRemoteMonitorSwitchOnHost(requestedIndex);
                }
            };

            _controlServer.SelectSourceRequested += (s, sourceId) =>
            {
                if (_isHosting && _allowRemoteControl)
                {
                    HandleRemoteSelectSourceOnHost(sourceId);
                }
            };

            _controlClient.MonitorInfoReceived += (s, monitorLabel) =>
            {
                if (_isViewing)
                {
                    ApplyRemoteMonitorInfo(monitorLabel);
                }
            };
        }

        private void InitializeFileTransferSubsystem()
        {
            _fileTransferServer = new TcpFileTransferServer(_sessionAuthority, _sslServerOptions);
            _fileTransferClient = new TcpFileTransferClient();

            _fileTransferServer.TransferProgress += (s, p) => Dispatcher.UIThread.Post(() => ShowFileTransferProgress(p));
            _fileTransferServer.TransferCompleted += (s, c) => Dispatcher.UIThread.Post(() => ShowFileTransferCompleted(c));
            _fileTransferServer.TransferError += (s, err) => Dispatcher.UIThread.Post(() => ShowFileTransferError(err));

            _fileTransferClient.TransferProgress += (s, p) => Dispatcher.UIThread.Post(() => ShowFileTransferProgress(p));
            _fileTransferClient.TransferCompleted += (s, c) => Dispatcher.UIThread.Post(() => ShowFileTransferCompleted(c));
            _fileTransferClient.TransferError += (s, err) => Dispatcher.UIThread.Post(() => ShowFileTransferError(err));

            var sendFileBtn = this.FindControl<Button>("SendFileButton");
            if (sendFileBtn != null)
            {
                sendFileBtn.Click += async (s, e) => await PickAndSendFilesAsync();
            }

            var cancelTransferBtn = this.FindControl<Button>("FileTransferCancelButton");
            if (cancelTransferBtn != null)
            {
                cancelTransferBtn.Click += (s, e) => CancelActiveFileTransfer();
            }

            var openFolderBtn = this.FindControl<Button>("FileTransferOpenFolderButton");
            if (openFolderBtn != null)
            {
                openFolderBtn.Click += (s, e) => OpenDownloadsFolder();
            }

            var dismissBannerBtn = this.FindControl<Button>("FileTransferDismissButton");
            if (dismissBannerBtn != null)
            {
                dismissBannerBtn.Click += (s, e) => HideFileTransferBanner();
            }

            this.AddHandler(DragDrop.DropEvent, MainWindow_Drop);
            this.AddHandler(DragDrop.DragOverEvent, MainWindow_DragOver);
        }

        private void InitializeDisplayAndWindowControls()
        {
            var devicesList = this.FindControl<ListBox>("DevicesList");
            if (devicesList != null)
            {
                devicesList.ItemsSource = Devices;
            }
            Devices.CollectionChanged += (s, e) => UpdateDevicesSectionUI();
            UpdateDevicesSectionUI();

            var hostButton = this.FindControl<Button>("HostButton");
            if (hostButton != null) hostButton.Click += HostButton_Click;

            var centerHostButton = this.FindControl<Button>("CenterHostButton");
            if (centerHostButton != null) centerHostButton.Click += HostButton_Click;

            var centerSelectSourceBtn = this.FindControl<Button>("CenterSelectSourceButton");
            if (centerSelectSourceBtn != null) centerSelectSourceBtn.Click += SelectSourceButton_Click;

            var helpButton = this.FindControl<Button>("HelpButton");
            if (helpButton != null)
            {
                helpButton.Click += (s, e) =>
                    UpdateStatus("Ayuda: Presiona 'Transmitir mi pantalla' o 'Buscar dispositivos' en tu red.", "#3B82F6");
            }

            var searchButton = this.FindControl<Button>("SearchButton");
            if (searchButton != null) searchButton.Click += SearchButton_Click;

            var collapseLeftBtn = this.FindControl<Button>("CollapseLeftSidebarButton");
            if (collapseLeftBtn != null) collapseLeftBtn.Click += (s, e) => SetLeftSidebarCollapsed(true);

            var expandLeftBtn = this.FindControl<Button>("ExpandLeftSidebarButton");
            if (expandLeftBtn != null) expandLeftBtn.Click += (s, e) => SetLeftSidebarCollapsed(false);

            var collapseRightBtn = this.FindControl<Button>("CollapseRightSidebarButton");
            if (collapseRightBtn != null) collapseRightBtn.Click += (s, e) => SetRightSidebarCollapsed(true);

            var expandRightBtn = this.FindControl<Button>("ExpandRightSidebarButton");
            if (expandRightBtn != null) expandRightBtn.Click += (s, e) => SetRightSidebarCollapsed(false);

            var disconnectButton = this.FindControl<Button>("DisconnectButton");
            if (disconnectButton != null) disconnectButton.Click += DisconnectButton_Click;

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

            var switchMonitorBtn = this.FindControl<Button>("SwitchMonitorButton");
            if (switchMonitorBtn != null)
            {
                switchMonitorBtn.Click += async (s, e) => await OpenRemoteSourceMenuAsync(switchMonitorBtn);
            }

            this.KeyDown += MainWindow_KeyDown;

            _headerAutoHideTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2.5)
            };
            _headerAutoHideTimer.Tick += (s, e) =>
            {
                if (_isFullscreen && !_isMouseOverHeaderBar)
                {
                    var header = this.FindControl<Border>("StreamHeaderBar");
                    if (header != null) header.IsVisible = false;
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
            if (fullscreenBtn != null) fullscreenBtn.Click += (s, e) => ToggleFullscreen();

            _currentStretchMode = _appSettings.StretchMode == "Fill" ? Stretch.Fill : Stretch.Uniform;
            var stretchBtn = this.FindControl<Button>("StretchModeButton");
            if (stretchBtn != null) stretchBtn.Click += (s, e) => ToggleStretchMode();

            var initialScreenImage = this.FindControl<Image>("ScreenImage");
            if (initialScreenImage != null) initialScreenImage.Stretch = _currentStretchMode;

            var initialStretchBtnText = this.FindControl<TextBlock>("StretchModeButtonText");
            if (initialStretchBtnText != null)
            {
                initialStretchBtnText.Text = _currentStretchMode == Stretch.Uniform ? "Ajustar" : "Llenar";
            }

            var popoutBtn = this.FindControl<Button>("PopoutWindowButton");
            if (popoutBtn != null) popoutBtn.Click += (s, e) => OpenPopoutWindow();

            var reattachBtn = this.FindControl<Button>("ReattachStreamButton");
            if (reattachBtn != null) reattachBtn.Click += (s, e) => ClosePopoutWindow();

            var exitFloatBtn = this.FindControl<Button>("ExitFullscreenFloatButton");
            if (exitFloatBtn != null) exitFloatBtn.Click += (s, e) => SetFullscreen(false);

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

        private void InitializeAudioControls()
        {
            var audioMuteButton = this.FindControl<Button>("AudioMuteButton");
            if (audioMuteButton != null) audioMuteButton.Click += (s, e) => ToggleMute();

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
            if (micMuteButton != null) micMuteButton.Click += (s, e) => ToggleMicMute();

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
                    if (!allowed)
                    {
                        _micAudioPlayerService?.ClearBuffer();
                    }
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

                    if (_isViewing && _audioClient.IsConnected)
                    {
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
                };
            }

            var audioShareCb = this.FindControl<CheckBox>("AudioShareCheckBox");
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
        }

        private void InitializeAccessAndApproval()
        {
            var streamAccessCombo = this.FindControl<ComboBox>("StreamAccessModeComboBox");
            if (streamAccessCombo != null)
            {
                SelectComboBoxItemByTag(streamAccessCombo, _appSettings.StreamAccessMode);
                _requireStreamApproval = (streamAccessCombo.SelectedItem as ComboBoxItem)?.Tag as string == "ask";
                streamAccessCombo.SelectionChanged += (s, e) =>
                {
                    _requireStreamApproval = (streamAccessCombo.SelectedItem as ComboBoxItem)?.Tag as string == "ask";
                    Console.WriteLine($"[AccessHost] Aprobación de stream requerida: {_requireStreamApproval}");
                    SaveCurrentSettings();
                };
            }

            var controlAccessCombo = this.FindControl<ComboBox>("ControlAccessModeComboBox");
            if (controlAccessCombo != null)
            {
                SelectComboBoxItemByTag(controlAccessCombo, _appSettings.ControlAccessMode);
                _remoteControlMode = (controlAccessCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
                controlAccessCombo.SelectionChanged += (s, e) =>
                {
                    _remoteControlMode = (controlAccessCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
                    Console.WriteLine($"[AccessHost] Modo de control remoto: {_remoteControlMode}");
                    SaveCurrentSettings();
                };
            }

            _streamServer.ClientApprovalCallback = async (clientId) =>
            {
                if (!_requireStreamApproval) return true;
                return await RequestStreamApprovalAsync(clientId);
            };

            var approveStreamBtn = this.FindControl<Button>("ApproveStreamButton");
            if (approveStreamBtn != null) approveStreamBtn.Click += (s, e) => _streamApprovalTcs?.TrySetResult(true);

            var rejectStreamBtn = this.FindControl<Button>("RejectStreamButton");
            if (rejectStreamBtn != null) rejectStreamBtn.Click += (s, e) => _streamApprovalTcs?.TrySetResult(false);

            _controlServer.ClientApprovalCallback = async (clientId) =>
            {
                if (!_allowRemoteControl) return false;
                if (_remoteControlMode == "auto") return true;
                return await RequestControlApprovalAsync(clientId);
            };

            var approveControlBtn = this.FindControl<Button>("ApproveControlButton");
            if (approveControlBtn != null) approveControlBtn.Click += (s, e) => _controlApprovalTcs?.TrySetResult(true);

            var rejectControlBtn = this.FindControl<Button>("RejectControlButton");
            if (rejectControlBtn != null) rejectControlBtn.Click += (s, e) => _controlApprovalTcs?.TrySetResult(false);
        }

        private static bool TryGetSelectedItemTagAsInt(ComboBox? combo, out int value)
        {
            if (combo?.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                return int.TryParse(tag, out value);
            }
            value = 0;
            return false;
        }

        private void InitializeEncoderAndSettingsControls()
        {
            var selectSourceBtn = this.FindControl<Button>("SelectSourceButton");
            if (selectSourceBtn != null) selectSourceBtn.Click += SelectSourceButton_Click;
            UpdateSourceUI();

            var engineCombo = this.FindControl<ComboBox>("EngineComboBox");
            if (engineCombo != null)
            {
                SelectComboBoxItemByTag(engineCombo, _appSettings.Engine);
                engineCombo.SelectionChanged += (s, e) =>
                {
                    OnEngineChanged();
                    SaveCurrentSettings();
                };
            }

            var codecCombo = this.FindControl<ComboBox>("CodecComboBox");
            if (codecCombo != null)
            {
                SelectComboBoxItemByTag(codecCombo, _appSettings.Codec);
                codecCombo.SelectionChanged += (s, e) =>
                {
                    string selCodec = GetSelectedCodec();
                    UpdateQualitySliderForCodec(selCodec);
                    UpdateHardwareEncoderBadgeUI();
                    OnSettingChanged();
                    SaveCurrentSettings();
                };
                UpdateHardwareEncoderBadgeUI();
            }

            var fpsCombo = this.FindControl<ComboBox>("FpsComboBox");
            if (fpsCombo != null)
            {
                SelectComboBoxItemByTag(fpsCombo, _appSettings.Fps.ToString());
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
                    if (TryGetSelectedItemTagAsInt(bitrateCombo, out int val))
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

            var abrCb = this.FindControl<CheckBox>("AbrCheckBox");
            if (abrCb != null)
            {
                abrCb.IsChecked = _appSettings.EnableAdaptiveBitrate;
                _abrController.IsEnabled = _appSettings.EnableAdaptiveBitrate;
                abrCb.IsCheckedChanged += (s, e) =>
                {
                    bool isChecked = abrCb.IsChecked ?? true;
                    _appSettings.EnableAdaptiveBitrate = isChecked;
                    _abrController.IsEnabled = isChecked;
                    if (!isChecked && _screenCaptureService != null)
                    {
                        var slider = this.FindControl<Slider>("QualitySlider");
                        int manualVal = slider != null ? (int)slider.Value : _appSettings.BitrateMbps;
                        _screenCaptureService.BitrateMbps = manualVal;
                        _screenCaptureService.TargetFps = GetSelectedFps();
                    }
                    UpdateAbrUI();
                    SaveCurrentSettings();
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
