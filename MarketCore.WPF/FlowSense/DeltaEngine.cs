using System;
using System.Collections.Generic;

namespace MarketCore.FlowSense
{
    /// <summary>
    /// DeltaEngine expandido com sinais novos:
    /// - CVD divergence: slope preço vs slope CVD
    /// - RVOL: volume relativo (vol atual vs média)
    /// - VWAP: volume weighted average price de sessão
    /// - Stop hunt: detecção de sweep + retorno
    /// - Session timing: multiplicador contextual (abertura/meio/fechamento)
    ///
    /// [PERF] v2: listas unbounded (_prices, _buyVolumes, _sellVolumes, _deltaValues)
    /// substituídas por ring buffers de tamanho fixo. Antes cresciam sem limite (~4M entries
    /// em 6h de pregão), causando resizes no LOH → GC Gen2 → pauses de 10-50ms que
    /// bloqueavam o TradeProcessingLoop. CalculateCVDDivergence só usa os últimos 5 entries;
    /// LINQ (.TakeLast(5).ToList(), .Select().ToList(), .Average()) eliminado — zero alocação
    /// por trade. _delta1min/_delta3min agora só guardam o último valor (era o único uso).
    /// </summary>
    public class DeltaEngine
    {
        /// <summary>
        /// Trades chegam na thread do provider; FlowScore/Agent lêem na UI -
        /// sem lock há exceção ao enumerar/modificar listas e valores incoerentes.
        /// </summary>
        private readonly object _sync = new();

        // Ring buffers de tamanho fixo — só os últimos 5 trades (tudo que CalculateCVDDivergence precisa).
        private const int RingSize = 5;
        private readonly double[] _recentPrices = new double[RingSize];
        private readonly int[] _recentDeltas = new int[RingSize];
        private int _ringIndex;  // próxima posição de escrita (mod RingSize)
        private int _ringCount;  // quantos foram escritos (capped em RingSize)

        // Acumuladores
        private long _cumulativeDelta = 0;
        private double _totalVolume = 0;
        private double _cumulativePriceVolume = 0; // para VWAP

        // Janelas temporais — antes eram List que cresciam sem limite dentro da janela.
        // Só o último valor era lido (.Last()). Substituídos por campo simples.
        private double _currentDelta1min;
        private double _currentDelta3min;
        private DateTime _last1minReset = DateTime.UtcNow;
        private DateTime _last3minReset = DateTime.UtcNow;

        // Stop hunt detection
        private double _sessionHigh = double.MinValue;
        private double _sessionLow = double.MaxValue;
        private int _barsAboveHigh = 0;
        private int _barsBelowLow = 0;

        // RVOL — ring buffer manual com soma corrente (elimina LINQ .Average())
        private const int RvolWindowSize = 20;
        private readonly double[] _rvolRing = new double[RvolWindowSize];
        private int _rvolIndex;
        private int _rvolCount;
        private double _rvolSum;

        // Campos espelhados - escritos só dentro de lock (_sync) em OnTrade
        private double _cvdDivergence;
        private double _rvol = 1;
        private double _sessionVWAP;
        private bool _stopHuntDetected;
        private SessionPhase _currentSessionPhase = SessionPhase.Meio;

        /// <summary>Lidas pelo FlowScoreEngine / Agent - thread-safe.</summary>
        public long CumulativeDelta           { get { lock (_sync) return _cumulativeDelta; } }
        public double CurrentDelta1min           { get { lock (_sync) return _currentDelta1min; } }
        public double CurrentDelta3min           { get { lock (_sync) return _currentDelta3min; } }
        public double CVDDivergence              { get { lock (_sync) return _cvdDivergence; } }
        public double RVOL                       { get { lock (_sync) return _rvol; } }
        public double SessionVWAP                { get { lock (_sync) return _sessionVWAP; } }
        public bool StopHuntDetected             { get { lock (_sync) return _stopHuntDetected; } }
        public SessionPhase CurrentSessionPhase  { get { lock (_sync) return _currentSessionPhase; } }

