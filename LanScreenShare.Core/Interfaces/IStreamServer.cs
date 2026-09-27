using System;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Core.Interfaces
{
    public interface IStreamServer : IDisposable
    {
        event EventHandler<string>? ClientConnected;
        event EventHandler<string>? ClientDisconnected;
        bool IsRunning { get; }
        int ConnectedClientsCount { get; }
        Task StartAsync(int port = 5001, CancellationToken cancellationToken = default);
        Task BroadcastFrameAsync(CapturedFrame frame);
        void Stop();
    }
}
