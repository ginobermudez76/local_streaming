using System;
using System.Threading;

namespace LanScreenShare.Core.Services
{
    public enum AvSyncAction
    {
        PlayImmediately = 0,
        DropStaleAudio = 1,
        DelayAudio = 2
    }

    /// <summary>
    /// Controlador de sincronización Audio/Video (A/V Sync) con alineación de marcas de tiempo en milisegundos.
    /// Detecta deriva (drift), descarta paquetes de audio obsoletos cuando hay desfase tardío,
    /// y proporciona telemetría en tiempo real de desfase A/V para la interfaz gráfica.
    /// </summary>
    public sealed class AudioVideoSyncController
    {
        private long _latestVideoTimestamp;
        private long _latestVideoReceiveTime;
        private long _latestAudioTimestamp;
        private int _smoothedDriftMs;
        private readonly object _lock = new();

        /// <summary>
        /// Umbral máximo en ms para considerar que el audio está retrasado respecto al video.
        /// Audio con desfase mayor a este umbral se descarta para evitar retraso acumulativo.
        /// </summary>
        public int MaxLagThresholdMs { get; set; } = 80;

        /// <summary>
        /// Umbral en ms para considerar que el audio va adelantado respecto al video.
        /// </summary>
        public int MaxLeadThresholdMs { get; set; } = 60;

        /// <summary>
        /// Desfase actual en milisegundos: (AudioTimestamp - VideoTimestamp).
        /// Positivo = Audio adelantado al video.
        /// Negativo = Audio retrasado respecto al video.
        /// </summary>
        public int CurrentDriftMs
        {
            get
            {
                lock (_lock) { return _smoothedDriftMs; }
            }
        }

        public long LatestVideoTimestamp => Interlocked.Read(ref _latestVideoTimestamp);
        public long LatestAudioTimestamp => Interlocked.Read(ref _latestAudioTimestamp);

        public void OnVideoFrameReceived(long videoTimestamp)
        {
            if (videoTimestamp <= 0)
                return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Interlocked.Exchange(ref _latestVideoTimestamp, videoTimestamp);
            Interlocked.Exchange(ref _latestVideoReceiveTime, now);

            lock (_lock)
            {
                if (_latestAudioTimestamp > 0)
                {
                    UpdateSmoothedDrift((int)(_latestAudioTimestamp - videoTimestamp));
                }
            }
        }

        public AvSyncAction ProcessAudioChunk(long audioTimestamp, out int delayMs)
        {
            delayMs = 0;
            if (audioTimestamp <= 0)
                return AvSyncAction.PlayImmediately;

            Interlocked.Exchange(ref _latestAudioTimestamp, audioTimestamp);

            long vidTs = LatestVideoTimestamp;
            if (vidTs <= 0)
            {
                // Aún no hay fotogramas de video recibidos; reproducir normalmente
                return AvSyncAction.PlayImmediately;
            }

            // Compensar periodos de pantalla estática donde DXGI no emite fotogramas redundantes
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long vidRecv = Interlocked.Read(ref _latestVideoReceiveTime);
            long elapsedSinceVideo = vidRecv > 0 ? Math.Max(0, now - vidRecv) : 0;
            long effectiveVidTs = vidTs + elapsedSinceVideo;

            int drift = (int)(audioTimestamp - effectiveVidTs);

            lock (_lock)
            {
                UpdateSmoothedDrift(drift);
            }

            // Audio con más de MaxLagThresholdMs de retraso respecto al video -> Descartar para recuperar sincronía
            if (drift < -MaxLagThresholdMs)
            {
                return AvSyncAction.DropStaleAudio;
            }

            // Audio con más de MaxLeadThresholdMs de adelanto -> Indicar micro-demora
            if (drift > MaxLeadThresholdMs)
            {
                delayMs = Math.Min(drift, 100);
                return AvSyncAction.DelayAudio;
            }

            return AvSyncAction.PlayImmediately;
        }

        private void UpdateSmoothedDrift(int sampleDrift)
        {
            // Filtro EMA (Exponential Moving Average) con peso 80/20
            _smoothedDriftMs = _smoothedDriftMs == 0
                ? sampleDrift
                : (int)(_smoothedDriftMs * 0.8 + sampleDrift * 0.2);
        }

        public void Reset()
        {
            Interlocked.Exchange(ref _latestVideoTimestamp, 0);
            Interlocked.Exchange(ref _latestVideoReceiveTime, 0);
            Interlocked.Exchange(ref _latestAudioTimestamp, 0);
            lock (_lock)
            {
                _smoothedDriftMs = 0;
            }
        }
    }
}
