using System;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace LanScreenShare.Core.Security
{
    public interface IIdentityKeyStorage
    {
        Task<X509Certificate2> GetOrCreateIdentityCertificateAsync();
        Task ResetIdentityAsync();
    }
}

