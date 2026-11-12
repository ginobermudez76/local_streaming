using System;

namespace LanScreenShare.Core.Security
{
    /// <summary>
    /// Value Object fuertemente tipado que representa el identificador único de una sesión activa (GUID v4).
    /// </summary>
    public readonly record struct SessionId(string Value)
    {
        public bool IsEmpty => string.IsNullOrEmpty(Value);

        public static SessionId New() => new(Guid.NewGuid().ToString("D"));
        public static implicit operator string(SessionId id) => id.Value;
        public static implicit operator SessionId(string value) => new(value);
        public override string ToString() => Value ?? string.Empty;
    }
}

