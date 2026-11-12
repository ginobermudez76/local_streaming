using System;

namespace LanScreenShare.Core.Security
{
    /// <summary>
    /// Value Object fuertemente tipado que representa el nombre amigable de un dispositivo o par en la red LAN.
    /// </summary>
    public readonly record struct DeviceName(string Value)
    {
        public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

        public static implicit operator string(DeviceName name) => name.Value;
        public static implicit operator DeviceName(string value) => new(value);
        public override string ToString() => Value ?? string.Empty;
    }
}

