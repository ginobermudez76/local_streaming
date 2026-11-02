using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

        public SourcePickerWindow() : this(null)
        {
        }

        public SourcePickerWindow(string? initialSourceId)
        {
            InitializeComponent();
            _initialSourceId = initialSourceId;

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

            var refreshBtn = this.FindControl<Button>("RefreshWindowsButton");
            if (refreshBtn != null)
            {
                refreshBtn.Click += (s, e) => LoadWindows();
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

        private void LoadSources()
        {
            LoadScreens();
            LoadWindows();

            // Seleccionar por defecto la fuente inicial o la primera pantalla
            if (!string.IsNullOrEmpty(_initialSourceId))
            {
                var screenMatch = _screens.FirstOrDefault(s => s.Id == _initialSourceId);
                if (screenMatch != null)
                {
                    var screensList = this.FindControl<ListBox>("ScreensListBox");
                    if (screensList != null) screensList.SelectedItem = screenMatch;
                    return;
                }

                var windowMatch = _windows.FirstOrDefault(w => w.Id == _initialSourceId);
                if (windowMatch != null)
                {
                    var windowsList = this.FindControl<ListBox>("WindowsListBox");
                    if (windowsList != null) windowsList.SelectedItem = windowMatch;
                    return;
                }
            }

            if (_screens.Count > 0)
            {
                var screensList = this.FindControl<ListBox>("ScreensListBox");
                if (screensList != null) screensList.SelectedIndex = 0;
            }
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
                var windowsList = this.FindControl<ListBox>("WindowsListBox");
                if (windowsList != null && windowsList.SelectedItem != null)
                {
                    windowsList.SelectedItem = null;
                }
                UpdateStatusText(source);
            }
        }

        private void WindowsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            var windowsList = this.FindControl<ListBox>("WindowsListBox");
            if (windowsList?.SelectedItem is CaptureSource source)
            {
                SelectedSource = source;
                var screensList = this.FindControl<ListBox>("ScreensListBox");
                if (screensList != null && screensList.SelectedItem != null)
                {
                    screensList.SelectedItem = null;
                }
                UpdateStatusText(source);
            }
        }

        private void UpdateStatusText(CaptureSource source)
        {
            var infoText = this.FindControl<TextBlock>("SelectedSourceInfoText");
            if (infoText != null)
            {
                infoText.Text = $"Seleccionado: {source.Name} ({source.Width}×{source.Height})";
                infoText.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#38BDF8"));
            }
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

