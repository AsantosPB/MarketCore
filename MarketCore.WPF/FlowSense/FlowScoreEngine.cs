using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketCore.FlowSense
{
    /// <summary>
    /// FlowScoreEngine — calcula score direcional -100 a +100
    /// Todos os pesos e parâmetros são lidos de FlowScoreConfig,
    /// permitindo calibragem em tempo real via popup de configuração.
    /// </summary>
    public class FlowScoreEngine
    {
        private readonly BrokerAccumulator _brokerAccum;
        private readonly DeltaEngine       _deltaEngine;
        private readonly BookAnalyzer      _bookAnalyzer;
        private readonly DetectorAggregator _detectors;

        /// <summary>Configuração ativa — pode ser alterada em tempo real via popup.</summary>
        public FlowScoreConfig Config { get; }

        public double FlowScore              { get; private set; }
        public double BrokerFlowComponent    { get; private set; }
        public double FluxoDirectoComponent  { get; private set; }
        public double BookComponent          { get; private set; }
        public double DetectoresComponent    { get; private set; }

        public FlowScoreEngine(
            BrokerAccumulator   brokerAccum,
            DeltaEngine         deltaEngine,
            BookAnalyzer        bookAnalyzer,
            DetectorAggregator  detectors,
            FlowScoreConfig?    config = null)
        {
            _brokerAccum  = brokerAccum;
            _deltaEngine  = deltaEngine;
            _bookAnalyzer = bookAnalyzer;
            _detectors    = detectors;
            Config        = config ?? new FlowScoreConfig();
        }

        /// <summary>
        /// Calcula o score combinando os 4 grupos.
        /// Chamado a cada novo trade (ou via timer para evitar overhead).
        /// </summary>
        public void CalculateScore()
        {
            BrokerFlowComponent   = CalculateBrokerFlowScore();
            FluxoDirectoComponent = CalculateFluxoDirectoScore();
            BookComponent         = CalculateBookScore();
            DetectoresComponent   = CalculateDetectoresScore();

            FlowScore =
                (BrokerFlowComponent   * Config.WeightBrokerFlow)  +
                (FluxoDirectoComponent * Config.WeightFluxoDireto)  +
                (BookComponent         * Config.WeightBook)         +
                (DetectoresComponent   * Config.WeightDetectores);

            FlowScore = Math.Max(-100, Math.Min(100, FlowScore));
        }

        // ══════════════════════════════════════════════════════
        // GRUPO 1 — BrokerFlow (peso configurável)
        // ══════════════════════════════════════════════════════
        private double CalculateBrokerFlowScore()
        {
            var activeBuyers  = _brokerAccum.GetActiveBuyers60s();
            var activeSellers = _brokerAccum.GetActiveSellers60s();

            double buySignal  = activeBuyers.Sum(b => b.ActiveBuyVol60s);
            double sellSignal = activeSellers.Sum(b => b.ActiveSellVol60s);

            double brokerScore = 0;
            if (buySignal + sellSignal > 0)
                brokerScore = ((buySignal - sellSignal) / (buySignal + sellSignal)) * 100;

            // Amplifica com RVOL — cap definido em Config
            double rvolMultiplier = Math.Min(Config.BrokerRvolMaxMultiplier, _deltaEngine.RVOL);
            brokerScore *= rvolMultiplier;

            // Amplifica com SessionPhase
            double phaseMultiplier = GetPhaseMultiplier(_deltaEngine.CurrentSessionPhase);
            brokerScore *= phaseMultiplier;

            return Math.Max(-100, Math.Min(100, brokerScore));
        }

        // ══════════════════════════════════════════════════════
        // GRUPO 2 — FluxoDireto (peso configurável)
        // ══════════════════════════════════════════════════════
        private double CalculateFluxoDirectoScore()
        {
            double delta1min = _deltaEngine.CurrentDelta1min;
            double delta3min = _deltaEngine.CurrentDelta3min;

            double deltaScore = 0;
            if (Math.Abs(delta1min) + Math.Abs(delta3min) > 0)
                deltaScore = ((delta3min - delta1min) / (Math.Abs(delta3min) + Math.Abs(delta1min))) * 100;

            double cvdScore   = _deltaEngine.CVDDivergence;

            double fluxoScore = (deltaScore * Config.FluxoDeltaWeight) +
                                (cvdScore   * Config.FluxoCVDWeight);

            return Math.Max(-100, Math.Min(100, fluxoScore));
        }

        // ══════════════════════════════════════════════════════
        // GRUPO 3 — Book (peso configurável)
        // ══════════════════════════════════════════════════════
        private double CalculateBookScore()
        {
            double pressureScore  = _bookAnalyzer.GetBidAskPressure() * 100;
            double imbalanceScore = _bookAnalyzer.GetLevelImbalance()  * 100;
            double renewableScore = _bookAnalyzer.IsRenewableActive()  ? Config.BookRenewableScore : 0;
            double vwapDistance   = _bookAnalyzer.GetVWAPDistance();
            double vwapScore      = -vwapDistance * 100;

            double bookScore =
                (pressureScore  * Config.BookPressureWeight)  +
                (imbalanceScore * Config.BookImbalanceWeight)  +
                (renewableScore * Config.BookRenewableWeight)  +
                (vwapScore      * Config.BookVWAPWeight);

            return Math.Max(-100, Math.Min(100, bookScore));
        }

        // ══════════════════════════════════════════════════════
        // GRUPO 4 — Detectores (peso configurável)
        // ══════════════════════════════════════════════════════
        private double CalculateDetectoresScore()
        {
            double detectScore = 0;

            if (_detectors.IsSpoofDetected())
                detectScore -= Config.DetectorSpoofPenalty;

            if (_detectors.IsIcebergDetected())
                detectScore += Config.DetectorIcebergBonus;

            if (_detectors.IsExhaustionDetected())
                detectScore -= Config.DetectorExhaustionPenalty;

            if (_deltaEngine.StopHuntDetected)
                detectScore -= Config.DetectorStopHuntPenalty;

            if (Math.Abs(_deltaEngine.CVDDivergence) > Config.DetectorCVDThreshold)
                detectScore += (_deltaEngine.CVDDivergence > 0
                    ? Config.DetectorCVDScore
                    : -Config.DetectorCVDScore);

            return Math.Max(-100, Math.Min(100, detectScore));
        }

        // ══════════════════════════════════════════════════════
        // Helper
        // ══════════════════════════════════════════════════════
        private double GetPhaseMultiplier(SessionPhase phase) => phase switch
        {
            SessionPhase.Abertura  => Config.PhaseMultiplierAbertura,
            SessionPhase.Leilao    => Config.PhaseMultiplierLeilao,
            SessionPhase.Meio      => Config.PhaseMultiplierMeio,
            _                      => 0.8
        };
    }
}
