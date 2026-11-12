using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LanScreenShare.Core.Security
{
    public interface ISessionAuthority
    {
        string? ActiveControllerSessionId { get; }
        event EventHandler<string>? SessionTerminated;

        ActiveSession CreateSession(DeviceFingerprint peerFingerprint, DeviceName peerName, TimeSpan? ttl = null);
        Task<(bool Success, ActiveSession? Session, string? FailureReason)> TryCreateSessionAsync(DeviceFingerprint peerFingerprint, DeviceName peerName = default, TimeSpan? ttl = null);
        ActiveSession? GetSession(SessionId sessionId);

        ChannelAttachmentToken IssueChannelToken(SessionId sessionId, ChannelType channel, TimeSpan? validFor = null);
        AttachmentStatus TryAttachChannel(ChannelAttachmentRequest request);
        AttachmentStatus TryAttachChannel(string tokenId, SessionId sessionId, DeviceFingerprint peerFingerprint, ChannelType channel);
        bool TryAttachChannel(ChannelAttachmentRequest request, out string? failureReason);

        RemoteControlState RequestRemoteControl(SessionId sessionId);
        bool TryAcquireControl(SessionId sessionId, out string? failureReason);
        void ReleaseRemoteControl(SessionId sessionId);
        void RevokeControl(SessionId sessionId);
        string? GetActiveControllerSessionId();

        void CloseSession(SessionId sessionId);
        void TerminateSession(SessionId sessionId);
        void PruneExpired();

        bool IsPairingBlocked(string ipAddress);
        void RecordPairingAttempt(string ipAddress, bool success);
    }
}
