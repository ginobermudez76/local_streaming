using System;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;

namespace LanScreenShare.Desktop
{
    public partial class MainWindow
    {
        #region Security & Pairing Approvals

        private bool OnProvisionalPairingRequested(string fingerprint, X509Certificate2 clientCert)
        {
            // Permitir el establecimiento del canal TLS seguro.
            // La autorización interactiva y verificación SAS se ejecuta en el canal de aplicación en TcpStreamServer.
            return true;
        }

        private bool OnClientProvisionalPairing(string fingerprint, X509Certificate2 hostCert)
        {
            return true;
        }

        private async Task<bool> RequestPairingApprovalAsync(string deviceName, string fingerprint, string sasFormatted)
        {
            _streamApprovalTcs?.TrySetResult(false);
            var tcs = new TaskCompletionSource<bool>();
            _streamApprovalTcs = tcs;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var banner = this.FindControl<Border>("StreamApprovalBanner");
                var title = this.FindControl<TextBlock>("StreamApprovalTitle");
                var subtitle = this.FindControl<TextBlock>("StreamApprovalSubtitle");
                var sasText = this.FindControl<TextBlock>("StreamApprovalSasCode");

                if (title != null) title.Text = $"Emparejamiento: {deviceName}";
                if (subtitle != null) subtitle.Text = "Compara este código fuera de banda antes de aprobar:";
                if (sasText != null)
                {
                    sasText.Text = $"Código SAS: {sasFormatted}";
                    sasText.IsVisible = true;
                }
                if (banner != null) banner.IsVisible = true;
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var reg = cts.Token.Register(() => tcs.TrySetResult(false));

            try
            {
                return await tcs.Task;
            }
            finally
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var banner = this.FindControl<Border>("StreamApprovalBanner");
                    var sasText = this.FindControl<TextBlock>("StreamApprovalSasCode");
                    if (sasText != null) sasText.IsVisible = false;
                    if (banner != null) banner.IsVisible = false;
                });
            }
        }

        private async Task<bool> RequestApprovalBannerAsync(
            string bannerName,
            string subtitleName,
            string subtitleText,
            Action<TaskCompletionSource<bool>> setTcs)
        {
            var tcs = new TaskCompletionSource<bool>();
            setTcs(tcs);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var banner = this.FindControl<Border>(bannerName);
                var subtitle = this.FindControl<TextBlock>(subtitleName);
                if (subtitle != null) subtitle.Text = subtitleText;
                if (banner != null) banner.IsVisible = true;
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            using var reg = cts.Token.Register(() => tcs.TrySetResult(false));

            try
            {
                return await tcs.Task;
            }
            finally
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var banner = this.FindControl<Border>(bannerName);
                    if (banner != null) banner.IsVisible = false;
                });
            }
        }

        private Task<bool> RequestStreamApprovalAsync(string clientId)
        {
            _streamApprovalTcs?.TrySetResult(false);
            return RequestApprovalBannerAsync(
                "StreamApprovalBanner",
                "StreamApprovalSubtitle",
                $"El dispositivo '{clientId}' solicita ver tu pantalla.",
                tcs => _streamApprovalTcs = tcs);
        }

        private Task<bool> RequestControlApprovalAsync(string clientId)
        {
            _controlApprovalTcs?.TrySetResult(false);
            return RequestApprovalBannerAsync(
                "ControlApprovalBanner",
                "ControlApprovalSubtitle",
                $"El dispositivo '{clientId}' solicita controlar tu mouse y teclado.",
                tcs => _controlApprovalTcs = tcs);
        }

        #endregion
    }
}

