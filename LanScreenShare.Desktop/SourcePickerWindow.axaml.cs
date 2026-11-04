using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using LanScreenShare.Core.Models;
using LanScreenShare.Media;

namespace LanScreenShare.Desktop
{
    public partial class SourcePickerWindow : Window
    {
        public CaptureSource? SelectedSource { get; private set; }

        private readonly ObservableCollection<CaptureSource> _screens = new();
        private readonly ObservableCollection<CaptureSource> _windows = new();
        private readonly string? _initialSourceId;
        private bool _isWindowsTabActive;

        public SourcePickerWindow() : this(null)
        {
        }

        public SourcePickerWindow(string? initialSourceId)
        {
            InitializeComponent();
            _initialSourceId = initialSourceId;

            var headerDrag = this.FindControl<Grid>("HeaderDragGrid");
            if (headerDrag != null)
            {
                headerDrag.PointerPressed += (s, e) =>
                {
                    if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                    {
                        BeginMoveDrag(e);
                    }
                };
            }

            this.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    SelectedSource = null;
                    Close(null);
                    e.Handled = true;
                }
                else if (e.Key == Key.Enter && SelectedSource != null)
                {
                    ConfirmSelection();
                    e.Handled = true;
                }
            };

            var screensList = this.FindControl<ListBox>("ScreensListBox");
            if (screensList != null)
            {
                screensList.ItemsSource = _screens;
                screensList.SelectionChanged += ScreensList_SelectionChanged;
                screensList.DoubleTapped += (s, e) => ConfirmSelection();
            }

            var windowsList = this.FindControl<ListBox>("WindowsListBox");
            if (windowsList != null)
            {
                windowsList.ItemsSource = _windows;
                windowsList.SelectionChanged += WindowsList_SelectionChanged;
                windowsList.DoubleTapped += (s, e) => ConfirmSelection();
            }

            var screensTabBtn = this.FindControl<Button>("ScreensTabButton");
            if (screensTabBtn != null)
            {
                screensTabBtn.Click += (s, e) => SwitchTab(isWindowsTab: false);
            }

            var windowsTabBtn = this.FindControl<Button>("WindowsTabButton");
            if (windowsTabBtn != null)
            {
                windowsTabBtn.Click += (s, e) => SwitchTab(isWindowsTab: true);
            }

            var refreshBtn = this.FindControl<Button>("RefreshWindowsButton");
            if (refreshBtn != null)
            {
                refreshBtn.Click += (s, e) =>
                {
                    var prevId = (windowsList?.SelectedItem as CaptureSource)?.Id;
                    LoadWindows();
                    UpdateCountLabel();
                    if (windowsList != null && _windows.Count > 0)
                    {
                        var match = prevId != null ? _windows.FirstOrDefault(w => w.Id == prevId) : null;
                        windowsList.SelectedItem = match ?? _windows[0];
                    }
                };
            }

            var closeBtn = this.FindControl<Button>("CloseButton");
            if (closeBtn != null)
            {
                closeBtn.Click += (s, e) =>
                {
                    SelectedSource = null;
                    Close(null);
                };
            }

            var cancelBtn = this.FindControl<Button>("CancelButton");
            if (cancelBtn != null)
            {
                cancelBtn.Click += (s, e) =>
                {
                    SelectedSource = null;
                    Close(null);
                };
            }

            var selectBtn = this.FindControl<Button>("SelectButton");
            if (selectBtn != null)
            {
                selectBtn.Click += (s, e) => ConfirmSelection();
            }

