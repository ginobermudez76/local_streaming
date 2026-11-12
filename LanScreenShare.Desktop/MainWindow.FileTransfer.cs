using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LanScreenShare.Core.Models;
using LanScreenShare.Network.Streaming;

namespace LanScreenShare.Desktop
{
    public partial class MainWindow
    {
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

            if (this.FindControl<Button>("SendFileButton") is { } sendFileBtn)
                sendFileBtn.Click += async (s, e) => await PickAndSendFilesAsync();

            if (this.FindControl<Button>("FileTransferCancelButton") is { } cancelTransferBtn)
                cancelTransferBtn.Click += (s, e) => CancelActiveFileTransfer();

            if (this.FindControl<Button>("FileTransferOpenFolderButton") is { } openFolderBtn)
                openFolderBtn.Click += (s, e) => OpenDownloadsFolder();

            if (this.FindControl<Button>("FileTransferDismissButton") is { } dismissBannerBtn)
                dismissBannerBtn.Click += (s, e) => HideFileTransferBanner();

            this.AddHandler(DragDrop.DropEvent, MainWindow_Drop);
            this.AddHandler(DragDrop.DragOverEvent, MainWindow_DragOver);
        }

        private bool CanTransferFiles =>
            (_isHosting && _streamServer.ConnectedClientsCount > 0) ||
            (_isViewing && _streamClient.IsConnected);

        private void MainWindow_DragOver(object? sender, DragEventArgs e)
        {
            if (CanTransferFiles && e.DataTransfer.Contains(DataFormat.File))
            {
                e.DragEffects = DragDropEffects.Copy;
                return;
            }
            e.DragEffects = DragDropEffects.None;
        }

        private async void MainWindow_Drop(object? sender, DragEventArgs e)
        {
            if (!CanTransferFiles)
                return;

            if (e.DataTransfer.TryGetFiles() is { } files)
            {
                foreach (var f in files)
                {
                    var path = f.Path.LocalPath;
                    if (File.Exists(path))
                    {
                        await SendFileToPeerAsync(path);
                    }
                }
            }
        }

