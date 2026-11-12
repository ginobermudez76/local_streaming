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

        private void InitializeStreamAccessMode()
        {
            var streamAccessCombo = this.FindControl<ComboBox>("StreamAccessModeComboBox");
            if (streamAccessCombo != null)
            {
                SelectComboBoxItemByTag(streamAccessCombo, _appSettings.StreamAccessMode);
                _requireStreamApproval = (streamAccessCombo.SelectedItem as ComboBoxItem)?.Tag as string == "ask";
                streamAccessCombo.SelectionChanged += (s, e) =>
                {
                    _requireStreamApproval = (streamAccessCombo.SelectedItem as ComboBoxItem)?.Tag as string == "ask";
                    Console.WriteLine($"[AccessHost] Aprobación de stream requerida: {_requireStreamApproval}");
                    SaveCurrentSettings();
                };
            }

            _streamServer.ClientApprovalCallback = async (clientId) =>
            {
                if (!_requireStreamApproval) return true;
                return await RequestStreamApprovalAsync(clientId);
            };

            WireButtonClick("ApproveStreamButton", (s, e) => _streamApprovalTcs?.TrySetResult(true));
            WireButtonClick("RejectStreamButton", (s, e) => _streamApprovalTcs?.TrySetResult(false));
        }

        private void InitializeControlAccessMode()
        {
            var controlAccessCombo = this.FindControl<ComboBox>("ControlAccessModeComboBox");
            if (controlAccessCombo != null)
            {
                SelectComboBoxItemByTag(controlAccessCombo, _appSettings.ControlAccessMode);
                _remoteControlMode = (controlAccessCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
                controlAccessCombo.SelectionChanged += (s, e) =>
                {
                    _remoteControlMode = (controlAccessCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
                    Console.WriteLine($"[AccessHost] Modo de control remoto: {_remoteControlMode}");
                    SaveCurrentSettings();
                };
            }

            _controlServer.ClientApprovalCallback = async (clientId) =>
            {
                if (!_allowRemoteControl) return false;
                if (_remoteControlMode == "auto") return true;
                return await RequestControlApprovalAsync(clientId);
            };

            WireButtonClick("ApproveControlButton", (s, e) => _controlApprovalTcs?.TrySetResult(true));
            WireButtonClick("RejectControlButton", (s, e) => _controlApprovalTcs?.TrySetResult(false));
        }

        private void InitializeAccessAndApproval()
        {
            InitializeStreamAccessMode();
            InitializeControlAccessMode();
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

