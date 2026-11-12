using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Desktop
{
    public partial class MainWindow
    {
        #region Remote Monitor Menu

        private async Task OpenRemoteSourceMenuAsync(Button? anchorButton)
        {
            if (!_isViewing || !_currentDeviceAllowsRemoteControl)
                return;

            anchorButton ??= this.FindControl<Button>("SwitchMonitorButton");
            if (anchorButton == null)
                return;

            EnsureRemoteSourceMenuInitialized();
            PopulateRemoteSourceMenu();
            _remoteSourceMenu!.Open(anchorButton);

            if (await EnsureControlConnectedAsync())
            {
                await _controlClient.RequestSourceCatalogAsync();
            }
        }

        private void EnsureRemoteSourceMenuInitialized()
        {
            _remoteSourceMenu ??= new ContextMenu
            {
                Background = new SolidColorBrush(Color.Parse("#1A2234")),
                BorderBrush = new SolidColorBrush(Color.Parse("#2D3B55")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(4)
            };
        }

        private readonly record struct CategoryStyle(string HeaderTitle, IBrush ActiveBrush, IBrush InactiveBrush, string Icon);

        private static readonly CategoryStyle ScreenCategoryStyle = new(
            "PANTALLAS DEL ANFITRIÓN",
            new SolidColorBrush(Color.Parse("#38BDF8")),
            new SolidColorBrush(Color.Parse("#F1F5F9")),
            "🖥️ ");

        private static readonly CategoryStyle WindowCategoryStyle = new(
            "VENTANAS / APLICACIONES",
            new SolidColorBrush(Color.Parse("#A78BFA")),
            new SolidColorBrush(Color.Parse("#E2E8F0")),
            "🪟 ");

        private void PopulateRemoteSourceMenu()
        {
            if (_remoteSourceMenu == null)
                return;

            var items = new List<object>();
            var catalog = _remoteMonitorCatalog;

            if (catalog?.Sources != null && catalog.Sources.Count > 0)
            {
                AddSourceCategoryMenuItems(items, catalog.Sources.Where(s => s.Category == "screen"), ScreenCategoryStyle);
                if (items.Count > 0)
                {
                    items.Add(new Separator { Background = new SolidColorBrush(Color.Parse("#263042")), Margin = new Thickness(0, 4) });
                }
                AddSourceCategoryMenuItems(items, catalog.Sources.Where(s => s.Category == "window"), WindowCategoryStyle);
            }
            else
            {
                items.Add(CreateLoadingMenuItem());
            }

            AddStaticRemoteSourceMenuItems(items);
            _remoteSourceMenu.ItemsSource = items;
        }

        private MenuItem CreateLoadingMenuItem() => new()
        {
            Header = _controlClient.IsConnected
                ? "⏳ Consultando pantallas y ventanas del anfitrión..."
                : "⏳ Conectando control remoto para listar fuentes...",
            IsEnabled = false,
            Foreground = new SolidColorBrush(Color.Parse("#94A3B8"))
        };

        private void AddSourceCategoryMenuItems(List<object> items, IEnumerable<RemoteSourceItemDto> sources, in CategoryStyle style)
        {
            var list = sources.ToList();
            if (list.Count == 0) return;

            items.Add(new MenuItem
            {
                Header = style.HeaderTitle,
                IsEnabled = false,
                FontSize = 10,
                FontWeight = FontWeight.Bold,
                Foreground = style.ActiveBrush
            });

            foreach (var s in list)
            {
                items.Add(CreateSourceMenuItem(s, style));
            }
        }

        private MenuItem CreateSourceMenuItem(RemoteSourceItemDto s, in CategoryStyle style)
        {
            bool isActive = s.Id == _remoteMonitorCatalog?.ActiveSourceId ||
                            (!string.IsNullOrEmpty(s.Name) && s.Name == _currentRemoteMonitorLabel);
            string prefix = isActive ? $"✓ {style.Icon}" : $"    {style.Icon}";
            string displayName = s.Name.Length > 45 ? s.Name[..42] + "..." : s.Name;
            string subtitle = !string.IsNullOrWhiteSpace(s.Subtitle) ? $" ({s.Subtitle})" : string.Empty;

            var menuItem = new MenuItem
            {
                Header = $"{prefix}{displayName}{subtitle}",
                FontWeight = isActive ? FontWeight.Bold : FontWeight.Normal,
                Foreground = isActive ? style.ActiveBrush : style.InactiveBrush,
                Tag = s.Id
            };
            string capturedId = s.Id;
            menuItem.Click += async (_, _) => await SelectRemoteSourceAsync(capturedId);
            return menuItem;
        }

        private void AddStaticRemoteSourceMenuItems(List<object> items)
        {
            items.Add(new Separator { Background = new SolidColorBrush(Color.Parse("#263042")), Margin = new Thickness(0, 4) });

            var cycleItem = new MenuItem
            {
                Header = "➔ Alternar siguiente fuente (Ctrl+Alt+M)",
                Foreground = new SolidColorBrush(Color.Parse("#38BDF8")),
                FontWeight = FontWeight.SemiBold
            };
            cycleItem.Click += async (_, _) => await RequestRemoteMonitorSwitchAsync();
            items.Add(cycleItem);

            var refreshItem = new MenuItem
            {
                Header = "🔄 Actualizar lista de fuentes",
                Foreground = new SolidColorBrush(Color.Parse("#CBD5E1"))
            };
            refreshItem.Click += async (_, _) =>
            {
                if (await EnsureControlConnectedAsync())
                {
                    await _controlClient.RequestSourceCatalogAsync();
                }
            };
            items.Add(refreshItem);
        }

        private bool CanSendRemoteSourceCommand(string? sourceId = null) =>
            (sourceId == null || !string.IsNullOrWhiteSpace(sourceId)) && _isViewing && _currentDeviceAllowsRemoteControl;

        private async Task SelectRemoteSourceAsync(string sourceId)
        {
            if (!CanSendRemoteSourceCommand(sourceId))
                return;

            if (await EnsureControlConnectedAsync())
            {
                await _controlClient.SendSelectSourceAsync(sourceId);
            }
        }

        private async Task RequestRemoteMonitorSwitchAsync()
        {
            if (!CanSendRemoteSourceCommand())
                return;

            if (await EnsureControlConnectedAsync())
            {
                await _controlClient.SendSwitchMonitorAsync(-1);
            }
        }

        private bool TryParseMonitorCatalog(string payload, out string activeName)
        {
            activeName = payload;
            if (!payload.TrimStart().StartsWith("{", StringComparison.Ordinal))
                return false;

            try
            {
                var catalog = JsonSerializer.Deserialize<RemoteMonitorCatalogDto>(payload);
                if (catalog != null)
                {
                    _remoteMonitorCatalog = catalog;
                    if (!string.IsNullOrWhiteSpace(catalog.ActiveSourceName))
                    {
                        activeName = catalog.ActiveSourceName;
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ControlViewer] Error parseando catálogo de monitores: {ex.Message}");
            }
            return false;
        }

        private void ApplyRemoteMonitorInfo(string monitorPayload)
        {
            if (string.IsNullOrWhiteSpace(monitorPayload))
                return;

            Dispatcher.UIThread.Post(() =>
            {
                TryParseMonitorCatalog(monitorPayload, out string monitorLabel);
                _currentRemoteMonitorLabel = monitorLabel;

                string shortLabel = monitorLabel.Length > 22 ? monitorLabel[..19] + "..." : monitorLabel;
                UpdateMonitorButtonAndPopout(shortLabel);

                if (_remoteSourceMenu?.IsOpen == true)
                {
                    PopulateRemoteSourceMenu();
                }

                if (_isViewing)
                {
                    UpdateCanvasSourceTitle(monitorLabel);
                    UpdateStatus($"Viendo {monitorLabel} de {_currentConnectedDeviceName}", "#22C55E");
                }
            });
        }

        private void UpdateMonitorButtonAndPopout(string shortLabel)
        {
            var switchBtnText = this.FindControl<TextBlock>("SwitchMonitorButtonText");
            if (switchBtnText != null) switchBtnText.Text = shortLabel;
            _popoutWindow?.SetActiveMonitorLabel(shortLabel);
        }

        private void UpdateCanvasSourceTitle(string monitorLabel)
        {
            var canvasTitle = this.FindControl<TextBlock>("CanvasSourceTitleText");
            if (canvasTitle != null)
            {
                canvasTitle.Text = !string.IsNullOrEmpty(_currentConnectedDeviceName)
                    ? $"{_currentConnectedDeviceName} · {monitorLabel}"
                    : monitorLabel;
            }
        }

        #endregion

        #region Clipboard Synchronization

        private void ApplyRemoteClipboardText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    if (text == _lastLocalClipboardText)
                        return;

                    _lastNetworkClipboardText = text;
                    _lastLocalClipboardText = text;

                    var topLevel = TopLevel.GetTopLevel(this);
                    if (topLevel?.Clipboard != null)
                    {
                        await topLevel.Clipboard.SetTextAsync(text);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ClipboardSync] Error aplicando texto remoto al portapapeles: {ex.Message}");
                }
            });
        }

        private bool CanSynchronizeClipboard(out bool asHost, out bool asViewer)
        {
            asHost = _isHosting && _allowRemoteControl && _controlServer.IsRunning;
            asViewer = _isViewing && _remoteControlEnabled && _controlClient.IsConnected;
            return asHost || asViewer;
        }

        private async void ClipboardSyncTimer_Tick(object? sender, EventArgs e)
        {
            if (_isPollingClipboard || !_allowClipboardSync)
                return;

            if (!CanSynchronizeClipboard(out bool asHost, out bool asViewer))
                return;

            _isPollingClipboard = true;
            try
            {
                await SyncLocalClipboardAsync(asHost, asViewer);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ClipboardSync] Error leyendo/sincronizando portapapeles: {ex.Message}");
            }
            finally
            {
                _isPollingClipboard = false;
            }
        }

        private bool IsDuplicateOrEmptyClipboard(string? text) =>
            string.IsNullOrEmpty(text) || text == _lastLocalClipboardText || text == _lastNetworkClipboardText;

        private async Task SyncLocalClipboardAsync(bool asHost, bool asViewer)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard == null)
                return;

            string? currentText = await topLevel.Clipboard.TryGetTextAsync();
            if (string.IsNullOrEmpty(currentText) || IsDuplicateOrEmptyClipboard(currentText))
            {
                if (!string.IsNullOrEmpty(currentText)) _lastLocalClipboardText = currentText;
                return;
            }

            _lastLocalClipboardText = currentText;
            _lastNetworkClipboardText = null;

            if (asHost)
            {
                await _controlServer.BroadcastClipboardTextAsync(currentText);
            }
            else if (asViewer)
            {
                await _controlClient.SendClipboardTextAsync(currentText);
            }
        }

        #endregion
    }
}
