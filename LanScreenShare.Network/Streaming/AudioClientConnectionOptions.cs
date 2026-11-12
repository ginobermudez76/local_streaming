using System.Net.Security;

namespace LanScreenShare.Network.Streaming
{
    public readonly record struct AudioClientConnectionOptions(
        string HostIp,
        int Port = 5002,
        SslClientAuthenticationOptions? SslOptions = null,
        string? SessionId = null,
        string? AttachmentToken = null);
}

