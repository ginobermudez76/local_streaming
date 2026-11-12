using System;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using LanScreenShare.Core.Security;
using LanScreenShare.Network.Security;

namespace LanScreenShare.Desktop
{
    public partial class MainWindow
    {
        #region Security & Pairing Approvals

        private void InitializeSecuritySubsystem()
        {
            _identityKeyStorage = IdentityKeyStorageFactory.Create();
            _pairedDeviceStore = new JsonPairedDeviceStore();
            _sessionAuthority = new SessionAuthority(_pairedDeviceStore);

            try
            {
                _identityCertificate = _identityKeyStorage.GetOrCreateIdentityCertificateAsync().GetAwaiter().GetResult();
                _sslServerOptions = TlsConfigurationHelper.CreateServerOptions(_identityCertificate, _pairedDeviceStore, OnProvisionalPairingRequested);
                _sslClientOptions = TlsConfigurationHelper.CreateClientOptions(_identityCertificate, "LanScreenShareHost", _pairedDeviceStore, OnClientProvisionalPairing);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Security] Error inicializando identidad mTLS: {ex.Message}");
            }
        }

        private void ConfigureAccessModeComboBox(string comboName, string currentSetting, Action<string> onModeChanged)
        {
            var combo = this.FindControl<ComboBox>(comboName);
            if (combo == null) return;

            SelectComboBoxItemByTag(combo, currentSetting);
            onModeChanged((combo.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty);

            combo.SelectionChanged += (s, e) =>
            {
                var tag = (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? string.Empty;
                onModeChanged(tag);
                SaveCurrentSettings();
            };
        }

        private void InitializeAccessAndApproval()
        {
            ConfigureAccessModeComboBox("StreamAccessModeComboBox", _appSettings.StreamAccessMode, tag =>
            {
                _requireStreamApproval = tag == "ask";
                Console.WriteLine($"[AccessHost] Aprobación de stream requerida: {_requireStreamApproval}");
            });
            _streamServer.ClientApprovalCallback = CheckStreamClientApproval;
            WireApprovalButtons("ApproveStreamButton", "RejectStreamButton", () => _streamApprovalTcs);

            ConfigureAccessModeComboBox("ControlAccessModeComboBox", _appSettings.ControlAccessMode, tag =>
            {
                _remoteControlMode = string.IsNullOrEmpty(tag) ? "auto" : tag;
                Console.WriteLine($"[AccessHost] Modo de control remoto: {_remoteControlMode}");
            });
            _controlServer.ClientApprovalCallback = CheckControlClientApproval;
            WireApprovalButtons("ApproveControlButton", "RejectControlButton", () => _controlApprovalTcs);
        }

        private void WireApprovalButtons(string approveButton, string rejectButton, Func<TaskCompletionSource<bool>?> getTcs)
        {
            WireButtonClick(approveButton, (s, e) => getTcs()?.TrySetResult(true));
            WireButtonClick(rejectButton, (s, e) => getTcs()?.TrySetResult(false));
        }

        private async Task<bool> CheckStreamClientApproval(string clientId)
        {
            return !_requireStreamApproval || await RequestStreamApprovalAsync(clientId);
        }

        private async Task<bool> CheckControlClientApproval(string clientId)
        {
            if (!_allowRemoteControl) return false;
            return _remoteControlMode == "auto" || await RequestControlApprovalAsync(clientId);
        }

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

