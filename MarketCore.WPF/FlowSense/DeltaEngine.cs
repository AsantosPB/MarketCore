using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketCore.FlowSense
{
    /// <summary>
    /// DeltaEngine expandido com sinais novos:
    /// - CVD divergence: slope preço vs slope CVD
    /// - RVOL: volume relativo (vol atual vs média)
    /// - VWAP: volume weighted average price de sessão
    /// - Stop hunt: detecção de sweep + retorno
    /// - Session timing: multiplicador contextual (abertura/meio/fechamento)
    /// </summary>
    public class DeltaEngine
    {
        private List<double> _prices = new List<double>(1000);
        private List<double> _buyVolumes = new List<double>(1000);
        private List<double> _sellVolumes = new List<double>(1000);
        private List<int> _deltaValues = new List<int>(1000);

        // Acumuladores
        private long _cumulativeDelta = 0;
        private double _totalVolume = 0;
        private double _cumulativePriceVolume = 0; // para VWAP

        // Janelas temporais
        private List<double> _delta1min = new List<double>(60);
        private List<double> _delta3min = new List<double>(180);
        private DateTime _last1minReset = DateTime.UtcNow;
        private DateTime _last3minReset = DateTime.UtcNow;

        // Stop hunt detection
        private double _sessionHigh = double.MinValue;
        private double _sessionLow = double.MaxValue;
        private int _barsAboveHigh = 0;
        private int _barsBelowLow = 0;

        // RVOL e média de volume
        private Queue<double> _volumeHistory = new Queue<double>(100);
        private readonly int _rvolWindowSize = 20; // últimas 20 barras

        // Propriedades públicas — lidas pelo FlowScoreEngine
        public long CumulativeDelta { get { return _cumulativeDelta; } }
        public double CurrentDelta1min { get; private set; }
        public double CurrentDelta3min { get; private set; }
        public double CVDDivergence { get; private set; }      // slope preco vs slope delta
        public double RVOL { get; private set; }                // vol atual / media 20 bars
        public double SessionVWAP { get; private set; }         // preco medio ponderado por volume
        public bool StopHuntDetected { get; private set; }      // true se sweep + retorno ativo
        public SessionPhase CurrentSessionPhase { get; private set; } // abertura/meio/leilao

        public DeltaEngine()
        {
            ResetSession();
        }

        /// <summary>
        /// Processa um trade — atualiza delta, VWAP, janelas e detectores
        /// </summary>
        public void OnTrade(double price, double buyVolume, double sellVolume, DateTime timestamp)
        {
            double volume = buyVolume + sellVolume;
            int delta = (int)(buyVolume - sellVolume);

            _prices.Add(price);
            _buyVolumes.Add(buyVolume);
            _sellVolumes.Add(sellVolume);
            _deltaValues.Add(delta);

            // Atualiza acumulados
            _cumulativeDelta += delta;
            _totalVolume += volume;
            _cumulativePriceVolume += price * volume;

            // Atualiza VWAP
            SessionVWAP = _totalVolume > 0 ? _cumulativePriceVolume / _totalVolume : price;

            // Atualiza session high/low para stop hunt detection
            if (price > _sessionHigh)
                _sessionHigh = price;
            if (price < _sessionLow)
                _sessionLow = price;

            // Atualiza volume history para RVOL
            _volumeHistory.Enqueue(volume);
            if (_volumeHistory.Count > _rvolWindowSize)
                _volumeHistory.Dequeue();
            CalculateRVOL(volume);

            // Atualiza janelas 1min/3min
            UpdateTimeWindows(timestamp);

            // Calcula CVD divergence
            CalculateCVDDivergence();

            // Detecta stop hunt
            DetectStopHunt(price);

            // Atualiza session phase
            UpdateSessionPhase(timestamp);
        }

        private void CalculateRVOL(double currentVolume)
        {
            if (_volumeHistory.Count == 0)
            {
                RVOL = 1.0;
                return;
            }

            double avgVolume = _volumeHistory.Average();
            RVOL = avgVolume > 0 ? currentVolume / avgVolume : 1.0;
        }

        private void CalculateCVDDivergence()
        {
            if (_prices.Count < 5)
            {
                CVDDivergence = 0;
                return;
            }

            // Calcula slope do preço nos ultimos 5 trades
            var recentPrices = _prices.TakeLast(5).ToList();
            var recentDeltas = _deltaValues.TakeLast(5).ToList();

            double priceSlope = CalculateSlope(recentPrices);
            double deltaSlope = CalculateSlope(recentDeltas.Select(d => (double)d).ToList());

            // CVD divergence = quando preço sobe mas delta cai (ou vice versa)
            // Retorna valor de -100 (maxima divergencia vendedora) a +100 (maxima divergencia compradora)
            if (Math.Abs(priceSlope) < 0.0001)
            {
                CVDDivergence = 0;
                return;
            }

            CVDDivergence = (deltaSlope / priceSlope) * 100;
            CVDDivergence = Math.Max(-100, Math.Min(100, CVDDivergence)); // clamp
        }

        private double CalculateSlope(List<double> values)
        {
            if (values.Count < 2)
                return 0;

            // Linear regression slope
            int n = values.Count;
            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;

            for (int i = 0; i < n; i++)
            {
                sumX += i;
                sumY += values[i];
                sumXY += i * values[i];
                sumX2 += i * i;
            }

            double denominator = (n * sumX2) - (sumX * sumX);
            if (Math.Abs(denominator) < 0.0001)
                return 0;

            return ((n * sumXY) - (sumX * sumY)) / denominator;
        }

        private void UpdateTimeWindows(DateTime timestamp)
        {
            // Delta 1min — reseta a cada minuto
            if ((timestamp - _last1minReset).TotalSeconds >= 60)
            {
                _delta1min.Clear();
                _last1minReset = timestamp;
            }
            _delta1min.Add(_cumulativeDelta);
            CurrentDelta1min = _delta1min.Count > 0 ? _delta1min.Last() : 0;

            // Delta 3min — reseta a cada 3 minutos
            if ((timestamp - _last3minReset).TotalSeconds >= 180)
            {
                _delta3min.Clear();
                _last3minReset = timestamp;
            }
            _delta3min.Add(_cumulativeDelta);
            CurrentDelta3min = _delta3min.Count > 0 ? _delta3min.Last() : 0;
        }

        private void DetectStopHunt(double price)
        {
            // Stop hunt = rompimento de highs/lows seguido de retorno rapido com delta invertido
            // Simplificado: detecta quando preço bate o high/low da sessão + reverte em 2 trades
            
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

            // Se bateu o extremo e reverteu em 2 bars, ativa flag
            StopHuntDetected = (_barsAboveHigh >= confirmationBars || _barsBelowLow >= confirmationBars);
        }

        private void UpdateSessionPhase(DateTime timestamp)
        {
            // Simplificado: abertura (9h-10h), meio (10h-16h), leilao (16h-16h30)
            int hour = timestamp.Hour;
            int minute = timestamp.Minute;

            if (hour == 9)
                CurrentSessionPhase = SessionPhase.Abertura;
            else if (hour == 16 && minute >= 0 && minute < 30)
                CurrentSessionPhase = SessionPhase.Leilao;
            else if (hour >= 10 && hour < 16)
                CurrentSessionPhase = SessionPhase.Meio;
            else
                CurrentSessionPhase = SessionPhase.PosLeilao;
        }

        private void ResetSession()
        {
            _cumulativeDelta = 0;
            _totalVolume = 0;
            _cumulativePriceVolume = 0;
            _sessionHigh = double.MinValue;
            _sessionLow = double.MaxValue;
            SessionVWAP = 0;
            StopHuntDetected = false;
            CurrentSessionPhase = SessionPhase.Meio;
        }

        /// <summary>
        /// Retorna delta acumulado de uma barra Renko (em pontos)
        /// Para uso pelo FlowCandleChart
        /// </summary>
        public long GetDeltaForRenkoBar(int barIndex)
        {
            if (barIndex < 0 || barIndex >= _deltaValues.Count)
                return 0;

            return _deltaValues[barIndex];
        }
    }

    public enum SessionPhase
    {
        PreMercado,
        Abertura,    // 9h-10h — maior volatilidade, peso x1.5 no FlowScore
        Meio,        // 10h-16h — normal
        Leilao,      // 16h-16h30 — fechamento, peso x1.2
        PosLeilao
    }
}
