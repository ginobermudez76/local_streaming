using System;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Core.Services
{
    public enum AbrNetworkState
    {
        Optimal = 0,
        Moderate = 1,
        Congested = 2,
        SevereCongestion = 3
    }

    /// <summary>
    /// Controlador de Ajuste Automático de Bitrate (ABR - Adaptive Bitrate Controller).
    /// Monitorea dinámicamente RTT, fluctuaciones de latencia, fotogramas descartados (en emisor y receptor)
    /// y cadencia de FPS para escalar dinámicamente el bitrate y la tasa de refresco del codificador H.264
    /// en tiempo real, garantizando la menor latencia y evitando el bufferbloat en redes LAN / Wi-Fi.
    /// </summary>
    public sealed class AdaptiveBitrateController
    {
        private readonly object _lock = new();
        private readonly Func<long> _timeProvider;

        private int _currentBitrateMbps;
        private int _minBitrateMbps = 2;
        private int _maxBitrateMbps = 25;

        private int _targetFps = 60;
        private int _baseFps = 60;
        private int _minFps = 30;

        private AbrNetworkState _currentState = AbrNetworkState.Optimal;
        private long _lastAdjustmentTimeMs;
        private long _lastDropTimeMs;
        private long _lastObservedDroppedFrames;
        private int _baselineRttMs = 15;
        private string _lastAdjustmentReason = "Inicial";

        // Umbrales de RTT adaptados para streaming en red local (LAN / Wi-Fi 5-6GHz)
        public int RttThresholdOptimalMs { get; set; } = 25;
        public int RttThresholdCongestionMs { get; set; } = 50;
        public int RttThresholdSevereMs { get; set; } = 90;

        // Intervalos de enfriamiento (cooldown) y muestreo
        public int IncreaseCooldownMs { get; set; } = 2000;
        public int DecreaseCooldownMs { get; set; } = 700;

        public bool IsEnabled { get; set; } = true;

        public int CurrentBitrateMbps
        {
            get { lock (_lock) { return _currentBitrateMbps; } }
            set
            {
                lock (_lock)
                {
                    int clamped = Math.Clamp(value, _minBitrateMbps, _maxBitrateMbps);
                    if (_currentBitrateMbps != clamped)
                    {
                        _currentBitrateMbps = clamped;
                        BitrateChanged?.Invoke(_currentBitrateMbps, "Manual");
                    }
                }
            }
        }

        public int MinBitrateMbps
        {
            get { lock (_lock) { return _minBitrateMbps; } }
            set
            {
                lock (_lock)
                {
                    _minBitrateMbps = Math.Max(1, value);
                    if (_maxBitrateMbps < _minBitrateMbps)
                        _maxBitrateMbps = _minBitrateMbps;
                    if (_currentBitrateMbps < _minBitrateMbps)
                        ApplyBitrate(_minBitrateMbps, "Ajuste de límite inferior");
                }
            }
        }

        public int MaxBitrateMbps
        {
            get { lock (_lock) { return _maxBitrateMbps; } }
            set
            {
                lock (_lock)
                {
                    _maxBitrateMbps = Math.Max(_minBitrateMbps, value);
                    if (_currentBitrateMbps > _maxBitrateMbps)
                        ApplyBitrate(_maxBitrateMbps, "Ajuste de límite superior");
                }
            }
        }

        public int TargetFps
        {
            get { lock (_lock) { return _targetFps; } }
            private set
            {
                int clamped = Math.Clamp(value, _minFps, _baseFps);
                if (_targetFps != clamped)
                {
                    _targetFps = clamped;
                    FpsChanged?.Invoke(_targetFps, _lastAdjustmentReason);
                }
            }
        }

        public int BaseFps
        {
            get { lock (_lock) { return _baseFps; } }
            set
            {
                lock (_lock)
                {
                    _baseFps = Math.Max(_minFps, value);
                    if (_targetFps > _baseFps)
                        TargetFps = _baseFps;
                }
            }
        }

        public int MinFps
        {
            get { lock (_lock) { return _minFps; } }
            set
            {
                lock (_lock)
                {
                    _minFps = Math.Clamp(value, 15, _baseFps);
                }
            }
        }

        public AbrNetworkState CurrentState
        {
            get { lock (_lock) { return _currentState; } }
            private set
            {
                if (_currentState != value)
                {
                    _currentState = value;
                    StateChanged?.Invoke(_currentState, _lastAdjustmentReason);
                }
            }
        }

        public string LastAdjustmentReason
        {
            get { lock (_lock) { return _lastAdjustmentReason; } }
        }

        public event Action<int, string>? BitrateChanged;
        public event Action<int, string>? FpsChanged;
        public event Action? KeyframeRequested;
        public event Action<AbrNetworkState, string>? StateChanged;

        public AdaptiveBitrateController(int initialBitrateMbps = 15, int baseFps = 60, Func<long>? timeProvider = null)
        {
            _timeProvider = timeProvider ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _currentBitrateMbps = initialBitrateMbps;
            _baseFps = baseFps;
            _targetFps = baseFps;
            _lastAdjustmentTimeMs = _timeProvider();
        }

        /// <summary>
        /// Reinicia el controlador al estado inicial especificado.
        /// </summary>
        public void Reset(int initialBitrateMbps = 15, int baseFps = 60)
        {
            lock (_lock)
            {
                _baseFps = Math.Max(15, baseFps);
                _targetFps = _baseFps;
                _currentBitrateMbps = Math.Clamp(initialBitrateMbps, _minBitrateMbps, _maxBitrateMbps);
                _currentState = AbrNetworkState.Optimal;
                _lastAdjustmentTimeMs = _timeProvider();
                _lastDropTimeMs = 0;
                _lastObservedDroppedFrames = 0;
                _baselineRttMs = 15;
                _lastAdjustmentReason = "Reinicio";
            }
        }

        /// <summary>
        /// Registra un descarte inmediato en la cola de salida del servidor TCP (host frame drop).
        /// Reacciona inmediatamente aplicando decremento multiplicativo de emergencia.
        /// </summary>
        public void RegisterHostDrop()
        {
            if (!IsEnabled) return;

            lock (_lock)
            {
                long now = _timeProvider();
                _lastDropTimeMs = now;
                HandleSevereCongestion("Descarte inmediato en cola del emisor", now);
            }
        }

        /// <summary>
        /// Analiza periódicamente las métricas de telemetría recibidas desde el receptor.
        /// </summary>
        public void RegisterTelemetry(StreamTelemetry telemetry)
        {
            if (telemetry == null || !IsEnabled) return;

            lock (_lock)
            {
                long now = _timeProvider();

                // 1. Detección de incrementos en fotogramas descartados
                long currentDrops = telemetry.DroppedFrames;
                long dropsDelta = Math.Max(0, currentDrops - _lastObservedDroppedFrames);
                _lastObservedDroppedFrames = currentDrops;

                if (dropsDelta > 0)
                {
                    _lastDropTimeMs = now;
                    HandleSevereCongestion($"Receptor reportó {dropsDelta} fotograma(s) descartado(s)", now);
                    return;
                }

                // 2. Si hubo un drop muy reciente, respetar ventana de estabilidad
                if (now - _lastDropTimeMs < DecreaseCooldownMs)
                {
                    return;
                }

                int rtt = telemetry.RttMs;

                // Actualizar baseline RTT cuando la red está estable y no congestionada
                if (rtt > 0 && rtt < _baselineRttMs)
                {
                    _baselineRttMs = rtt;
                }

                // 3. Evaluar congestión severa por RTT
                if (rtt >= RttThresholdSevereMs)
                {
                    HandleSevereCongestion($"RTT crítico ({rtt} ms >= {RttThresholdSevereMs} ms)", now);
                    return;
                }

                // 4. Evaluar congestión moderada
                if (IsModerateCongestion(rtt))
                {
                    HandleModerateCongestion($"RTT elevado ({rtt} ms)", now);
                    return;
                }

                // 5. Evaluar condiciones óptimas para incremento aditivo
                if (rtt < RttThresholdOptimalMs && now - _lastAdjustmentTimeMs >= IncreaseCooldownMs)
                {
                    HandleOptimalNetwork(telemetry, now);
                }
            }
        }

        private bool IsModerateCongestion(int rtt)
        {
            if (rtt >= RttThresholdCongestionMs)
                return true;

            return rtt > 30 && rtt > _baselineRttMs + 20;
        }

        private void HandleSevereCongestion(string reason, long now)
        {
            if (now - _lastAdjustmentTimeMs < DecreaseCooldownMs && _currentState == AbrNetworkState.SevereCongestion)
            {
                return;
            }

            CurrentState = AbrNetworkState.SevereCongestion;
            _lastAdjustmentReason = reason;
            _lastAdjustmentTimeMs = now;

            // Reducción multiplicativa agresiva (30% - 40%)
            int newBitrate = Math.Max(_minBitrateMbps, (int)Math.Round(_currentBitrateMbps * 0.65));
            if (newBitrate == _currentBitrateMbps && _currentBitrateMbps > _minBitrateMbps)
            {
                newBitrate = _currentBitrateMbps - 1;
            }

            ApplyBitrate(newBitrate, reason);

            // Si el bitrate ya está en el mínimo y la congestión continúa, reducir FPS de forma defensiva
            if (_currentBitrateMbps <= _minBitrateMbps && _targetFps > _minFps)
            {
                int newFps = Math.Max(_minFps, _targetFps - 15);
                TargetFps = newFps;
            }

            // Solicitar Keyframe (IDR) para limpiar artefactos de compresión en el receptor
            KeyframeRequested?.Invoke();
        }

        private void HandleModerateCongestion(string reason, long now)
        {
            if (now - _lastAdjustmentTimeMs < DecreaseCooldownMs)
            {
                return;
            }

            CurrentState = AbrNetworkState.Congested;
            _lastAdjustmentReason = reason;
            _lastAdjustmentTimeMs = now;

            // Reducción preventiva suave (15% o al menos 1 Mbps)
            int newBitrate = Math.Max(_minBitrateMbps, (int)Math.Round(_currentBitrateMbps * 0.85));
            if (newBitrate == _currentBitrateMbps && _currentBitrateMbps > _minBitrateMbps)
            {
                newBitrate = _currentBitrateMbps - 1;
            }

            ApplyBitrate(newBitrate, reason);
        }

        private void HandleOptimalNetwork(StreamTelemetry telemetry, long now)
        {
            CurrentState = AbrNetworkState.Optimal;

            // 1. Primero restaurar FPS base si fue reducido previamente
            if (_targetFps < _baseFps)
            {
                TargetFps = Math.Min(_baseFps, _targetFps + 15);
                _lastAdjustmentReason = "Restaurando FPS en red óptima";
                _lastAdjustmentTimeMs = now;
                return;
            }

            // 2. Si FPS está completo y aún estamos por debajo del bitrate máximo, incremento aditivo (+1 Mbps o +2 Mbps si muy bajo)
            if (_currentBitrateMbps < _maxBitrateMbps)
            {
                int step = _currentBitrateMbps < 8 ? 2 : 1;
                int newBitrate = Math.Min(_maxBitrateMbps, _currentBitrateMbps + step);
                _lastAdjustmentReason = $"Incremento aditivo (RTT {telemetry.RttMs} ms < {RttThresholdOptimalMs} ms)";
                _lastAdjustmentTimeMs = now;
                ApplyBitrate(newBitrate, _lastAdjustmentReason);
            }
        }

        private void ApplyBitrate(int newBitrate, string reason)
        {
            int clamped = Math.Clamp(newBitrate, _minBitrateMbps, _maxBitrateMbps);
            if (_currentBitrateMbps != clamped)
            {
                _currentBitrateMbps = clamped;
                _lastAdjustmentReason = reason;
                BitrateChanged?.Invoke(_currentBitrateMbps, reason);
            }
        }
    }
}