        public DeltaEngine()
        {
            ResetSession();
        }

        /// <summary>
        /// Processa um trade - atualiza delta, VWAP, janelas e detectores.
        /// Zero alocações de heap (nenhum LINQ, nenhum List resize).
        /// </summary>
        public void OnTrade(double price, double buyVolume, double sellVolume, DateTime timestamp)
        {
            lock (_sync)
            {
                double volume = buyVolume + sellVolume;
                int delta = (int)(buyVolume - sellVolume);

                // Ring buffer — sobrescreve o mais antigo
                _recentPrices[_ringIndex] = price;
                _recentDeltas[_ringIndex] = delta;
                _ringIndex = (_ringIndex + 1) % RingSize;
                if (_ringCount < RingSize) _ringCount++;

                _cumulativeDelta += delta;
                _totalVolume += volume;
                _cumulativePriceVolume += price * volume;

                _sessionVWAP = _totalVolume > 0 ? _cumulativePriceVolume / _totalVolume : price;

                if (price > _sessionHigh)
                    _sessionHigh = price;
                if (price < _sessionLow)
                    _sessionLow = price;

                // RVOL — ring buffer com soma corrente (zero alloc)
                if (_rvolCount >= RvolWindowSize)
                    _rvolSum -= _rvolRing[_rvolIndex]; // subtrai o que vai ser sobrescrito
                _rvolRing[_rvolIndex] = volume;
                _rvolSum += volume;
                _rvolIndex = (_rvolIndex + 1) % RvolWindowSize;
                if (_rvolCount < RvolWindowSize) _rvolCount++;
                _rvol = _rvolCount > 0 && _rvolSum > 0 ? volume / (_rvolSum / _rvolCount) : 1.0;

                UpdateTimeWindows(timestamp);
                CalculateCVDDivergence();
                DetectStopHunt(price);
                UpdateSessionPhase(timestamp);
            }
        }

        private void CalculateCVDDivergence()
        {
            if (_ringCount < RingSize)
            {
                _cvdDivergence = 0;
                return;
            }

            // Lê os últimos 5 do ring buffer na ordem cronológica (zero alloc).
            // _ringIndex aponta pro PRÓXIMO slot. O mais antigo é _ringIndex (que já
            // foi sobrescrito), o mais recente é (_ringIndex - 1 + RingSize) % RingSize.
            double priceSlope = CalculateSlopeFromRingDouble(_recentPrices);
            double deltaSlope = CalculateSlopeFromRingInt(_recentDeltas);

            if (Math.Abs(priceSlope) < 0.0001)
            {
                _cvdDivergence = 0;
                return;
            }

            _cvdDivergence = (deltaSlope / priceSlope) * 100;
            _cvdDivergence = Math.Max(-100, Math.Min(100, _cvdDivergence));
        }

        /// <summary>Linear regression slope sobre os últimos RingSize itens do ring buffer (double).</summary>
        private double CalculateSlopeFromRingDouble(double[] ring)
        {
            // n = RingSize = 5; sumX = 0+1+2+3+4 = 10; sumX2 = 0+1+4+9+16 = 30
            // denominator = 5*30 - 10*10 = 50 (constante, hardcoded)
            const double denom = 50.0;
            double sumY = 0, sumXY = 0;
            for (int i = 0; i < RingSize; i++)
            {
                double val = ring[(_ringIndex + i) % RingSize]; // cronológico: oldest → newest
                sumY += val;
                sumXY += i * val;
            }
            return (RingSize * sumXY - 10.0 * sumY) / denom;
        }

