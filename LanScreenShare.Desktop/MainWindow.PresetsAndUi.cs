using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LanScreenShare.Core.Models;
using LanScreenShare.Media;

namespace LanScreenShare.Desktop
{
    public partial class MainWindow
    {
        public readonly record struct ResolutionMetrics(
            int NativeW,
            int NativeH,
            int TargetW,
            int TargetH,
            double Scale);

        private readonly record struct TransmissionPresetConfig(
            double? ApproximateScale,
            int? ScaleIndex,
            string Fps,
            string Codec,
            int Quality,
            string Bitrate);

        private readonly record struct ButtonTheme(
            IBrush Background,
            IBrush Border,
            Thickness Thickness,
            IBrush IconForeground,
            IBrush TitleForeground,
            IBrush SubForeground);

        private readonly record struct AdminStatusStyle(
            string Bg,
            string Border,
            string Text,
            string Subtext,
            string Tooltip);

        private readonly record struct BadgeTheme(
            string Bg,
            string Border,
            string Fg,
            string Text,
            string IconResource);

        private static readonly ButtonTheme ActivePresetTheme = new(
            new SolidColorBrush(Color.Parse("#192B4D")),
            new SolidColorBrush(Color.Parse("#3B82F6")),
            new Thickness(1.5),
            new SolidColorBrush(Color.Parse("#60A5FA")),
            new SolidColorBrush(Color.Parse("#F8FAFC")),
            new SolidColorBrush(Color.Parse("#93C5FD")));

        private static readonly ButtonTheme InactivePresetTheme = new(
            new SolidColorBrush(Color.Parse("#1A2230")),
            new SolidColorBrush(Color.Parse("#263246")),
            new Thickness(1),
            new SolidColorBrush(Color.Parse("#94A3B8")),
            new SolidColorBrush(Color.Parse("#E2E8F0")),
            new SolidColorBrush(Color.Parse("#7C8B9E")));

        private static readonly AdminStatusStyle AdminElevatedStyle = new(
            "#14291F",
            "#1E4631",
            "Modo administrador · Juegos y UAC activos",
            "Control de juegos, apps elevadas y UAC garantizado.",
            "Permisos elevados activos: Puede interactuar libremente con launchers de juegos, Administrador de Tareas y ventanas administrativas.");

        private static readonly AdminStatusStyle AdminStandardStyle = new(
            "#231F1A",
            "#42331B",
            "Modo estándar · Juegos/UAC requieren admin.",
            "Ejecuta como Administrador para interactuar con juegos o UAC.",
            "Windows restringe el control remoto sobre ventanas elevadas como launchers o juegos. Abre la aplicación con 'Ejecutar como Administrador' para habilitarlo.");

        private static TransmissionPresetConfig GetPresetConfig(string presetName) =>
            presetName.ToLowerInvariant() switch
            {
                "quality" => new TransmissionPresetConfig(null, 0, "60", "h264", 15, "15"),
                "speed" => new TransmissionPresetConfig(0.50, null, "30", "h264", 3, "3"),
                _ => new TransmissionPresetConfig(0.75, null, "30", "h264", 8, "8")
            };

        private void ApplyPresetScale(ComboBox? scaleCombo, in TransmissionPresetConfig config)
        {
            if (scaleCombo == null || scaleCombo.Items.Count == 0) return;
            if (config.ScaleIndex is int scaleIdx)
            {
                scaleCombo.SelectedIndex = scaleIdx;
                return;
            }
            if (config.ApproximateScale is double approxScale)
            {
                SelectScaleApproximate(scaleCombo, approxScale);
            }
        }

        private void ApplyPresetControls(TransmissionPresetConfig config)
        {
            var codecCombo = this.FindControl<ComboBox>("CodecComboBox");
            var fpsCombo = this.FindControl<ComboBox>("FpsComboBox");
            var scaleCombo = this.FindControl<ComboBox>("ScaleComboBox");
            var qualitySlider = this.FindControl<Slider>("QualitySlider");
            var bitrateCombo = this.FindControl<ComboBox>("BitrateComboBox");

            ApplyPresetScale(scaleCombo, config);
            SelectComboBoxItemByTag(fpsCombo, config.Fps);
            SelectComboBoxItemByTag(codecCombo, config.Codec);
            UpdateQualitySliderForCodec(config.Codec);

            if (qualitySlider != null)
            {
                qualitySlider.Value = config.Quality;
            }

            SelectComboBoxItemByTag(bitrateCombo, config.Bitrate);
        }

