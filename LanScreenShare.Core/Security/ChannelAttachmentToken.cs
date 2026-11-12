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

    public readonly record struct ChannelAttachmentRequest(
        string TokenId,
        string SessionId,
        string PeerFingerprint,
        ChannelType Channel
    );
}

