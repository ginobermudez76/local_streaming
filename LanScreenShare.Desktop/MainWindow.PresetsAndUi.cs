using System;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using LanScreenShare.Core.Models;

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
                        break;

                    case "balanced":
                    default:
                        // 70-75% Escala, 30 FPS, Bitrate balanceado (8 Mbps H.264 / 75% JPEG)
                        SelectScaleApproximate(scaleCombo, 0.75);
                        SelectComboBoxItemByTag(fpsCombo, "30");
                        SelectComboBoxItemByTag(codecCombo, "h264");
                        UpdateQualitySliderForCodec("h264");
                        if (qualitySlider != null)
                        {
                            qualitySlider.Value = 8;
                        }
                        break;
                }

                UpdatePresetButtonsHighlight(presetName);
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

            var activeBg = new SolidColorBrush(Color.Parse("#1E3A8A"));
            var activeBorder = new SolidColorBrush(Color.Parse("#3B82F6"));
            var normalBg = new SolidColorBrush(Color.Parse("#1E1E24"));
            var normalBorder = new SolidColorBrush(Color.Parse("#3F3F46"));

            if (presetQualityBtn != null)
            {
                bool isAct = activePreset == "quality";
                presetQualityBtn.Background = isAct ? activeBg : normalBg;
                presetQualityBtn.BorderBrush = isAct ? activeBorder : normalBorder;
            }

            if (presetBalancedBtn != null)
            {
                bool isAct = activePreset == "balanced";
                presetBalancedBtn.Background = isAct ? activeBg : normalBg;
                presetBalancedBtn.BorderBrush = isAct ? activeBorder : normalBorder;
            }

            if (presetSpeedBtn != null)
            {
                bool isAct = activePreset == "speed";
                presetSpeedBtn.Background = isAct ? activeBg : normalBg;
                presetSpeedBtn.BorderBrush = isAct ? activeBorder : normalBorder;
            }

            if (presetBadgeText != null)
            {
                presetBadgeText.Text = activePreset switch
                {
                    "quality" => "🌟 Calidad",
                    "speed" => "🍃 Fluidez",
                    "custom" => "⚙️ Personalizado",
                    _ => "⚡ Equilibrado"
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
                captureResLabel.Text = $"{nativeW}×{nativeH}";
            }

            var outputResLabel = this.FindControl<TextBlock>("OutputResolutionText");
            if (outputResLabel != null)
            {
                if (targetW > 0 && targetH > 0)
                {
                    int pct = (int)Math.Round(((double)targetW / Math.Max(1, nativeW)) * 100);
                    outputResLabel.Text = $"{targetW}×{targetH} ({pct}%)";
                }
                else
                {
                    int outW = (int)Math.Round((nativeW * scale) / 2.0) * 2;
                    int outH = (int)Math.Round((nativeH * scale) / 2.0) * 2;
                    int pct = (int)Math.Round(scale * 100);
                    outputResLabel.Text = $"{outW}×{outH} ({pct}%)";
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
                    adminStatusBorder.Background = new SolidColorBrush(Color.Parse("#14532D"));
                    adminStatusBorder.BorderBrush = new SolidColorBrush(Color.Parse("#16A34A"));
                    adminStatusText.Text = "Administrador Activo";
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
                    adminStatusBorder.Background = new SolidColorBrush(Color.Parse("#451A03"));
                    adminStatusBorder.BorderBrush = new SolidColorBrush(Color.Parse("#D97706"));
                    adminStatusText.Text = "Modo Estándar";
                    adminStatusText.Foreground = new SolidColorBrush(Color.Parse("#FBBF24"));
                    if (adminStatusSubtext != null)
                    {
                        adminStatusSubtext.Text = "Ejecuta como Administrador para interactuar con juegos o UAC.";
                        adminStatusSubtext.Foreground = new SolidColorBrush(Color.Parse("#FDE68A"));
                    }
                    if (adminStatusIcon != null) adminStatusIcon.Foreground = new SolidColorBrush(Color.Parse("#FBBF24"));
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
    }
}