            LoadSources();
        }

        private void SwitchTab(bool isWindowsTab)
        {
            _isWindowsTabActive = isWindowsTab;

            var screensTabBtn = this.FindControl<Button>("ScreensTabButton");
            var screensTabIcon = this.FindControl<PathIcon>("ScreensTabIcon");
            var screensTabText = this.FindControl<TextBlock>("ScreensTabText");

            var windowsTabBtn = this.FindControl<Button>("WindowsTabButton");
            var windowsTabIcon = this.FindControl<PathIcon>("WindowsTabIcon");
            var windowsTabText = this.FindControl<TextBlock>("WindowsTabText");

            var activeBg = new SolidColorBrush(Color.Parse("#1E3252"));
            var inactiveBg = Brushes.Transparent;
            var activeIconBrush = new SolidColorBrush(Color.Parse("#60A5FA"));
            var activeTextBrush = new SolidColorBrush(Color.Parse("#F8FAFC"));
            var inactiveBrush = new SolidColorBrush(Color.Parse("#8B9BB0"));

            if (screensTabBtn != null) screensTabBtn.Background = !isWindowsTab ? activeBg : inactiveBg;
            if (screensTabIcon != null) screensTabIcon.Foreground = !isWindowsTab ? activeIconBrush : inactiveBrush;
            if (screensTabText != null) screensTabText.Foreground = !isWindowsTab ? activeTextBrush : inactiveBrush;

            if (windowsTabBtn != null) windowsTabBtn.Background = isWindowsTab ? activeBg : inactiveBg;
            if (windowsTabIcon != null) windowsTabIcon.Foreground = isWindowsTab ? activeIconBrush : inactiveBrush;
            if (windowsTabText != null) windowsTabText.Foreground = isWindowsTab ? activeTextBrush : inactiveBrush;

            var screensScroll = this.FindControl<ScrollViewer>("ScreensScrollViewer");
            var windowsScroll = this.FindControl<ScrollViewer>("WindowsScrollViewer");
            var refreshBtn = this.FindControl<Button>("RefreshWindowsButton");

            if (screensScroll != null) screensScroll.IsVisible = !isWindowsTab;
            if (windowsScroll != null) windowsScroll.IsVisible = isWindowsTab;
            if (refreshBtn != null) refreshBtn.IsVisible = isWindowsTab;

            UpdateCountLabel();

            if (isWindowsTab)
            {
                var windowsList = this.FindControl<ListBox>("WindowsListBox");
                if (windowsList != null)
                {
                    if (windowsList.SelectedItem is CaptureSource winSource)
                    {
                        SelectedSource = winSource;
                        UpdateStatusText(winSource);
                    }
                    else if (_windows.Count > 0)
                    {
                        windowsList.SelectedIndex = 0;
                    }
                }
            }
            else
            {
                var screensList = this.FindControl<ListBox>("ScreensListBox");
                if (screensList != null)
                {
                    if (screensList.SelectedItem is CaptureSource scrSource)
                    {
                        SelectedSource = scrSource;
                        UpdateStatusText(scrSource);
                    }
                    else if (_screens.Count > 0)
                    {
                        var primary = _screens.FirstOrDefault(s => s.IsPrimary) ?? _screens[0];
                        screensList.SelectedItem = primary;
                    }
                }
            }
        }

        private void UpdateCountLabel()
        {
            var countText = this.FindControl<TextBlock>("SourceCountText");
            if (countText == null) return;

            if (_isWindowsTabActive)
            {
                countText.Text = _windows.Count == 1
                    ? "1 ventana activa detectada"
                    : $"{_windows.Count} ventanas activas detectadas";
            }
            else
            {
                countText.Text = _screens.Count == 1
                    ? "1 fuente disponible"
                    : $"{_screens.Count} fuentes disponibles";
            }
        }

        private void LoadSources()
        {
            LoadScreens();
            LoadWindows();

            var screensList = this.FindControl<ListBox>("ScreensListBox");
            var windowsList = this.FindControl<ListBox>("WindowsListBox");

            if (!string.IsNullOrEmpty(_initialSourceId))
            {
                var windowMatch = _windows.FirstOrDefault(w => w.Id == _initialSourceId);
                if (windowMatch != null)
                {
                    if (windowsList != null) windowsList.SelectedItem = windowMatch;
                    SwitchTab(isWindowsTab: true);
                    return;
                }

                var screenMatch = _screens.FirstOrDefault(s => s.Id == _initialSourceId);
                if (screenMatch != null)
                {
                    if (screensList != null) screensList.SelectedItem = screenMatch;
                    SwitchTab(isWindowsTab: false);
                    return;
                }
            }

            if (_screens.Count > 0 && screensList != null)
            {
                var primary = _screens.FirstOrDefault(s => s.IsPrimary) ?? _screens[0];
                screensList.SelectedItem = primary;
            }
            SwitchTab(isWindowsTab: false);
        }

        private void LoadScreens()
        {
            _screens.Clear();
            var screens = CaptureSourceDiscoveryService.GetScreenSources();
            foreach (var s in screens)
            {
                _screens.Add(s);
            }
        }

        private void LoadWindows()
        {
            _windows.Clear();
            var windows = CaptureSourceDiscoveryService.GetWindowSources();
            foreach (var w in windows)
            {
                _windows.Add(w);
            }
        }

        private void ScreensList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            var screensList = this.FindControl<ListBox>("ScreensListBox");
            if (screensList?.SelectedItem is CaptureSource source)
            {
                SelectedSource = source;
                UpdateStatusText(source);
            }
        }

        private void WindowsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            var windowsList = this.FindControl<ListBox>("WindowsListBox");
            if (windowsList?.SelectedItem is CaptureSource source)
            {
                SelectedSource = source;
                UpdateStatusText(source);
            }
        }

        private void UpdateStatusText(CaptureSource source)
        {
            var infoText = this.FindControl<TextBlock>("SelectedSourceInfoText");
            if (infoText == null) return;

            if (source.Type == CaptureSourceType.AllScreens)
            {
                infoText.Text = "Todas las pantallas · Escritorio completo";
            }
            else if (source.Type == CaptureSourceType.Screen)
            {
                infoText.Text = source.IsPrimary
                    ? $"{source.Name} · Principal"
                    : source.Name;
            }
            else
            {
                infoText.Text = FormatWindowSelectedSummary(source.Name);
            }
        }

        private static string FormatWindowSelectedSummary(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return "Aplicación";

            string[] separators = { " – ", " - ", " — " };
            var parts = title.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                return $"{parts[^2]} · {parts[^1]}";
            }
            return title;
        }

        private void ConfirmSelection()
        {
            if (SelectedSource != null)
            {
                Close(SelectedSource);
            }
        }
    }
}