        /// <summary>Linear regression slope sobre os últimos RingSize itens do ring buffer (int).</summary>
        private double CalculateSlopeFromRingInt(int[] ring)
        {
            const double denom = 50.0;
            double sumY = 0, sumXY = 0;
            for (int i = 0; i < RingSize; i++)
            {
                double val = ring[(_ringIndex + i) % RingSize];
                sumY += val;
                sumXY += i * val;
            }
            return (RingSize * sumXY - 10.0 * sumY) / denom;
        }

        private void UpdateTimeWindows(DateTime timestamp)
        {
            // Delta 1min - reseta a cada minuto
            if ((timestamp - _last1minReset).TotalSeconds >= 60)
            {
                _last1minReset = timestamp;
            }
            _currentDelta1min = _cumulativeDelta;

            // Delta 3min - reseta a cada 3 minutos
            if ((timestamp - _last3minReset).TotalSeconds >= 180)
            {
                _last3minReset = timestamp;
            }
            _currentDelta3min = _cumulativeDelta;
        }

        private void DetectStopHunt(double price)
        {
            const double tolerance = 0.0001;
            const int confirmationBars = 2;

            if (Math.Abs(price - _sessionHigh) < tolerance)
            {
                _barsAboveHigh++;
                _barsBelowLow = 0;
            }
            else if (price < _sessionHigh)
            {
                _barsAboveHigh = 0;
            }

            if (Math.Abs(price - _sessionLow) < tolerance)
            {
                _barsBelowLow++;
                _barsAboveHigh = 0;
            }
            else if (price > _sessionLow)
            {
                _barsBelowLow = 0;
            }

            _stopHuntDetected = (_barsAboveHigh >= confirmationBars || _barsBelowLow >= confirmationBars);
        }

        private void UpdateSessionPhase(DateTime timestamp)
        {
            int hour = timestamp.Hour;
            int minute = timestamp.Minute;

            if (hour == 9)
                _currentSessionPhase = SessionPhase.Abertura;
            else if (hour == 16 && minute >= 0 && minute < 30)
                _currentSessionPhase = SessionPhase.Leilao;
            else if (hour >= 10 && hour < 16)
                _currentSessionPhase = SessionPhase.Meio;
            else
                _currentSessionPhase = SessionPhase.PosLeilao;
        }

        /// <summary>Zera VWAP, delta e janelas ao trocar o ativo monitorizado.</summary>
        public void ClearSessionState() => ResetSession();

        private void ResetSession()
        {
            lock (_sync)
            {
                Array.Clear(_recentPrices);
                Array.Clear(_recentDeltas);
                _ringIndex = 0;
                _ringCount = 0;

                Array.Clear(_rvolRing);
                _rvolIndex = 0;
                _rvolCount = 0;
                _rvolSum = 0;

                _barsAboveHigh = 0;
                _barsBelowLow = 0;
                _cumulativeDelta = 0;
                _totalVolume = 0;
                _cumulativePriceVolume = 0;
                _sessionHigh = double.MinValue;
                _sessionLow = double.MaxValue;
                _sessionVWAP = 0;
                _stopHuntDetected = false;
                _currentSessionPhase = SessionPhase.Meio;
                _cvdDivergence = 0;
                _rvol = 1;
                _currentDelta1min = 0;
                _currentDelta3min = 0;
                _last1minReset = DateTime.UtcNow;
                _last3minReset = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// [DEAD CODE] Nunca chamado no codebase. Mantido como stub para evitar quebra de compilação
        /// caso algum assembly externo referencie. Retorna 0 sempre — os dados per-trade não são mais
        /// mantidos em lista (ring buffer de 5 itens substituiu a lista unbounded).
        /// </summary>
        public long GetDeltaForRenkoBar(int barIndex)
        {
            return 0;
        }
    }

    public enum SessionPhase
    {
        PreMercado,
        Abertura,    // 9h-10h - maior volatilidade, peso x1.5 no FlowScore
        Meio,        // 10h-16h - normal
        Leilao,      // 16h-16h30 - fechamento, peso x1.2
        PosLeilao
    }
}
