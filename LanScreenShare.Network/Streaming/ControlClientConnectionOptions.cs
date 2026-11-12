using System.Net.Security;

namespace LanScreenShare.Network.Streaming
{
    public readonly record struct ControlClientConnectionOptions(
        string HostIp,
        int Port = 5003,
        SslClientAuthenticationOptions? SslOptions = null,
        string? SessionId = null,
        string? AttachmentToken = null);
}

