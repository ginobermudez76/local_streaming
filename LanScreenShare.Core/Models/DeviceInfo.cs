using System;

namespace LanScreenShare.Core.Models
{
    public class DeviceInfo
    {
        public string Name { get; set; } = string.Empty;
        public string IPAddress { get; set; } = string.Empty;
        public DateTime LastSeen { get; set; }
    }
}
