using System;
using System.Net;
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
                using var udpClient = new UdpClient();
                udpClient.EnableBroadcast = true;
                
                var endPoint = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);
                var pcName = Environment.MachineName;

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var message = $"{BroadcastMessagePrefix}|{pcName}|{GetLocalIPAddress()}";
                        var bytes = Encoding.UTF8.GetBytes(message);
                        
                        await udpClient.SendAsync(bytes, bytes.Length, endPoint);
                    }
                    catch (Exception ex)
                    {
                        // TODO: Log exception
                        Console.WriteLine($"Error broadcasting: {ex.Message}");
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
                        if (parts.Length == 3 && parts[0] == BroadcastMessagePrefix)
                        {
                            var device = new DeviceInfo
                            {
                                Name = parts[1],
                                IPAddress = parts[2],
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
                        // TODO: Log exception
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

        private string GetLocalIPAddress()
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    return ip.ToString();
                }
            }
            return "127.0.0.1";
        }
    }
}
