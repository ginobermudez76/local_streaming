using System;

namespace LanScreenShare.Core.Security
{
    /// <summary>
    /// Value Object fuertemente tipado que encapsula una huella criptográfica de certificado X.509 (SHA-256).
    /// </summary>
    public readonly record struct DeviceFingerprint(string Value)
    {
        public bool IsEmpty => string.IsNullOrEmpty(Value);

        public static implicit operator string(DeviceFingerprint fingerprint) => fingerprint.Value;
        public static implicit operator DeviceFingerprint(string value) => new(value);
        public override string ToString() => Value ?? string.Empty;
    }
}

