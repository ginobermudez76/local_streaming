using System;

namespace LanScreenShare.Core.Security
{
    /// <summary>
    /// Value Object fuertemente tipado que representa la dirección IP o endpoint de un cliente remoto.
    /// </summary>
    public readonly record struct ClientIpAddress(string Value)
    {
        public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

        public static implicit operator string(ClientIpAddress ip) => ip.Value;
        public static implicit operator ClientIpAddress(string value) => new(value);
        public override string ToString() => Value ?? string.Empty;
    }
}

