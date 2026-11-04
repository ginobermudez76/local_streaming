using System;

namespace LanScreenShare.Core.Security
{
    public enum SessionState
    {
        ProvisionalPairing,
        Authenticated,
        Active,
        Revoked,
        Terminated
    }

    public enum RemoteControlState
    {
        Disabled,
        PendingApproval,
        Granted,
        DeniedConflict,
        Revoked
    }

    public enum ChannelType
    {
        Video,
        Audio,
        RemoteControl
    }

    public enum AttachmentStatus
    {
        Success,
        SessionNotFound,
        TokenNotFound,
        TokenBindingMismatch,
        ChannelMismatch,
        TokenExpired,
        TokenAlreadyConsumed
    }
}
