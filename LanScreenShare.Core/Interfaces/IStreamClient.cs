using System;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Core.Interfaces
{
    public interface IStreamClient : IDisposable
    {
        event EventHandler<CapturedFrame>? FrameReceived;
        event EventHandler<string>? StatusChanged;
        event EventHandler<StreamTelemetry>? TelemetryUpdated;
        bool IsConnected { get; }
        StreamTelemetry CurrentTelemetry { get; }
        void UpdateAvDrift(int driftMs);
        Task ConnectAsync(string hostIp, int port = 5001, CancellationToken cancellationToken = default);
        Task DisconnectAsync();
    }
}
