using System;

namespace LanScreenShare.Core.Security
{
    public sealed record PairedDevice(
        string DeviceId,
        string DeviceName,
        string PublicKeyFingerprint,
        DateTime PairedAt,
        DateTime LastSeenAt,
        bool IsRevoked = false
    );
}

