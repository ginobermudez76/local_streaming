using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using LanScreenShare.Core.Interfaces;

namespace LanScreenShare.Desktop
{
    public partial class MainWindow
    {
        #region Display, Fullscreen & Sidebars

        private void MainWindow_GlobalPointerMoved(object? sender, PointerEventArgs e)
        {
            if (!_isFullscreen || _isGameMode)
                return;

            var headerBar = this.FindControl<Border>("StreamHeaderBar");
            if (headerBar == null)
                return;

            var pos = e.GetPosition(this);
            UpdateFullscreenHeaderVisibility(headerBar, pos.Y);
        }

        private void UpdateFullscreenHeaderVisibility(Border headerBar, double posY)
        {
            if (posY <= 20)
            {
                if (!headerBar.IsVisible) headerBar.IsVisible = true;
                _headerAutoHideTimer.Stop();
                _headerAutoHideTimer.Start();
                return;
            }

            if (!headerBar.IsVisible)
                return;

            double headerHeight = headerBar.Bounds.Height > 0 ? headerBar.Bounds.Height : 50;
            if (posY <= headerHeight)
            {
                _headerAutoHideTimer.Stop();
                _headerAutoHideTimer.Start();
            }
            else if (posY > headerHeight + 25)
            {
                headerBar.IsVisible = false;
                _headerAutoHideTimer.Stop();
            }
        }

        private void SetLeftSidebarCollapsed(bool collapsed)
        {
            SetElementVisibility("LeftSidebarExpandedPanel", !collapsed);
            SetElementVisibility("LeftSidebarCollapsedPanel", collapsed);
        }

        private void SetRightSidebarCollapsed(bool collapsed)
        {
            SetElementVisibility("RightSidebarExpandedPanel", !collapsed);
            SetElementVisibility("RightSidebarCollapsedPanel", collapsed);
        }

        private void ToggleFullscreen() => SetFullscreen(!_isFullscreen);

        private void SetFullscreen(bool fullscreen)
        {
            if (_isFullscreen == fullscreen)
                return;

            _isFullscreen = fullscreen;
            if (_isFullscreen)
            {
                ApplyFullscreenUi();
            }
            else
            {
                ApplyNormalWindowUi();
            }
        }

        private void ApplyFullscreenUi()
        {
            _previousWindowState = this.WindowState;
            this.WindowState = WindowState.FullScreen;

            SetElementVisibility("SidebarBorder", false);
            SetElementVisibility("RightSidebarBorder", false);
            SetElementVisibility("ExitFullscreenFloatButton", true);
            SetElementVisibility("WorkspaceHeaderGrid", false);
            SetElementVisibility("DevicesSectionBorder", false);
            SetElementVisibility("BottomFooterGrid", false);
            SetElementVisibility("CanvasCardHeader", false);
            SetElementVisibility("CanvasCardFooter", false);

            UpdateFullscreenButtonVisuals("IconFullscreenExit", "Salir");
            ConfigureWorkspaceContainer(new Thickness(0), new CornerRadius(0), new Thickness(0));
            ConfigureFullscreenHeaderBar(true);
        }

        private void ApplyNormalWindowUi()
        {
            this.WindowState = _previousWindowState == WindowState.FullScreen ? WindowState.Normal : _previousWindowState;

            SetElementVisibility("SidebarBorder", true);
            SetElementVisibility("RightSidebarBorder", true);
            SetElementVisibility("ExitFullscreenFloatButton", false);
            SetElementVisibility("WorkspaceHeaderGrid", true);
            SetElementVisibility("DevicesSectionBorder", true);
            SetElementVisibility("BottomFooterGrid", true);
            SetElementVisibility("CanvasCardHeader", true);
            SetElementVisibility("CanvasCardFooter", true);

            UpdateFullscreenButtonVisuals("IconFullscreen", "Pantalla Completa");
            ConfigureWorkspaceContainer(new Thickness(24, 18, 24, 12), new CornerRadius(14), new Thickness(1));
            ConfigureFullscreenHeaderBar(false);
        }

        private void SetElementVisibility(string name, bool isVisible)
        {
            var control = this.FindControl<Control>(name);
            if (control != null) control.IsVisible = isVisible;
        }

