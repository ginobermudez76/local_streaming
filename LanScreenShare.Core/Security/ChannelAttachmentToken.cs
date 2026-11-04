using System;

namespace LanScreenShare.Core.Security
{
    public sealed record ChannelAttachmentToken(
        string TokenId,
        string SessionId,
        string PeerFingerprint,
        ChannelType AllowedChannel,
        DateTime ExpiresAt
    );
}

