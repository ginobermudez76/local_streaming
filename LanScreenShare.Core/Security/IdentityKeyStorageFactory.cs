using System;

namespace LanScreenShare.Core.Security
{
    public static class IdentityKeyStorageFactory
    {
        public static IIdentityKeyStorage Create(string? customFilePath = null)
        {
            if (OperatingSystem.IsWindows())
            {
                return new WindowsDpapiIdentityKeyStorage(customFilePath);
            }

            return new CrossPlatformIdentityKeyStorage(customFilePath);
        }
    }
}

