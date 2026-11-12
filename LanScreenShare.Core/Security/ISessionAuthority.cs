using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LanScreenShare.Core.Security
{
    public interface ISessionAuthority
    {
        string? ActiveControllerSessionId { get; }
        event EventHandler<string>? SessionTerminated;

        ActiveSession CreateSession(string peerFingerprint, string peerName, TimeSpan? ttl = null);
        Task<(bool Success, ActiveSession? Session, string? FailureReason)> TryCreateSessionAsync(string peerFingerprint, string peerName = "RemotePeer", TimeSpan? ttl = null);
        ActiveSession? GetSession(string sessionId);

        ChannelAttachmentToken IssueChannelToken(string sessionId, ChannelType channel, TimeSpan? validFor = null);
        AttachmentStatus TryAttachChannel(ChannelAttachmentRequest request);
        AttachmentStatus TryAttachChannel(string tokenId, string sessionId, string peerFingerprint, ChannelType channel);
        bool TryAttachChannel(ChannelAttachmentRequest request, out string? failureReason);

        RemoteControlState RequestRemoteControl(string sessionId);
        bool TryAcquireControl(string sessionId, out string? failureReason);
        void ReleaseRemoteControl(string sessionId);
        void RevokeControl(string sessionId);
        string? GetActiveControllerSessionId();

        void CloseSession(string sessionId);
        void TerminateSession(string sessionId);
        void PruneExpired();

        bool IsPairingBlocked(string ipAddress);
        void RecordPairingAttempt(string ipAddress, bool success);
    }
}