        private void ApplyTransmissionPreset(string presetName, bool save = true)
        {
            _isApplyingPreset = true;
            try
            {
                _appSettings.TransmissionPreset = presetName;

                var config = GetPresetConfig(presetName);
                ApplyPresetControls(config);

                UpdatePresetButtonsHighlight(presetName);
                UpdateHardwareEncoderBadgeUI();
                OnSettingChanged();
                if (save)
                {
                    SaveCurrentSettings();
                }
            }
            finally
            {
                _isApplyingPreset = false;
            }
        }

        private void UpdatePresetButtonsHighlight(string activePreset)
        {
            UpdateSinglePresetButtonHighlight("Quality", activePreset == "quality");
            UpdateSinglePresetButtonHighlight("Balanced", activePreset == "balanced");
            UpdateSinglePresetButtonHighlight("Speed", activePreset == "speed");

            var presetBadgeText = this.FindControl<TextBlock>("PresetBadgeText");
            if (presetBadgeText != null)
            {
                presetBadgeText.Text = activePreset switch
                {
                    "quality" => "Calidad",
                    "speed" => "Fluidez",
                    "custom" => "Personalizado",
                    _ => "Equilibrado"
                };
            }
        }

        private void UpdateSinglePresetButtonHighlight(string prefix, bool isActive)
        {
            var btn = this.FindControl<Button>($"Preset{prefix}Button");
            var icon = this.FindControl<PathIcon>($"Preset{prefix}Icon");
            var title = this.FindControl<TextBlock>($"Preset{prefix}TitleText");
            var sub = this.FindControl<TextBlock>($"Preset{prefix}SubText");

            var theme = isActive ? ActivePresetTheme : InactivePresetTheme;

            if (btn != null)
            {
                btn.Background = theme.Background;
                btn.BorderBrush = theme.Border;
                btn.BorderThickness = theme.Thickness;
            }

            if (icon != null) icon.Foreground = theme.IconForeground;
            if (title != null) title.Foreground = theme.TitleForeground;
            if (sub != null) sub.Foreground = theme.SubForeground;
        }

        private void UpdateAdvancedSettingsVisibility()
        {
            var advPanel = this.FindControl<StackPanel>("AdvancedSettingsPanel");
            var advArrow = this.FindControl<TextBlock>("ToggleAdvancedArrow");
            if (advPanel != null)
            {
                advPanel.IsVisible = _appSettings.ShowAdvancedSettings;
            }
            if (advArrow != null)
            {
                advArrow.Text = _appSettings.ShowAdvancedSettings ? "▴" : "▾";
            }
        }

        private static int CalculateDimension(int target, int native, double scale) =>
            target > 0 ? target : (int)Math.Round((native * scale) / 2.0) * 2;

        private void UpdateResolutionLabels(ResolutionMetrics metrics)
        {
            var captureResLabel = this.FindControl<TextBlock>("CaptureResolutionText");
            if (captureResLabel != null)
            {
                captureResLabel.Text = $"{metrics.NativeW} × {metrics.NativeH}";
            }

            int outW = CalculateDimension(metrics.TargetW, metrics.NativeW, metrics.Scale);
            int outH = CalculateDimension(metrics.TargetH, metrics.NativeH, metrics.Scale);

            var outputResLabel = this.FindControl<TextBlock>("OutputResolutionText");
            if (outputResLabel != null)
            {
                outputResLabel.Text = $"{outW} × {outH}";
            }

            if (!_isViewing)
            {
                var canvasSpecs = this.FindControl<TextBlock>("CanvasSpecsText");
                if (canvasSpecs != null)
                {
                    int fps = GetSelectedFps();
                    canvasSpecs.Text = $"{outW} × {outH} · {fps} fps";
                }
            }
        }

        private void UpdateAdminStatusUI()
        {
            var adminStatusBorder = this.FindControl<Border>("AdminStatusBorder");
            var adminStatusText = this.FindControl<TextBlock>("AdminStatusText");
            if (adminStatusBorder == null || adminStatusText == null) return;

            bool isAdmin = IsRunningAsAdministrator();
            var style = isAdmin ? AdminElevatedStyle : AdminStandardStyle;
            var textBrush = new SolidColorBrush(Color.Parse(isAdmin ? "#4ADE80" : "#FBBF24"));

            adminStatusBorder.Background = new SolidColorBrush(Color.Parse(style.Bg));
            adminStatusBorder.BorderBrush = new SolidColorBrush(Color.Parse(style.Border));
            adminStatusText.Text = style.Text;
            adminStatusText.Foreground = textBrush;

            var adminStatusSubtext = this.FindControl<TextBlock>("AdminStatusSubtext");
            if (adminStatusSubtext != null)
            {
                adminStatusSubtext.Text = style.Subtext;
                adminStatusSubtext.Foreground = new SolidColorBrush(Color.Parse(isAdmin ? "#86EFAC" : "#FDE68A"));
            }

            var adminStatusIcon = this.FindControl<PathIcon>("AdminStatusIcon");
            if (adminStatusIcon != null)
            {
                adminStatusIcon.Foreground = textBrush;
            }

            ToolTip.SetTip(adminStatusBorder, style.Tooltip);
        }