        private async Task PickAndSendFilesAsync()
        {
            if (!CanTransferFiles)
                return;

            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel?.StorageProvider == null)
                    return;

                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Seleccionar archivo(s) para enviar",
                    AllowMultiple = true
                });

                if (files != null && files.Count > 0)
                {
                    foreach (var file in files)
                    {
                        var localPath = file.Path.LocalPath;
                        if (File.Exists(localPath))
                        {
                            await SendFileToPeerAsync(localPath);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FileTransfer] Error abriendo selector de archivos: {ex.Message}");
            }
        }

        private async Task SendFileToPeerAsync(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                return;

            _activeTransferCts?.Cancel();
            _activeTransferCts?.Dispose();
            _activeTransferCts = new CancellationTokenSource();

            try
            {
                await ExecuteFileTransferAsync(filePath, _activeTransferCts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("[FileTransfer] Envío de archivo cancelado por el usuario.");
            }
            catch (Exception ex)
            {
                HandleFileTransferError(filePath, ex);
            }
        }

        private async Task ExecuteFileTransferAsync(string filePath, CancellationToken ct)
        {
            if (_isHosting && _fileTransferServer.IsRunning)
            {
                await _fileTransferServer.SendFileAsync(filePath, ct);
            }
            else if (_isViewing && _fileTransferClient.IsConnected)
            {
                await _fileTransferClient.SendFileAsync(filePath, ct);
            }
        }

        private void HandleFileTransferError(string filePath, Exception ex)
        {
            Console.WriteLine($"[FileTransfer] Error enviando archivo '{filePath}': {ex.Message}");
            ShowFileTransferError(new FileTransferError
            {
                TransferId = Guid.NewGuid().ToString(),
                FileName = System.IO.Path.GetFileName(filePath),
                ErrorMessage = ex.Message,
                IsUpload = true
            });
        }

        private void ShowFileTransferProgress(FileTransferProgress p)
        {
            _fileTransferBannerTimer?.Stop();

            var banner = this.FindControl<Border>("FileTransferBanner");
            if (banner != null) banner.IsVisible = true;

            SetTransferBannerText(p.IsUpload ? "Enviando archivo..." : "Recibiendo archivo...", Brushes.White);

            var subtitle = this.FindControl<TextBlock>("FileTransferBannerSubtitle");
            if (subtitle != null)
            {
                double speedMb = p.SpeedBytesPerSec / (1024.0 * 1024.0);
                subtitle.Text = $"{p.FileName} — {p.Percent:0}% ({speedMb:0.1} MB/s)";
            }

            UpdateProgressBar(true, p.Percent, "#10B981");
            SetTransferBannerButtonVisibility(showOpen: false, showCancel: true, showDismiss: false);
        }

        private void SetTransferBannerText(string title, IBrush foreground)
        {
            var titleBlock = this.FindControl<TextBlock>("FileTransferBannerTitle");
            if (titleBlock != null)
            {
                titleBlock.Text = title;
                titleBlock.Foreground = foreground;
            }
        }

        private void UpdateProgressBar(bool isVisible, double val, string colorHex)
        {
            var progressBar = this.FindControl<ProgressBar>("FileTransferProgressBar");
            if (progressBar != null)
            {
                progressBar.IsVisible = isVisible;
                if (isVisible)
                {
                    progressBar.Value = val;
                    progressBar.Foreground = new SolidColorBrush(Color.Parse(colorHex));
                }
            }
        }

        private void SetTransferBannerButtonVisibility(bool showOpen, bool showCancel, bool showDismiss)
        {
            var openFolderBtn = this.FindControl<Button>("FileTransferOpenFolderButton");
            if (openFolderBtn != null) openFolderBtn.IsVisible = showOpen;

            var cancelBtn = this.FindControl<Button>("FileTransferCancelButton");
            if (cancelBtn != null) cancelBtn.IsVisible = showCancel;

            var dismissBtn = this.FindControl<Button>("FileTransferDismissButton");
            if (dismissBtn != null) dismissBtn.IsVisible = showDismiss;
        }

        private readonly record struct TransferBannerConfig(
            string Title,
            string TitleColorHex,
            string Subtitle,
            bool ShowProgressBar = false,
            double ProgressVal = 0,
            string ProgressColorHex = "#10B981",
            bool ShowOpenFolder = false);

        private void ShowFileTransferStatusBanner(in TransferBannerConfig cfg)
        {
            _fileTransferBannerTimer?.Stop();

            var banner = this.FindControl<Border>("FileTransferBanner");
            if (banner != null) banner.IsVisible = true;

            SetTransferBannerText(cfg.Title, new SolidColorBrush(Color.Parse(cfg.TitleColorHex)));

            var subtitleBlock = this.FindControl<TextBlock>("FileTransferBannerSubtitle");
            if (subtitleBlock != null) subtitleBlock.Text = cfg.Subtitle;

            UpdateProgressBar(cfg.ShowProgressBar, cfg.ProgressVal, cfg.ProgressColorHex);
            SetTransferBannerButtonVisibility(showOpen: cfg.ShowOpenFolder, showCancel: false, showDismiss: true);

            _fileTransferBannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            _fileTransferBannerTimer.Tick += (s, e) =>
            {
                _fileTransferBannerTimer.Stop();
                HideFileTransferBanner();
            };
            _fileTransferBannerTimer.Start();
        }

        private void ShowFileTransferCompleted(FileTransferCompleted c)
        {
            _lastReceivedFilePath = c.FilePath;
            string title = c.IsUpload ? "Archivo enviado con éxito" : "Archivo recibido";
            double mb = c.FileSize / (1024.0 * 1024.0);
            string subtitle = $"{c.FileName} ({mb:0.1} MB) — Completado";
            bool canOpen = !string.IsNullOrEmpty(c.FilePath) && File.Exists(c.FilePath);

            ShowFileTransferStatusBanner(new TransferBannerConfig(
                title, "#34D399", subtitle,
                ShowProgressBar: true, ProgressVal: 100, ProgressColorHex: "#10B981", ShowOpenFolder: canOpen));
        }

        private void ShowFileTransferError(FileTransferError err)
        {
            ShowFileTransferStatusBanner(new TransferBannerConfig(
                "Error en transferencia", "#F87171", $"{err.FileName}: {err.ErrorMessage}"));
        }

        private void CancelActiveFileTransfer()
        {
            try
            {
                _activeTransferCts?.Cancel();
            }
            catch { }
            HideFileTransferBanner();
        }

        private void HideFileTransferBanner()
        {
            _fileTransferBannerTimer?.Stop();
            var banner = this.FindControl<Border>("FileTransferBanner");
            if (banner != null)
            {
                banner.IsVisible = false;
            }
        }

        private void OpenDownloadsFolder()
        {
            try
            {
                if (TryOpenTargetFile(_lastReceivedFilePath))
                    return;

                TryOpenDefaultDownloadsDirectory();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FileTransfer] Error abriendo carpeta: {ex.Message}");
            }
        }

        private static bool TryOpenTargetFile(string? targetPath)
        {
            if (string.IsNullOrEmpty(targetPath) || !File.Exists(targetPath))
                return false;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start("explorer.exe", $"/select,\"{targetPath}\"");
                return true;
            }

            string? dir = System.IO.Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
                return true;
            }

            return false;
        }

        private static void TryOpenDefaultDownloadsDirectory()
        {
            string downloads = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "LanScreenShare");
            if (Directory.Exists(downloads))
            {
                Process.Start(new ProcessStartInfo { FileName = downloads, UseShellExecute = true });
            }
        }
    }
}
