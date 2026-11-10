using System;

namespace LanScreenShare.Core.Models
{
    public sealed class FileTransferOffer
    {
        public string TransferId { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long FileSize { get; set; }
    }

    public sealed class FileTransferProgress
    {
        public string TransferId { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long BytesTransferred { get; set; }
        public long TotalBytes { get; set; }
        public double Percent => TotalBytes > 0 ? Math.Clamp((double)BytesTransferred / TotalBytes * 100.0, 0.0, 100.0) : 0.0;
        public double SpeedBytesPerSec { get; set; }
        public bool IsUpload { get; set; }
    }

    public sealed class FileTransferCompleted
    {
        public string TransferId { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public bool IsUpload { get; set; }
    }

    public sealed class FileTransferError
    {
        public string TransferId { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        public bool IsUpload { get; set; }
    }
}