        private void UpdateFullscreenButtonVisuals(string iconResourceKey, string buttonText)
        {
            var fullscreenIcon = this.FindControl<PathIcon>("FullscreenButtonIcon");
            if (fullscreenIcon != null)
            {
                fullscreenIcon.Data = (this.FindResource(iconResourceKey) as StreamGeometry)!;
            }

            var fullscreenText = this.FindControl<TextBlock>("FullscreenButtonText");
            if (fullscreenText != null)
            {
                fullscreenText.Text = buttonText;
            }
        }

        private void ConfigureWorkspaceContainer(Thickness margin, CornerRadius cornerRadius, Thickness borderThickness)
        {
            var workspaceContainer = this.FindControl<Grid>("WorkspaceContainerGrid");
            if (workspaceContainer != null) workspaceContainer.Margin = margin;

            var canvasCardBorder = this.FindControl<Border>("CanvasCardBorder");
            if (canvasCardBorder != null)
            {
                canvasCardBorder.CornerRadius = cornerRadius;
                canvasCardBorder.BorderThickness = borderThickness;
            }
        }

        private void ConfigureFullscreenHeaderBar(bool isFullscreen)
        {
            var headerBar = this.FindControl<Border>("StreamHeaderBar");
            if (headerBar == null) return;

            if (isFullscreen)
            {
                Grid.SetRow(headerBar, 0);
                Grid.SetRowSpan(headerBar, 2);
                headerBar.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
                headerBar.ZIndex = 100;
                headerBar.Background = new SolidColorBrush(Color.Parse("#F211161F"));
                headerBar.IsVisible = false;
            }
            else
            {
                Grid.SetRow(headerBar, 0);
                Grid.SetRowSpan(headerBar, 1);
                headerBar.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
                headerBar.ZIndex = 0;
                headerBar.Background = new SolidColorBrush(Color.Parse("#11161F"));
                headerBar.IsVisible = true;
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

        #endregion

        #region Audio Controls

        private static void ToggleAudioMuteHelper(IAudioPlayerService? service, Action updateUi, Action saveSettings)
        {
            if (service == null) return;
            service.IsMuted = !service.IsMuted;
            if (!service.IsMuted && service.Volume <= 0.01f)
            {
                service.Volume = 0.5f;
            }
            updateUi();
            saveSettings();
        }

        private static void SetAudioVolumeHelper(IAudioPlayerService? service, float vol, Action updateUi, Action saveSettings)
        {
            if (service == null) return;
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

        private void ToggleMute() => ToggleAudioMuteHelper(_audioPlayerService, UpdateAudioUI, SaveCurrentSettings);
        private void SetAudioVolume(float vol) => SetAudioVolumeHelper(_audioPlayerService, vol, UpdateAudioUI, SaveCurrentSettings);

        private void ToggleMicMute() => ToggleAudioMuteHelper(_micAudioPlayerService, UpdateMicUI, SaveCurrentSettings);
        private void SetMicVolume(float vol) => SetAudioVolumeHelper(_micAudioPlayerService, vol, UpdateMicUI, SaveCurrentSettings);

        private readonly record struct AudioControlConfig(
            string MuteIconName,
            string OffResource,
            string OnResource,
            string SliderName,
            string TextName,
            string? ActiveColorHex = null);

        private void UpdateAudioControlsUi(IAudioPlayerService? service, in AudioControlConfig cfg)
        {
            if (service == null) return;

            bool isMuted = service.IsMuted || service.Volume <= 0.01f;
            int percent = (int)Math.Round(service.Volume * 100);

            UpdateMuteIcon(cfg, isMuted);
            UpdateVolumeSlider(cfg.SliderName, percent);
            UpdateVolumeText(cfg.TextName, percent);
        }

        private void UpdateMuteIcon(in AudioControlConfig cfg, bool isMuted)
        {
            var muteIcon = this.FindControl<PathIcon>(cfg.MuteIconName);
            if (muteIcon == null) return;

            string res = isMuted ? cfg.OffResource : cfg.OnResource;
            muteIcon.Data = (this.FindResource(res) as StreamGeometry)!;
            if (cfg.ActiveColorHex != null)
            {
                muteIcon.Foreground = new SolidColorBrush(isMuted ? Color.Parse("#EF4444") : Color.Parse(cfg.ActiveColorHex));
            }
        }

        private void UpdateVolumeSlider(string sliderName, int percent)
        {
            var volumeSlider = this.FindControl<Slider>(sliderName);
            if (volumeSlider != null && Math.Abs(volumeSlider.Value - percent) > 1)
            {
                volumeSlider.Value = percent;
            }
        }

        private void UpdateVolumeText(string textName, int percent)
        {
            var volumeText = this.FindControl<TextBlock>(textName);
            if (volumeText != null)
            {
                volumeText.Text = $"{percent}%";
            }
        }

        private static readonly AudioControlConfig SystemAudioConfig = new(
            "AudioMuteIcon", "IconVolumeOff", "IconVolumeUp", "AudioVolumeSlider", "AudioVolumeText");

        private static readonly AudioControlConfig MicAudioConfig = new(
            "MicMuteIcon", "IconMicOff", "IconMic", "MicVolumeSlider", "MicVolumeText", "#10B981");

        private void UpdateAudioUI()
        {
            UpdateAudioControlsUi(_audioPlayerService, SystemAudioConfig);
            if (_popoutWindow != null && _audioPlayerService != null)
            {
                _popoutWindow.SetVolume(_audioPlayerService.Volume, _audioPlayerService.IsMuted);
            }
        }

        private void UpdateMicUI()
        {
            UpdateAudioControlsUi(_micAudioPlayerService, MicAudioConfig);
        }

        private void UpdateViewerVoiceChatUI(bool active)
        {
            var icon = this.FindControl<PathIcon>("ViewerVoiceChatIcon");
            if (icon != null)
            {
                icon.Data = (this.FindResource(active ? "IconMic" : "IconMicOff") as StreamGeometry)!;
                icon.Foreground = new SolidColorBrush(active ? Color.Parse("#22C55E") : Color.Parse("#EF4444"));
            }

            var text = this.FindControl<TextBlock>("ViewerVoiceChatText");
            if (text != null)
            {
                text.Text = active ? "Hablando" : "Silenciado";
                text.Foreground = new SolidColorBrush(active ? Color.Parse("#22C55E") : Color.Parse("#94A3B8"));
            }

            var toggle = this.FindControl<ToggleButton>("ViewerVoiceChatToggle");
            if (toggle != null)
            {
                toggle.IsChecked = active;
            }
        }

        private readonly record struct AudioIndicatorConfig(
            string TextName,
            string IconName,
            string ActiveText,
            string InactiveText);

        private static readonly AudioIndicatorConfig SystemIndicatorConfig = new(
            "CanvasAudioStatusText", "CanvasAudioStatusIcon", "Audio del sistema activado", "Audio del sistema desactivado");

        private static readonly AudioIndicatorConfig MicIndicatorConfig = new(
            "CanvasMicStatusText", "CanvasMicStatusIcon", "Micrófono activado", "Micrófono desactivado");

        private void UpdateCanvasFooterAudioStatus()
        {
            bool shareAudio = this.FindControl<CheckBox>("AudioShareCheckBox")?.IsChecked ?? true;
            bool shareMic = this.FindControl<CheckBox>("MicShareCheckBox")?.IsChecked ?? false;

            UpdateAudioIndicator(SystemIndicatorConfig, shareAudio);
            UpdateAudioIndicator(MicIndicatorConfig, shareMic);
        }

        private void UpdateAudioIndicator(in AudioIndicatorConfig cfg, bool active)
        {
            var text = this.FindControl<TextBlock>(cfg.TextName);
            if (text != null)
            {
                text.Text = active ? cfg.ActiveText : cfg.InactiveText;
                text.Foreground = new SolidColorBrush(Color.Parse(active ? "#94A3B8" : "#64748B"));
            }

            var icon = this.FindControl<PathIcon>(cfg.IconName);
            if (icon != null)
            {
                icon.Foreground = new SolidColorBrush(Color.Parse(active ? "#22C55E" : "#64748B"));
            }
        }

        #endregion

        #region Popout Window

        private void OpenPopoutWindow()
        {
            if (_isPopoutActive) return;

            if (_isFullscreen)
            {
                SetFullscreen(false);
            }

            _isPopoutActive = true;
            TogglePopoutPanelVisibility(true);

            _popoutWindow = CreateConfiguredPopoutWindow();
            WirePopoutEvents(_popoutWindow);
            _popoutWindow.Show();
        }

        private void TogglePopoutPanelVisibility(bool isPopout)
        {
            var screenImage = this.FindControl<Image>("ScreenImage");
            var popoutPanel = this.FindControl<StackPanel>("PopoutActivePanel");
            if (screenImage != null) screenImage.IsVisible = !isPopout;
            if (popoutPanel != null) popoutPanel.IsVisible = isPopout;
        }

        private StreamPopoutWindow CreateConfiguredPopoutWindow()
        {
            var popout = new StreamPopoutWindow
            {
                Title = $"Visor Remoto - {_currentConnectedDeviceName ?? "LanScreenShare"}"
            };

            popout.SetRemoteControlAllowed(_currentDeviceAllowsRemoteControl);
            popout.SetRemoteControlState(_remoteControlEnabled);
            popout.SetGameModeState(_isGameMode);
            popout.SetActiveMonitorLabel(_currentRemoteMonitorLabel);
            popout.SetVolume(_audioPlayerService?.Volume ?? 1.0f, _audioPlayerService?.IsMuted ?? false);
            popout.SetMicVolume(_micAudioPlayerService?.Volume ?? 1.0f, _micAudioPlayerService?.IsMuted ?? false);
            popout.SetStretchMode(_currentStretchMode);

            var screenImage = this.FindControl<Image>("ScreenImage");
            if (screenImage?.Source != null)
            {
                popout.UpdateFrame(screenImage.Source);
            }

            return popout;
        }

        private void WirePopoutEvents(StreamPopoutWindow popout)
        {
            popout.ReattachRequested += ClosePopoutWindow;
            popout.DisconnectRequested += () => DisconnectButton_Click(null, null);
            popout.RemoteControlToggled += HandlePopoutRemoteControlToggled;
            popout.GameModeToggled += () =>
            {
                ToggleGameMode();
                _popoutWindow?.SetGameModeState(_isGameMode);
            };
            popout.SwitchMonitorRequested += async () => await OpenRemoteSourceMenuAsync(_popoutWindow?.GetSwitchMonitorButton());
            popout.CycleMonitorRequested += async () => await RequestRemoteMonitorSwitchAsync();
            popout.SendFileRequested += async () => await PickAndSendFilesAsync();
            popout.FilesDropped += async (paths) =>
            {
                foreach (var p in paths) await SendFileToPeerAsync(p);
            };
            popout.MuteToggled += ToggleMute;
            popout.VolumeChanged += SetAudioVolume;
            popout.StretchModeChanged += HandlePopoutStretchModeChanged;
            popout.RemoteInputSent += async (input) =>
            {
                if (_controlClient.IsConnected && _remoteControlEnabled)
                {
                    await _controlClient.SendInputAsync(input);
                }
            };
        }

        private async void HandlePopoutRemoteControlToggled(bool enabled)
        {
            if (enabled)
            {
                await EnablePopoutRemoteControlAsync();
            }
            else
            {
                DisablePopoutRemoteControl();
            }
        }

        private async Task EnablePopoutRemoteControlAsync()
        {
            var remoteControlBtn = this.FindControl<ToggleButton>("RemoteControlButton");
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

        private void DisablePopoutRemoteControl()
        {
            var remoteControlBtn = this.FindControl<ToggleButton>("RemoteControlButton");
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

        private void HandlePopoutStretchModeChanged(Stretch stretch)
        {
            _currentStretchMode = stretch;
            var screenImage = this.FindControl<Image>("ScreenImage");
            if (screenImage != null) screenImage.Stretch = stretch;
            var stretchBtnText = this.FindControl<TextBlock>("StretchModeButtonText");
            if (stretchBtnText != null) stretchBtnText.Text = stretch == Stretch.Uniform ? "Ajustar" : "Llenar";
            SaveCurrentSettings();
        }

        private void ClosePopoutWindow()
        {
            if (!_isPopoutActive) return;

            _isPopoutActive = false;
            TogglePopoutPanelVisibility(false);

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

        #endregion
    }
}
