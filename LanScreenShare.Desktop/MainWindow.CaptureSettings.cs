using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Media;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;
using LanScreenShare.Core.Services;
using LanScreenShare.Media;

namespace LanScreenShare.Desktop
{
    public partial class MainWindow
    {
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

        private static bool HasPositiveDimensions(CaptureSource? s) => s != null && s.Width > 0 && s.Height > 0;
        private static bool HasPositiveDimensions(int w, int h) => w > 0 && h > 0;

        private (int Width, int Height) GetNativeDimensions(CaptureSource? source)
        {
            if (HasPositiveDimensions(source))
                return (source!.Width, source.Height);

            if (OperatingSystem.IsWindows())
            {
                int sw = GetSystemMetrics(SM_CXSCREEN);
                int sh = GetSystemMetrics(SM_CYSCREEN);
                if (HasPositiveDimensions(sw, sh)) return (sw, sh);
            }
            else if (Screens?.Primary != null)
            {
                return (Screens.Primary.Bounds.Width, Screens.Primary.Bounds.Height);
            }

            return (1920, 1080);
        }

        private void InitializeResolutionOptions(CaptureSource? source = null)
        {
            source ??= _currentSource;
            var scaleCombo = this.FindControl<ComboBox>("ScaleComboBox");
            if (scaleCombo == null) return;

            var (nativeW, nativeH) = GetNativeDimensions(source);
            double nativeAspect = (double)nativeW / Math.Max(1, nativeH);

            var items = BuildResolutionComboItems(nativeW, nativeH, nativeAspect);
            scaleCombo.ItemsSource = items;

            scaleCombo.SelectedIndex = FindMatchingResolutionIndex(items, _appSettings.ResolutionTag);
            GetSelectedResolution(out int targetW, out int targetH, out double scale);
            UpdateResolutionLabels(nativeW, nativeH, targetW, targetH, scale);
        }

        private static List<ComboBoxItem> BuildResolutionComboItems(int nativeW, int nativeH, double nativeAspect)
        {
            var items = new List<ComboBoxItem>
            {
                new() { Content = $"{nativeW} × {nativeH} · Nativa", Tag = $"{nativeW}x{nativeH}" }
            };

            if (nativeAspect > 2.2)
            {
                AppendExtendedMultiMonitorResolutions(items, nativeW, nativeH);
            }
            else
            {
                AppendStandardResolutions(items, nativeW, nativeH, nativeAspect);
            }

            return items;
        }

        private static bool IsValidScaledOption(int scaledW, int scaledH, int nativeW) =>
            scaledW > 0 && scaledH > 0 && scaledW < nativeW;