        private static double? TryParseDimensionRatio(string tag, int nativeWidth)
        {
            if (nativeWidth <= 0) return null;
            var parts = tag.Split('x');
            if (parts.Length != 2) return null;
            return int.TryParse(parts[0], out int width) ? (double)width / nativeWidth : null;
        }

        private static double? TryParseScaleRatio(string tag, int nativeWidth)
        {
            if (tag.Contains('x'))
            {
                return TryParseDimensionRatio(tag, nativeWidth);
            }

            return double.TryParse(tag, NumberStyles.Any, CultureInfo.InvariantCulture, out double scale)
                ? scale
                : null;
        }

        private int GetCurrentSourceWidth() =>
            (_currentSource?.Width > 0) ? _currentSource.Width : 1920;

        private static int FindClosestScaleIndex(ComboBox scaleCombo, double targetRatio, int nativeWidth)
        {
            int bestIndex = 0;
            double bestDiff = double.MaxValue;

            for (int i = 0; i < scaleCombo.Items.Count; i++)
            {
                if (scaleCombo.Items[i] is not ComboBoxItem { Tag: string tag }) continue;

                double? ratio = TryParseScaleRatio(tag, nativeWidth);
                if (!ratio.HasValue) continue;

                double diff = Math.Abs(ratio.Value - targetRatio);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        private void SelectScaleApproximate(ComboBox? scaleCombo, double targetRatio)
        {
            if (scaleCombo == null || scaleCombo.Items.Count == 0) return;
            int nativeW = GetCurrentSourceWidth();
            scaleCombo.SelectedIndex = FindClosestScaleIndex(scaleCombo, targetRatio, nativeW);
        }

        private static void SelectComboBoxItemByTag(ComboBox? comboBox, string tag)
        {
            if (comboBox == null) return;
            for (int i = 0; i < comboBox.Items.Count; i++)
            {
                if (comboBox.Items[i] is ComboBoxItem item && item.Tag as string == tag)
                {
                    comboBox.SelectedIndex = i;
                    return;
                }
            }
        }

        private void ApplyEncoderBadgeTheme(Border badge, PathIcon? icon, TextBlock text, BadgeTheme theme)
        {
            badge.Background = new SolidColorBrush(Color.Parse(theme.Bg));
            badge.BorderBrush = new SolidColorBrush(Color.Parse(theme.Border));
            text.Text = theme.Text;
            text.Foreground = new SolidColorBrush(Color.Parse(theme.Fg));

            if (icon != null)
            {
                if (this.TryFindResource(theme.IconResource, out var iconGeo) && iconGeo is Geometry geo)
                {
                    icon.Data = geo;
                }
                icon.Foreground = new SolidColorBrush(Color.Parse(theme.Fg));
            }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private void UpdateWindowsEncoderBadge(Border badge, PathIcon? icon, TextBlock text)
        {
            var encoders = HardwareMftEncoderDiscovery.EnumerateEncoders();
            var best = encoders.Count > 0 ? encoders[0] : null;

            var theme = (best != null && best.IsHardware)
                ? new BadgeTheme("#0F2922", "#10B981", "#34D399", $"⚡ {best.DisplayName} (Hardware)", "IconZap")
                : new BadgeTheme("#261E14", "#D97706", "#FBBF24", "⚙️ CPU (Software Microsoft)", "IconSettings");

            ApplyEncoderBadgeTheme(badge, icon, text, theme);
        }

        private void UpdateHardwareEncoderBadgeUI()
        {
            var badge = this.FindControl<Border>("HardwareEncoderBadge");
            var icon = this.FindControl<PathIcon>("HardwareEncoderIcon");
            var text = this.FindControl<TextBlock>("HardwareEncoderText");

            if (badge == null || text == null) return;

            var codecCombo = this.FindControl<ComboBox>("CodecComboBox");
            string selectedCodec = (codecCombo?.SelectedItem as ComboBoxItem)?.Tag as string ?? "h264";

            if (selectedCodec.Equals("jpeg", StringComparison.OrdinalIgnoreCase))
            {
                var jpegTheme = new BadgeTheme("#1A2230", "#263246", "#94A3B8", "Codificador: JPEG (Software CPU)", "IconSliders");
                ApplyEncoderBadgeTheme(badge, icon, text, jpegTheme);
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                UpdateWindowsEncoderBadge(badge, icon, text);
            }
        }
    }
}
