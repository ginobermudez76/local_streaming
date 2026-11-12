using System;
using System.Collections.Generic;

namespace LanScreenShare.Core.Security
{
    public sealed class ActiveSession
    {
        public SessionId SessionId { get; }
        public DeviceFingerprint PeerFingerprint { get; }
        public DeviceName PeerName { get; }
        public DateTime CreatedAt { get; }
        public DateTime ExpiresAt { get; set; }
        public SessionState State { get; set; }
        public bool VideoAuthorized { get; set; }
        public bool AudioAuthorized { get; set; }
        public RemoteControlState ControlState { get; set; }
        public HashSet<ChannelType> AttachedChannels { get; } = new();

        public ActiveSession(SessionId sessionId, DeviceFingerprint peerFingerprint, DeviceName peerName, DateTime expiresAt)
        {
            SessionId = sessionId;
            PeerFingerprint = peerFingerprint;
            PeerName = peerName;
            CreatedAt = DateTime.UtcNow;
            ExpiresAt = expiresAt;
            State = SessionState.Authenticated;
            ControlState = RemoteControlState.Disabled;
        }

        public bool IsExpired => DateTime.UtcNow > ExpiresAt;
    }
}

