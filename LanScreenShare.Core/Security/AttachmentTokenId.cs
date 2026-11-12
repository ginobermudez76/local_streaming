using System;

namespace LanScreenShare.Core.Security
{
    /// <summary>
    /// Value Object fuertemente tipado que encapsula un token de fijación de canal secundario.
    /// </summary>
    public readonly record struct AttachmentTokenId(string Value)
    {
        public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

        public static implicit operator string(AttachmentTokenId token) => token.Value;
        public static implicit operator AttachmentTokenId(string value) => new(value);
        public override string ToString() => Value ?? string.Empty;
    }
}

