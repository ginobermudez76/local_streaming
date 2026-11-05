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
        private void ApplyTransmissionPreset(string presetName, bool save = true)
        {
            _isApplyingPreset = true;
            try
            {
                _appSettings.TransmissionPreset = presetName;

                var codecCombo = this.FindControl<ComboBox>("CodecComboBox");
                var fpsCombo = this.FindControl<ComboBox>("FpsComboBox");
                var scaleCombo = this.FindControl<ComboBox>("ScaleComboBox");
                var qualitySlider = this.FindControl<Slider>("QualitySlider");
                var bitrateCombo = this.FindControl<ComboBox>("BitrateComboBox");

                switch (presetName.ToLowerInvariant())
                {
                    case "quality":
                        // 100% Nativa, 60 FPS, Bitrate alto (15 Mbps H.264 / 85% JPEG)
                        if (scaleCombo != null && scaleCombo.Items.Count > 0)
                        {
                            scaleCombo.SelectedIndex = 0; // Nativa
                        }
                        SelectComboBoxItemByTag(fpsCombo, "60");
                        SelectComboBoxItemByTag(codecCombo, "h264");
                        UpdateQualitySliderForCodec("h264");
                        if (qualitySlider != null)
                        {
                            qualitySlider.Value = 15;
                        }
                        SelectComboBoxItemByTag(bitrateCombo, "15");
                        break;

                    case "speed":
                        // 50% Escala, 30 FPS, Bitrate bajo (3 Mbps H.264 / 50% JPEG)
                        SelectScaleApproximate(scaleCombo, 0.50);
                        SelectComboBoxItemByTag(fpsCombo, "30");
                        SelectComboBoxItemByTag(codecCombo, "h264");
                        UpdateQualitySliderForCodec("h264");
                        if (qualitySlider != null)
                        {
                            qualitySlider.Value = 3;
                        }
                        SelectComboBoxItemByTag(bitrateCombo, "3");
                        break;

                    case "balanced":
                    default:
                        // 75% Escala, 30 FPS, Bitrate balanceado (8 Mbps H.264 / 75% JPEG)
                        SelectScaleApproximate(scaleCombo, 0.75);
                        SelectComboBoxItemByTag(fpsCombo, "30");
                        SelectComboBoxItemByTag(codecCombo, "h264");
                        UpdateQualitySliderForCodec("h264");
                        if (qualitySlider != null)
                        {
                            qualitySlider.Value = 8;
                        }
                        SelectComboBoxItemByTag(bitrateCombo, "8");
                        break;
                }

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
            var presetQualityBtn = this.FindControl<Button>("PresetQualityButton");
            var presetBalancedBtn = this.FindControl<Button>("PresetBalancedButton");
            var presetSpeedBtn = this.FindControl<Button>("PresetSpeedButton");
            var presetBadgeText = this.FindControl<TextBlock>("PresetBadgeText");

            var activeBg = new SolidColorBrush(Color.Parse("#192B4D"));
            var activeBorder = new SolidColorBrush(Color.Parse("#3B82F6"));
            var normalBg = new SolidColorBrush(Color.Parse("#1A2230"));
            var normalBorder = new SolidColorBrush(Color.Parse("#263246"));

            var activeIconBrush = new SolidColorBrush(Color.Parse("#60A5FA"));
            var normalIconBrush = new SolidColorBrush(Color.Parse("#94A3B8"));
            var activeTitleBrush = new SolidColorBrush(Color.Parse("#F8FAFC"));
            var normalTitleBrush = new SolidColorBrush(Color.Parse("#E2E8F0"));
            var activeSubBrush = new SolidColorBrush(Color.Parse("#93C5FD"));
            var normalSubBrush = new SolidColorBrush(Color.Parse("#7C8B9E"));

            void ApplyButtonState(Button? btn, PathIcon? icon, TextBlock? title, TextBlock? sub, bool isAct)
            {
                if (btn != null)
                {
                    btn.Background = isAct ? activeBg : normalBg;
                    btn.BorderBrush = isAct ? activeBorder : normalBorder;
                    btn.BorderThickness = isAct ? new Thickness(1.5) : new Thickness(1);
                }
                if (icon != null) icon.Foreground = isAct ? activeIconBrush : normalIconBrush;
                if (title != null) title.Foreground = isAct ? activeTitleBrush : normalTitleBrush;
                if (sub != null) sub.Foreground = isAct ? activeSubBrush : normalSubBrush;
            }

            ApplyButtonState(
                presetQualityBtn,
                this.FindControl<PathIcon>("PresetQualityIcon"),
                this.FindControl<TextBlock>("PresetQualityTitleText"),
                this.FindControl<TextBlock>("PresetQualitySubText"),
                activePreset == "quality");

            ApplyButtonState(
                presetBalancedBtn,
                this.FindControl<PathIcon>("PresetBalancedIcon"),
                this.FindControl<TextBlock>("PresetBalancedTitleText"),
                this.FindControl<TextBlock>("PresetBalancedSubText"),
                activePreset == "balanced");

            ApplyButtonState(
                presetSpeedBtn,
                this.FindControl<PathIcon>("PresetSpeedIcon"),
                this.FindControl<TextBlock>("PresetSpeedTitleText"),
                this.FindControl<TextBlock>("PresetSpeedSubText"),
                activePreset == "speed");

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

        private void UpdateResolutionLabels(int nativeW, int nativeH, int targetW, int targetH, double scale)
        {
            var captureResLabel = this.FindControl<TextBlock>("CaptureResolutionText");
            if (captureResLabel != null)
            {
                captureResLabel.Text = $"{nativeW} × {nativeH}";
            }

            int outW;
            int outH;
            if (targetW > 0 && targetH > 0)
            {
                outW = targetW;
                outH = targetH;
            }
            else
            {
                outW = (int)Math.Round((nativeW * scale) / 2.0) * 2;
                outH = (int)Math.Round((nativeH * scale) / 2.0) * 2;
            }

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
            var adminStatusSubtext = this.FindControl<TextBlock>("AdminStatusSubtext");
            var adminStatusIcon = this.FindControl<PathIcon>("AdminStatusIcon");

            if (adminStatusBorder != null && adminStatusText != null)
            {
                if (IsRunningAsAdministrator())
                {
                    adminStatusBorder.Background = new SolidColorBrush(Color.Parse("#14291F"));
                    adminStatusBorder.BorderBrush = new SolidColorBrush(Color.Parse("#1E4631"));
                    adminStatusText.Text = "Modo administrador · Juegos y UAC activos";
                    adminStatusText.Foreground = new SolidColorBrush(Color.Parse("#4ADE80"));
                    if (adminStatusSubtext != null)
                    {
                        adminStatusSubtext.Text = "Control de juegos, apps elevadas y UAC garantizado.";
                        adminStatusSubtext.Foreground = new SolidColorBrush(Color.Parse("#86EFAC"));
                    }
                    if (adminStatusIcon != null) adminStatusIcon.Foreground = new SolidColorBrush(Color.Parse("#4ADE80"));
                    ToolTip.SetTip(adminStatusBorder, "Permisos elevados activos: Puede interactuar libremente con launchers de juegos, Administrador de Tareas y ventanas administrativas.");
                }
                else
                {
                    adminStatusBorder.Background = new SolidColorBrush(Color.Parse("#231F1A"));
                    adminStatusBorder.BorderBrush = new SolidColorBrush(Color.Parse("#42331B"));
                    adminStatusText.Text = "Modo estándar · Juegos/UAC requieren admin.";
                    adminStatusText.Foreground = new SolidColorBrush(Color.Parse("#FBBF24"));
                    if (adminStatusSubtext != null)
                    {
                        adminStatusSubtext.Text = "Ejecuta como Administrador para interactuar con juegos o UAC.";
                        adminStatusSubtext.Foreground = new SolidColorBrush(Color.Parse("#FDE68A"));
                    }
                    if (adminStatusIcon != null) adminStatusIcon.Foreground = new SolidColorBrush(Color.Parse("#F59E0B"));
                    ToolTip.SetTip(adminStatusBorder, "Windows restringe el control remoto sobre ventanas elevadas como launchers o juegos. Abre la aplicación con 'Ejecutar como Administrador' para habilitarlo.");
                }
            }
        }

        private void SelectScaleApproximate(ComboBox? scaleCombo, double targetRatio)
        {
            if (scaleCombo == null || scaleCombo.Items.Count == 0) return;

            int nativeW = (_currentSource != null && _currentSource.Width > 0) ? _currentSource.Width : 1920;
            int bestIndex = 0;
            double bestDiff = double.MaxValue;

            for (int i = 0; i < scaleCombo.Items.Count; i++)
            {
                if (scaleCombo.Items[i] is ComboBoxItem item && item.Tag is string tag)
                {
                    if (tag.Contains('x'))
                    {
                        var parts = tag.Split('x');
                        if (parts.Length == 2 && int.TryParse(parts[0], out int w))
                        {
                            double ratio = (double)w / nativeW;
                            double diff = Math.Abs(ratio - targetRatio);
                            if (diff < bestDiff)
                            {
                                bestDiff = diff;
                                bestIndex = i;
                            }
                        }
                    }
                    else if (double.TryParse(tag, NumberStyles.Any, CultureInfo.InvariantCulture, out double s))
                    {
                        double diff = Math.Abs(s - targetRatio);
                        if (diff < bestDiff)
                        {
                            bestDiff = diff;
                            bestIndex = i;
                        }
                    }
                }
            }

            scaleCombo.SelectedIndex = bestIndex;
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
                badge.Background = new SolidColorBrush(Color.Parse("#1A2230"));
                badge.BorderBrush = new SolidColorBrush(Color.Parse("#263246"));
                text.Text = "Codificador: JPEG (Software CPU)";
                text.Foreground = new SolidColorBrush(Color.Parse("#94A3B8"));
                if (icon != null)
                {
                    if (this.TryFindResource("IconSliders", out var iconGeo) && iconGeo is Geometry geo)
                    {
                        icon.Data = geo;
                    }
                    icon.Foreground = new SolidColorBrush(Color.Parse("#94A3B8"));
                }
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                var encoders = HardwareMftEncoderDiscovery.EnumerateEncoders();
                var best = encoders.Count > 0 ? encoders[0] : null;

                if (best != null && best.IsHardware)
                {
                    badge.Background = new SolidColorBrush(Color.Parse("#0F2922"));
                    badge.BorderBrush = new SolidColorBrush(Color.Parse("#10B981"));
                    text.Text = $"⚡ {best.DisplayName} (Hardware)";
                    text.Foreground = new SolidColorBrush(Color.Parse("#34D399"));
                    if (icon != null)
                    {
                        if (this.TryFindResource("IconZap", out var iconGeo) && iconGeo is Geometry geo)
                        {
                            icon.Data = geo;
                        }
                        icon.Foreground = new SolidColorBrush(Color.Parse("#34D399"));
                    }
                }
                else
                {
                    badge.Background = new SolidColorBrush(Color.Parse("#261E14"));
                    badge.BorderBrush = new SolidColorBrush(Color.Parse("#D97706"));
                    text.Text = "⚙️ CPU (Software Microsoft)";
                    text.Foreground = new SolidColorBrush(Color.Parse("#FBBF24"));
                    if (icon != null)
                    {
                        if (this.TryFindResource("IconSettings", out var iconGeo) && iconGeo is Geometry geo)
                        {
                            icon.Data = geo;
                        }
                        icon.Foreground = new SolidColorBrush(Color.Parse("#FBBF24"));
                    }
                }
            }
        }
    }
}
