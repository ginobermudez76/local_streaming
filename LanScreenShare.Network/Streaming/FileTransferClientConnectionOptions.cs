using System.Net.Security;

namespace LanScreenShare.Network.Streaming
{
    public readonly record struct FileTransferClientConnectionOptions(
        string HostIp,
        int Port = 5004,
        SslClientAuthenticationOptions? SslOptions = null,
        string? SessionId = null,
        string? AttachmentToken = null);
}