        private static void AppendExtendedMultiMonitorResolutions(List<ComboBoxItem> items, int nativeW, int nativeH)
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
                if (IsValidScaledOption(scaledW, scaledH, nativeW))
                {
                    items.Add(new ComboBoxItem
                    {
                        Content = $"{scaledW} × {scaledH} · {rLabel}",
                        Tag = $"{scaledW}x{scaledH}"
                    });
                }
            }
        }

        private static readonly (int w, int h, string label)[] CommonResolutions =
        [
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
        ];

        private static void AppendStandardResolutions(List<ComboBoxItem> items, int nativeW, int nativeH, double nativeAspect)
        {
            foreach (var (w, h, label) in CommonResolutions)
            {
                if (w >= nativeW || h >= nativeH)
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

        private static int FindMatchingResolutionIndex(List<ComboBoxItem> items, string? targetTag)
        {
            if (string.IsNullOrEmpty(targetTag))
                return 0;

            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Tag as string == targetTag)
                    return i;
            }
            return 0;
        }

        private static bool TryParseResolutionTag(string tag, out int width, out int height)
        {
            width = 0;
            height = 0;
            int idx = tag.IndexOf('x');
            if (idx <= 0) return false;
            return int.TryParse(tag.AsSpan(0, idx), out width) && int.TryParse(tag.AsSpan(idx + 1), out height);
        }

        private void GetSelectedResolution(out int targetW, out int targetH, out double scale)
        {
            targetW = 0;
            targetH = 0;
            scale = 1.0;

            var combo = this.FindControl<ComboBox>("ScaleComboBox");
            if (combo?.SelectedItem is not ComboBoxItem item || item.Tag is not string tag)
                return;

            if (TryParseResolutionTag(tag, out int w, out int h))
            {
                targetW = w;
                targetH = h;
            }
            else if (double.TryParse(tag, NumberStyles.Any, CultureInfo.InvariantCulture, out double s))
            {
                scale = s;
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
                ApplyCaptureSourceToHost(source);
            }
        }

        private void ApplyCaptureSourceToHost(CaptureSource source)
        {
            var profile = BuildCurrentCaptureProfile();
            SwitchCaptureSource(source, GetSelectedEngine(), profile);
            RestartHostAudioCaptureIfEnabled();

            if (_controlServer.IsRunning && _allowRemoteControl)
            {
                BroadcastHostSourceCatalog();
            }

            UpdateStatus($"Transmitiendo: {source.Name}", "#22C55E");
        }

        private CaptureProfile BuildCurrentCaptureProfile()
        {
            string codec = GetSelectedCodec();
            int fps = GetSelectedFps();
            int sliderVal = (int)(this.FindControl<Slider>("QualitySlider")?.Value ?? (codec == "h264" ? 8 : 75));
            int quality = codec == "h264" ? _appSettings.Quality : sliderVal;
            int bitrateMbps = codec == "h264" ? sliderVal : _appSettings.BitrateMbps;
            GetSelectedResolution(out int targetW, out int targetH, out double scale);

            return new CaptureProfile(fps, quality, scale, codec, targetW, targetH, bitrateMbps);
        }

        private void RestartHostAudioCaptureIfEnabled()
        {
            bool shareAudio = this.FindControl<CheckBox>("AudioShareCheckBox")?.IsChecked ?? true;
            if (shareAudio && _audioCaptureService != null)
            {
                _audioCaptureService.StartCapture(GetTargetAudioProcessId());
            }
        }

        private void UpdateSourceUI()
        {
            UpdateSourceLabels();
            UpdateSourceBadge();
            UpdateSourceIcons();
        }

        private static string GetSourceSubText(CaptureSource source) =>
            source.Type switch
            {
                CaptureSourceType.Window => "Aplicación",
                CaptureSourceType.AllScreens => "Escritorio extendido",
                _ => source.IsPrimary ? "Principal" : "Secundaria"
            };

        private static string GetSourceCanvasTitle(CaptureSource source) =>
            source.Type == CaptureSourceType.Screen && source.IsPrimary
                ? $"{source.Name} (Principal)"
                : source.Name;

        private void UpdateSourceLabels()
        {
            var sourceNameText = this.FindControl<TextBlock>("SelectedSourceNameText");
            if (sourceNameText != null) sourceNameText.Text = _currentSource.Name;

            var sourceSubText = this.FindControl<TextBlock>("SelectedSourceSubText");
            if (sourceSubText != null)
            {
                sourceSubText.Text = GetSourceSubText(_currentSource);
            }

            if (!_isViewing)
            {
                var canvasSourceTitle = this.FindControl<TextBlock>("CanvasSourceTitleText");
                if (canvasSourceTitle != null)
                {
                    canvasSourceTitle.Text = GetSourceCanvasTitle(_currentSource);
                }
            }
        }

        private void UpdateSourceBadge()
        {
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
        }

        private void UpdateSourceIcons()
        {
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
                if (canvasIcon != null) canvasIcon.Data = geom;
            }
        }

        private bool _isUpdatingQualitySlider = false;

        private static bool HasRequiredQualityControls(Slider? slider, TextBlock? title, TextBlock? valueLabel) =>
            slider != null && title != null && valueLabel != null;

        private void UpdateQualitySliderForCodec(string codec)
        {
            var slider = this.FindControl<Slider>("QualitySlider");
            var title = this.FindControl<TextBlock>("QualityTitleLabel");
            var valueLabel = this.FindControl<TextBlock>("QualityValueLabel");
            var bitrateCombo = this.FindControl<ComboBox>("BitrateComboBox");
            if (!HasRequiredQualityControls(slider, title, valueLabel)) return;

            _isUpdatingQualitySlider = true;
            try
            {
                if (codec == "h264")
                {
                    ConfigureH264Slider(slider!, title!, valueLabel!, bitrateCombo);
                }
                else
                {
                    ConfigureJpegSlider(slider!, title!, valueLabel!, bitrateCombo);
                }
            }
            finally
            {
                _isUpdatingQualitySlider = false;
            }
        }

        private void ConfigureH264Slider(Slider slider, TextBlock title, TextBlock valueLabel, ComboBox? bitrateCombo)
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
                var (items, bestIdx) = BuildOptionsComboItems([3, 5, 8, 12, 15, 20, 25], val, " Mbps");
                bitrateCombo.ItemsSource = items;
                bitrateCombo.SelectedIndex = bestIdx;
            }
        }

        private void ConfigureJpegSlider(Slider slider, TextBlock title, TextBlock valueLabel, ComboBox? bitrateCombo)
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
                var (items, bestIdx) = BuildOptionsComboItems([40, 60, 75, 85, 95], val, "%");
                bitrateCombo.ItemsSource = items;
                bitrateCombo.SelectedIndex = bestIdx;
            }
        }

        private static (List<ComboBoxItem> Items, int BestIndex) BuildOptionsComboItems(int[] options, int targetVal, string suffix)
        {
            var items = new List<ComboBoxItem>();
            int bestIdx = 0;
            int bestDiff = int.MaxValue;
            for (int i = 0; i < options.Length; i++)
            {
                items.Add(new ComboBoxItem { Content = $"{options[i]}{suffix}", Tag = options[i].ToString() });
                int diff = Math.Abs(options[i] - targetVal);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    bestIdx = i;
                }
            }
            return (items, bestIdx);
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
            if (!_isHosting)
                return;

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

        private void UpdateAbrUI()
        {
            var qualityTitle = this.FindControl<TextBlock>("QualityTitleLabel");
            if (qualityTitle == null) return;

            string codec = GetSelectedCodec();
            if (codec == "h264")
            {
                qualityTitle.Text = _abrController.IsEnabled
                    ? (_isHosting ? $"Tasa de bits (ABR: {_abrController.CurrentBitrateMbps} Mbps)" : "Tasa de bits (ABR auto)")
                    : "Tasa de bits";
            }
            else
            {
                qualityTitle.Text = "Calidad JPEG";
            }
        }

        private (int Fps, int BitrateMbps) AdjustForAbr(int fps, int bitrateMbps, string codec)
        {
            if (!_abrController.IsEnabled || codec != "h264")
                return (fps, bitrateMbps);

            _abrController.BaseFps = fps;
            _abrController.MaxBitrateMbps = Math.Max(bitrateMbps, _appSettings.MaxAdaptiveBitrateMbps);
            return (_abrController.TargetFps, _abrController.CurrentBitrateMbps);
        }

        private void MarkCustomPresetIfUserChanged()
        {
            if (!_isApplyingPreset && !_isInitializingSettings)
            {
                _appSettings.TransmissionPreset = "custom";
                UpdatePresetButtonsHighlight("custom");
            }
        }

        private void OnSettingChanged()
        {
            int fps = GetSelectedFps();
            string codec = GetSelectedCodec();
            int sliderVal = (int)(this.FindControl<Slider>("QualitySlider")?.Value ?? (codec == "h264" ? 8 : 75));
            GetSelectedResolution(out int targetW, out int targetH, out double scale);

            UpdateFpsAndQualityLabels(fps, codec, sliderVal);

            int quality = codec == "h264" ? _appSettings.Quality : sliderVal;
            int bitrateMbps = codec == "h264" ? sliderVal : _appSettings.BitrateMbps;

            (fps, bitrateMbps) = AdjustForAbr(fps, bitrateMbps, codec);

            UpdateAbrUI();
            UpdateScaleAndResolutionLabels(targetW, targetH, scale);
            MarkCustomPresetIfUserChanged();

            _screenCaptureService.UpdateSettings(fps, quality, scale, codec, targetW, targetH, bitrateMbps);
        }

        private void UpdateFpsAndQualityLabels(int fps, string codec, int sliderVal)
        {
            var fpsLabel = this.FindControl<TextBlock>("FpsValueLabel");
            if (fpsLabel != null) fpsLabel.Text = $"{fps} fps";

            var qualityLabel = this.FindControl<TextBlock>("QualityValueLabel");
            if (qualityLabel != null) qualityLabel.Text = codec == "h264" ? $"{sliderVal} Mbps" : $"{sliderVal}%";
        }

        private void UpdateScaleAndResolutionLabels(int targetW, int targetH, double scale)
        {
            var scaleLabel = this.FindControl<TextBlock>("ScaleValueLabel");
            if (scaleLabel != null)
            {
                scaleLabel.Text = (targetW > 0 && targetH > 0)
                    ? $"{targetW} × {targetH}"
                    : $"{scale * 100:0}%";
            }

            int nativeW = (_currentSource != null && _currentSource.Width > 0) ? _currentSource.Width : 1920;
            int nativeH = (_currentSource != null && _currentSource.Height > 0) ? _currentSource.Height : 1080;
            UpdateResolutionLabels(nativeW, nativeH, targetW, targetH, scale);
        }

        private void SaveCurrentSettings()
        {
            if (_isInitializingSettings)
                return;

            try
            {
                PopulateSettingsFromControls();
                SettingsService.Save(_appSettings);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Settings] Error al guardar configuración actual: {ex.Message}");
            }
        }

        private string? GetSelectedComboTag(string controlName)
        {
            var combo = this.FindControl<ComboBox>(controlName);
            return combo?.SelectedItem is ComboBoxItem item && item.Tag is string tag ? tag : null;
        }

        private void PopulateSettingsFromControls()
        {
            _appSettings.Engine = GetSelectedEngine();
            _appSettings.Codec = GetSelectedCodec();
            _appSettings.Fps = GetSelectedFps();

            int sliderVal = (int)(this.FindControl<Slider>("QualitySlider")?.Value ?? (_appSettings.Codec == "h264" ? 8 : 75));
            if (_appSettings.Codec == "h264")
            {
                _appSettings.BitrateMbps = sliderVal;
            }
            else
            {
                _appSettings.Quality = sliderVal;
            }

            if (GetSelectedComboTag("ScaleComboBox") is { } tag)
            {
                _appSettings.ResolutionTag = tag;
            }

            PopulateTogglesAndAccessSettings();
            PopulateAudioAndDisplaySettings();
        }

        private void PopulateAudioAndDisplaySettings()
        {
            _appSettings.AudioVolume = _audioPlayerService?.Volume ?? 1.0f;
            _appSettings.AudioMuted = _audioPlayerService?.IsMuted ?? false;
            _appSettings.MicVolume = _micAudioPlayerService?.Volume ?? 1.0f;
            _appSettings.MicMuted = _micAudioPlayerService?.IsMuted ?? false;
            _appSettings.StretchMode = _currentStretchMode == Stretch.Fill ? "Fill" : "Uniform";
            _appSettings.CaptureSourceId = _currentSource.Id;
            _appSettings.CaptureSourceName = _currentSource.Name;
            _appSettings.TransmissionPreset = string.IsNullOrEmpty(_appSettings.TransmissionPreset) ? "balanced" : _appSettings.TransmissionPreset;
            _appSettings.ShowAdvancedSettings = this.FindControl<StackPanel>("AdvancedSettingsPanel")?.IsVisible ?? false;
        }

        private bool IsCheckBoxChecked(string name, bool defaultValue = true) =>
            this.FindControl<CheckBox>(name)?.IsChecked ?? defaultValue;

        private void PopulateTogglesAndAccessSettings()
        {
            _appSettings.ShareAudio = IsCheckBoxChecked("AudioShareCheckBox", true);
            _appSettings.ShareMicrophone = IsCheckBoxChecked("MicShareCheckBox", false);
            _appSettings.ViewerVoiceChatEnabled = IsCheckBoxChecked("ViewerVoiceChatCheckBox", true);
            _appSettings.AllowRemoteControl = IsCheckBoxChecked("AllowRemoteControlCheckBox", true);
            _appSettings.ClipboardSyncEnabled = IsCheckBoxChecked("ClipboardSyncCheckBox", true);
            _appSettings.EnableAdaptiveBitrate = IsCheckBoxChecked("AbrCheckBox", true);

            PopulateAccessModeSettings();
        }

        private void PopulateAccessModeSettings()
        {
            if (GetSelectedComboTag("StreamAccessModeComboBox") is { } streamTag)
            {
                _appSettings.StreamAccessMode = streamTag;
            }

            if (GetSelectedComboTag("ControlAccessModeComboBox") is { } controlTag)
            {
                _appSettings.ControlAccessMode = controlTag;
            }
        }

        #endregion

        #region Real-Time Metrics & P/Invoke

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

        private static string GetAbrStateIcon(AbrNetworkState state) =>
            state switch
            {
                AbrNetworkState.Moderate => "⚖️",
                AbrNetworkState.Congested => "⚠️",
                AbrNetworkState.SevereCongestion => "🚨",
                _ => "⚡"
            };

        private string FormatAbrTag()
        {
            if (!_isHosting || !_abrController.IsEnabled) return "";
            return $" • ABR: {_abrController.CurrentBitrateMbps} Mbps {GetAbrStateIcon(_abrController.CurrentState)}";
        }

        private bool HasActiveStreamingSession() =>
            _isHosting || (_streamClient != null && _streamClient.IsConnected);

        private bool CanUpdateCanvasSpecs() =>
            _isViewing && _lastFrameWidth > 0 && _lastFrameHeight > 0;

        private void MetricsTimer_Tick(object? sender, EventArgs e)
        {
            long frames = Interlocked.Exchange(ref _metricFramesCount, 0);
            long bytes = Interlocked.Exchange(ref _metricBytesCount, 0);

            var statsBadge = this.FindControl<Border>("StatsBadge");
            var statsText = this.FindControl<TextBlock>("StatsText");

            if (!HasActiveStreamingSession())
            {
                if (statsBadge != null) statsBadge.IsVisible = false;
                return;
            }

            if (statsBadge != null && statsText != null)
            {
                statsBadge.IsVisible = true;
                UpdateMetricsBadgeUi(statsText, frames, bytes);
            }

            UpdateCanvasSpecsUi(frames);
        }

        private string FormatEngineTag() =>
            _isHosting ? (_screenCaptureService is DxgiScreenCaptureService ? " • DXGI GPU" : " • GDI CPU") : "";

        private string FormatAudioTag()
        {
            bool hasHostAudio = _isHosting && _audioServer.IsRunning;
            bool hasClientAudio = _streamClient != null && _streamClient.IsConnected && _audioClient.IsConnected;
            return (hasHostAudio || hasClientAudio) ? " • Audio" : "";
        }

        private string FormatControlTag() =>
            _controlClient.IsConnected && _remoteControlEnabled ? " • Control Activo" : "";

        private string FormatDropsTag() =>
            (_isHosting && _streamServer.DroppedFramesCount > 0) ? $" • Descartados: {_streamServer.DroppedFramesCount}" : "";

        private string FormatAvSyncTag() =>
            (!_isHosting && _audioClient.IsConnected) ? $" • A/V: {_avSyncController.CurrentDriftMs} ms" : "";

        private void UpdateMetricsBadgeUi(TextBlock statsText, long frames, long bytes)
        {
            string transferRate = FormatTransferRate(bytes);
            GetActiveRttAndQualityColor(out int rtt, out string qualityColor);
            string rttTag = rtt > 0 ? $"RTT: {rtt} ms • " : "";

            var statsDot = this.FindControl<Ellipse>("StatsQualityDot");
            if (statsDot != null && Avalonia.Media.Color.TryParse(qualityColor, out var parsedColor))
            {
                statsDot.Fill = new SolidColorBrush(parsedColor);
            }

            string fpsInfo = FormatFpsInfo(frames);
            string res = _lastFrameWidth > 0 ? $" • {_lastFrameWidth}x{_lastFrameHeight}" : "";
            string engineTag = FormatEngineTag();
            string codecTag = FormatCodecTag();
            string audioTag = FormatAudioTag();
            string controlTag = FormatControlTag();
            string dropsTag = FormatDropsTag();
            string avSyncTag = FormatAvSyncTag();
            string abrTag = FormatAbrTag();

            statsText.Text = $"{rttTag}{fpsInfo} • {transferRate}{res}{engineTag}{codecTag}{abrTag}{audioTag}{avSyncTag}{controlTag}{dropsTag}";

            if (_isPopoutActive && _popoutWindow != null)
            {
                _popoutWindow.UpdateMetrics(statsText.Text, qualityColor);
            }
        }

        private void UpdateCanvasSpecsUi(long frames)
        {
            if (!CanUpdateCanvasSpecs()) return;

            var canvasSpecs = this.FindControl<TextBlock>("CanvasSpecsText");
            if (canvasSpecs != null)
            {
                int displayFps = frames > 0 ? (int)frames : (_lastHostFps > 0 ? _lastHostFps : 0);
                canvasSpecs.Text = $"{_lastFrameWidth} × {_lastFrameHeight} · {displayFps} fps";
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

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        #endregion
    }
}
