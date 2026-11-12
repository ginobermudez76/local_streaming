using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Network
{
    public readonly record struct BroadcastTarget(IPAddress LocalIp, IPAddress BroadcastIp);

    public class DiscoveryService
    {
        private const int DiscoveryPort = 5000;
        private const string BroadcastMessagePrefix = "LAN_SCREEN_SHARE_HOST";
        private static readonly IPAddress DefaultSubnetMask = IPAddress.Parse("255.255.255.0");

        private CancellationTokenSource? _hostCts;
        private CancellationTokenSource? _listenCts;

        public event EventHandler<DeviceInfo>? OnDeviceDiscovered;
        public Func<bool>? IsRemoteControlAllowedProvider { get; set; }

        public void StartHostingBroadcast()
        {
            if (_hostCts != null && !_hostCts.IsCancellationRequested)
                return;

            _hostCts = new CancellationTokenSource();
            var token = _hostCts.Token;

            Task.Run(async () =>
            {
                var pcName = Environment.MachineName;

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await BroadcastToAllTargetsAsync(pcName);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error en ciclo de broadcast: {ex.Message}");
                    }

                    await Task.Delay(3000, token);
                }
            }, token);
        }

        private async Task BroadcastToAllTargetsAsync(string pcName)
        {
            var targets = GetActiveBroadcastTargets();
            foreach (var target in targets)
            {
                await TrySendBroadcastAsync(target, pcName);
            }
        }

        private async Task TrySendBroadcastAsync(BroadcastTarget target, string pcName)
        {
            try
            {
                using var udpClient = new UdpClient { EnableBroadcast = true };
                var rcAllowed = IsRemoteControlAllowedProvider?.Invoke() ?? true;
                var message = $"{BroadcastMessagePrefix}|{pcName}|{target.LocalIp}|{(rcAllowed ? "1" : "0")}";
                var bytes = Encoding.UTF8.GetBytes(message);

                // 1. Broadcast dirigido a la subred de esta interfaz física
                await udpClient.SendAsync(bytes, bytes.Length, new IPEndPoint(target.BroadcastIp, DiscoveryPort));

                // 2. Broadcast global (255.255.255.255) como respaldo
                if (!target.BroadcastIp.Equals(IPAddress.Broadcast))
                {
                    await udpClient.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error enviando broadcast a {target.BroadcastIp}: {ex.Message}");
            }
        }

        public void StopHosting()
        {
            _hostCts?.Cancel();
            _hostCts?.Dispose();
            _hostCts = null;
        }

        public void StartListening()
        {
            if (_listenCts != null && !_listenCts.IsCancellationRequested)
                return;

            _listenCts = new CancellationTokenSource();
            var token = _listenCts.Token;

            Task.Run(async () =>
            {
                using var udpClient = new UdpClient();
                udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var result = await udpClient.ReceiveAsync(token);
                        ProcessIncomingDatagram(result);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error listening: {ex.Message}");
                    }
                }
            }, token);
        }

        private void ProcessIncomingDatagram(UdpReceiveResult result)
        {
            var message = Encoding.UTF8.GetString(result.Buffer);
            var parts = message.Split('|');
            if (parts.Length < 3 || parts[0] != BroadcastMessagePrefix)
            {
                return;
            }

            var senderName = parts[1];
            var reportedIp = parts[2];
            var remoteIp = result.RemoteEndPoint.Address.ToString();

            if (IsSelfMessage(senderName, reportedIp, remoteIp))
            {
                return;
            }

            bool isRemoteControlAllowed = parts.Length < 4 || parts[3] == "1";
            var finalIp = ResolveFinalIp(reportedIp, remoteIp);

            OnDeviceDiscovered?.Invoke(this, new DeviceInfo
            {
                Name = senderName,
                IPAddress = finalIp,
                LastSeen = DateTime.Now,
                IsRemoteControlAllowed = isRemoteControlAllowed
            });
        }

        private static string ResolveFinalIp(string reportedIp, string remoteIp)
        {
            bool isReportedValid = !string.IsNullOrWhiteSpace(reportedIp) &&
                                   reportedIp != "127.0.0.1" &&
                                   !reportedIp.StartsWith("169.254");

            return isReportedValid ? reportedIp : remoteIp;
        }

        public void StopListening()
        {
            _listenCts?.Cancel();
            _listenCts?.Dispose();
            _listenCts = null;
        }

        private static bool IsSelfMessage(string senderName, string reportedIp, string remoteIp)
        {
            if (!string.Equals(senderName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                return false;

            if (reportedIp == "127.0.0.1" || remoteIp == "127.0.0.1")
                return true;

            var localIps = GetActiveBroadcastTargets().Select(t => t.LocalIp.ToString()).ToHashSet();
            return localIps.Contains(reportedIp) || localIps.Contains(remoteIp);
        }

        private static List<BroadcastTarget> GetActiveBroadcastTargets()
        {
            var targets = new List<BroadcastTarget>();

            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (IsEligibleInterface(ni))
                    {
                        CollectInterfaceTargets(ni, targets);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error obteniendo interfaces de red: {ex.Message}");
            }

            if (targets.Count == 0)
            {
                targets.Add(new BroadcastTarget(IPAddress.Any, IPAddress.Broadcast));
            }

            return targets;
        }

        private static bool IsEligibleInterface(NetworkInterface ni) =>
            ni.OperationalStatus == OperationalStatus.Up &&
            ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
            ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel;

        private static void CollectInterfaceTargets(NetworkInterface ni, List<BroadcastTarget> targets)
        {
            foreach (var unicast in ni.GetIPProperties().UnicastAddresses)
            {
                if (TryExtractTarget(unicast, out var target))
                {
                    targets.Add(target);
                }
            }
        }

        private static bool TryExtractTarget(UnicastIPAddressInformation unicast, out BroadcastTarget target)
        {
            target = default;
            var ip = unicast.Address;

            if (!IsEligibleIpv4(ip))
            {
                return false;
            }

            var mask = NormalizeMask(unicast.IPv4Mask);
            var broadcast = CalculateBroadcastAddress(ip, mask);
            target = new BroadcastTarget(ip, broadcast);
            return true;
        }

        private static bool IsEligibleIpv4(IPAddress ip)
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
            if (IPAddress.IsLoopback(ip)) return false;
            return !ip.ToString().StartsWith("169.254.");
        }

        private static IPAddress NormalizeMask(IPAddress? mask) =>
            IsValidSubnetMask(mask) ? mask! : DefaultSubnetMask;

        private static bool IsValidSubnetMask(IPAddress? mask)
        {
            if (mask == null) return false;
            return !mask.Equals(IPAddress.Any) && !mask.Equals(IPAddress.None);
        }

        private static IPAddress CalculateBroadcastAddress(IPAddress address, IPAddress mask)
        {
            byte[] ipBytes = address.GetAddressBytes();
            byte[] maskBytes = mask.GetAddressBytes();

            if (ipBytes.Length != maskBytes.Length)
                return IPAddress.Broadcast;

            byte[] broadcastBytes = new byte[ipBytes.Length];
            for (int i = 0; i < broadcastBytes.Length; i++)
            {
                broadcastBytes[i] = (byte)(ipBytes[i] | (maskBytes[i] ^ 255));
            }
            return new IPAddress(broadcastBytes);
        }
    }
}
