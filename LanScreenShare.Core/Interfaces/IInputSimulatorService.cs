using System;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Core.Interfaces
{
    public interface IInputSimulatorService : IDisposable
    {
        void SimulateInput(RemoteInputMessage input);
    }
}
