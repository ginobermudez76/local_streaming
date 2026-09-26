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
    public class DiscoveryService
    {
        private const int DiscoveryPort = 5000;
        private const string BroadcastMessagePrefix = "LAN_SCREEN_SHARE_HOST";
        
        private CancellationTokenSource? _hostCts;
        private CancellationTokenSource? _listenCts;

        public event EventHandler<DeviceInfo>? OnDeviceDiscovered;

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
                        var targets = GetActiveBroadcastTargets();

                        foreach (var target in targets)
                        {
                            try
                            {
                                using var udpClient = new UdpClient();
                                udpClient.EnableBroadcast = true;

                                var message = $"{BroadcastMessagePrefix}|{pcName}|{target.LocalIp}";
                                var bytes = Encoding.UTF8.GetBytes(message);

                                // 1. Broadcast dirigido a la subred de esta interfaz física (ej. 192.168.100.255)
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
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error en ciclo de broadcast: {ex.Message}");
                    }

                    await Task.Delay(3000, token);
                }
            }, token);
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
                        var message = Encoding.UTF8.GetString(result.Buffer);
                        
                        var parts = message.Split('|');
                        if (parts.Length >= 3 && parts[0] == BroadcastMessagePrefix)
                        {
                            var senderName = parts[1];
                            var reportedIp = parts[2];
                            var remoteIp = result.RemoteEndPoint.Address.ToString();

                            // Evitar auto-descubrimiento si es este mismo equipo
                            if (IsSelfMessage(senderName, reportedIp, remoteIp))
                            {
                                continue;
                            }

                            // Usar la IP reportada si es válida; si viene vacía o es loopback/APIPA, usar la IP remota del socket
                            var finalIp = (!string.IsNullOrWhiteSpace(reportedIp) && reportedIp != "127.0.0.1" && !reportedIp.StartsWith("169.254"))
                                ? reportedIp
                                : remoteIp;

                            var device = new DeviceInfo
                            {
                                Name = senderName,
                                IPAddress = finalIp,
                                LastSeen = DateTime.Now
                            };

                            OnDeviceDiscovered?.Invoke(this, device);
                        }
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

        private static List<(IPAddress LocalIp, IPAddress BroadcastIp)> GetActiveBroadcastTargets()
        {
            var targets = new List<(IPAddress LocalIp, IPAddress BroadcastIp)>();

            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces();

                foreach (var ni in interfaces)
                {
                    if (ni.OperationalStatus != OperationalStatus.Up)
                        continue;

                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                        continue;

                    var ipProps = ni.GetIPProperties();
                    foreach (var unicast in ipProps.UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                            continue;

                        var ip = unicast.Address;

                        // Descartar Loopback y direcciones APIPA (169.254.x.x)
                        if (IPAddress.IsLoopback(ip) || ip.ToString().StartsWith("169.254."))
                            continue;

                        var mask = unicast.IPv4Mask;
                        if (mask == null || mask.Equals(IPAddress.Any) || mask.Equals(IPAddress.None))
                        {
                            mask = IPAddress.Parse("255.255.255.0");
                        }

                        var broadcast = CalculateBroadcastAddress(ip, mask);
                        targets.Add((ip, broadcast));
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error obteniendo interfaces de red: {ex.Message}");
            }

            if (targets.Count == 0)
            {
                targets.Add((IPAddress.Any, IPAddress.Broadcast));
            }

            return targets;
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
