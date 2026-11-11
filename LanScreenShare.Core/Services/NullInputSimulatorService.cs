using System;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Core.Services
{
    /// <summary>
    /// Simulador de entrada multiplataforma/fallback para entornos sin acceso directo a la API de entrada de Windows (Linux, Android, macOS).
    /// Permite recibir y validar eventos de entrada remota sin generar excepciones de plataforma no soportada.
    /// </summary>
    public sealed class NullInputSimulatorService : IInputSimulatorService
    {
        public CaptureSource? CurrentTargetSource { get; private set; }
        public int SimulatedInputsCount { get; private set; }

        public void SetTargetSource(CaptureSource source)
        {
            CurrentTargetSource = source;
        }

        public void SimulateInput(RemoteInputMessage input)
        {
            if (input == null) return;
            SimulatedInputsCount++;
        }

        public void ReleaseInjectedInputs()
        {
        }

        public void Dispose()
        {
        }
    }
}

