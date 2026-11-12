using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LanScreenShare.Core.Security
{
    public interface IPairedDeviceStore
    {
        Task<bool> IsDevicePairedAsync(DeviceFingerprint fingerprint);
        Task<bool> IsDeviceRevokedAsync(DeviceFingerprint fingerprint);
        Task<IReadOnlyList<PairedDevice>> GetAllPairedDevicesAsync();
        Task AddPairedDeviceAsync(PairedDevice device);
        Task RevokeDeviceAsync(DeviceFingerprint fingerprint);
        Task RemoveDeviceAsync(DeviceFingerprint fingerprint);
    }
}

