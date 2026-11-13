using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace LanScreenShare.Desktop
{
    public partial class StreamPopoutWindow
    {
        private DispatcherTimer? _gameModeBannerTimer;

        private readonly record struct BannerStyle(string IconName, string ColorHex, string Text, double Seconds);

        private static readonly BannerStyle ActiveGameModeStyle = new(
            "Icon3dRotation", "#38BDF8", "Modo 3D Activo — Presiona Ctrl + Alt + L para liberar el cursor", 3.0);

        private static readonly BannerStyle InactiveGameModeStyle = new(
            "IconLockOpen", "#FBBF24", "Mouse Liberado — Presiona Ctrl + Alt + L para recapturar", 2.5);

        private static bool IsMutedOrZero(bool isMuted, float volume) =>
            isMuted || volume <= 0.01f;

        private bool TryGetStreamGeometry(string resourceKey, out StreamGeometry geometry)
        {
            if (this.TryFindResource(resourceKey, out var res) && res is StreamGeometry geo)
            {
                geometry = geo;
                return true;
            }
            geometry = null!;
            return false;
        }

        private void InitializeAudioControls()
        {
            var muteBtn = this.FindControl<Button>("AudioMuteButton");
            if (muteBtn != null) muteBtn.Click += (s, e) => MuteToggled?.Invoke();

            ConfigureAudioVolumeSlider("AudioVolumeSlider", VolumeChanged, UpdateVolumeText);

            var micMuteBtn = this.FindControl<Button>("MicMuteButton");
            if (micMuteBtn != null) micMuteBtn.Click += (s, e) => MicMuteToggled?.Invoke();

            ConfigureAudioVolumeSlider("MicVolumeSlider", MicVolumeChanged, UpdateMicVolumeText);
        }

        private void ConfigureAudioVolumeSlider(string sliderName, Action<float>? volumeChanged, Action<double> updateVolumeText)
        {
            var slider = this.FindControl<Slider>(sliderName);
            if (slider == null) return;

            slider.PropertyChanged += (s, e) =>
            {
                if (e.Property.Name == nameof(Slider.Value))
                {
                    float vol = (float)Math.Clamp(slider.Value / 100.0, 0.0, 1.0);
                    volumeChanged?.Invoke(vol);
                    updateVolumeText(slider.Value);
                }
            };
        }

        public void SetVolume(float volume, bool isMuted)
        {
            var volumeSlider = this.FindControl<Slider>("AudioVolumeSlider");
            if (volumeSlider != null)
            {
                volumeSlider.Value = Math.Round(volume * 100);
            }
            UpdateVolumeText(volume * 100);

            var muteIcon = this.FindControl<PathIcon>("AudioMuteIcon");
            string iconName = IsMutedOrZero(isMuted, volume) ? "IconVolumeOff" : "IconVolumeUp";
            if (muteIcon != null && TryGetStreamGeometry(iconName, out var geo))
            {
                muteIcon.Data = geo;
            }
        }

        private void UpdateVolumeText(double percent)
        {
            var volumeText = this.FindControl<TextBlock>("AudioVolumeText");
            if (volumeText != null)
            {
                volumeText.Text = $"{(int)Math.Round(percent)}%";
            }
        }

        public void SetMicVolume(float volume, bool isMuted)
        {
            var volumeSlider = this.FindControl<Slider>("MicVolumeSlider");
            if (volumeSlider != null)
            {
                volumeSlider.Value = Math.Round(volume * 100);
            }
            UpdateMicVolumeText(volume * 100);

            var muteIcon = this.FindControl<PathIcon>("MicMuteIcon");
            if (muteIcon == null) return;

            bool muted = IsMutedOrZero(isMuted, volume);
            string iconName = muted ? "IconMicOff" : "IconMic";
            if (TryGetStreamGeometry(iconName, out var geo))
            {
                muteIcon.Data = geo;
                muteIcon.Foreground = new SolidColorBrush(Color.Parse(muted ? "#EF4444" : "#10B981"));
            }
        }

        private void UpdateMicVolumeText(double percent)
        {
            var volumeText = this.FindControl<TextBlock>("MicVolumeText");
            if (volumeText != null)
            {
                volumeText.Text = $"{(int)Math.Round(percent)}%";
            }
        }

        public void SetGameModeState(bool isGameMode)
        {
            _isGameMode = isGameMode;
            var gameModeBtn = this.FindControl<ToggleButton>("GameModeButton");
            var screenImage = this.FindControl<Image>("ScreenImage");
            var banner = this.FindControl<Border>("GameModeBanner");
            var bannerText = this.FindControl<TextBlock>("GameModeBannerText");
            var bannerIcon = this.FindControl<PathIcon>("GameModeBannerIcon");

            if (gameModeBtn != null)
            {
                gameModeBtn.IsChecked = isGameMode;
                gameModeBtn.Background = isGameMode ? new SolidColorBrush(Color.Parse("#2563EB")) : new SolidColorBrush(Color.Parse("#27272A"));
            }

            if (screenImage != null)
            {
                screenImage.Cursor = isGameMode ? new Cursor(StandardCursorType.None) : Cursor.Default;
            }

            ShowGameModeBanner(isGameMode, banner, bannerText, bannerIcon);
        }

        private void ShowGameModeBanner(bool isGameMode, Border? banner, TextBlock? bannerText, PathIcon? bannerIcon)
        {
            if (banner == null || bannerText == null) return;

            var style = isGameMode ? ActiveGameModeStyle : InactiveGameModeStyle;
            banner.IsVisible = true;

            if (bannerIcon != null && TryGetStreamGeometry(style.IconName, out var geo))
            {
                bannerIcon.Data = geo;
                bannerIcon.Foreground = new SolidColorBrush(Color.Parse(style.ColorHex));
            }
            bannerText.Text = style.Text;
            bannerText.Foreground = new SolidColorBrush(Color.Parse(style.ColorHex));

            RestartBannerTimer(banner, style.Seconds);
        }

        private void RestartBannerTimer(Border banner, double seconds)
        {
            _gameModeBannerTimer?.Stop();
            _gameModeBannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            _gameModeBannerTimer.Tick += (s, e) =>
            {
                banner.IsVisible = false;
                _gameModeBannerTimer?.Stop();
            };
            _gameModeBannerTimer.Start();
        }

        public void SetSecureDesktopState(bool isBlocked)
        {
            var banner = this.FindControl<Border>("SecureDesktopBanner");
            if (banner != null)
            {
                banner.IsVisible = isBlocked;
            }
        }
    }
}

