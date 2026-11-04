using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LanScreenShare.Core.Security
{
    public interface IPairedDeviceStore
    {
        Task<bool> IsDevicePairedAsync(string fingerprint);
        Task<bool> IsDeviceRevokedAsync(string fingerprint);
        Task<IReadOnlyList<PairedDevice>> GetAllPairedDevicesAsync();
        Task AddPairedDeviceAsync(PairedDevice device);
        Task RevokeDeviceAsync(string fingerprint);
        Task RemoveDeviceAsync(string fingerprint);
    }
}

