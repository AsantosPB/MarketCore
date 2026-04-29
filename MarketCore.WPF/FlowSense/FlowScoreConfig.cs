using System;

namespace MarketCore.FlowSense
{
    /// <summary>
    /// Todos os parâmetros calibráveis do FlowScoreEngine.
    /// Instância única passada ao engine e à janela de config.
    /// </summary>
    public class FlowScoreConfig
    {
        // ════════════════════════════════════════════
        // GRUPO 1 — Pesos dos 4 componentes (somam 1.0)
        // ════════════════════════════════════════════
        public double WeightBrokerFlow    { get; set; } = 0.35;
        public double WeightFluxoDireto   { get; set; } = 0.25;
        public double WeightBook          { get; set; } = 0.20;
        public double WeightDetectores    { get; set; } = 0.20;

        // ════════════════════════════════════════════
        // GRUPO 2 — BrokerFlow
        // ════════════════════════════════════════════

        /// <summary>Multiplicador máximo de RVOL (ex: 2.0 = até dobrar o sinal)</summary>
        public double BrokerRvolMaxMultiplier { get; set; } = 2.0;

        /// <summary>Multiplicador de fase: Abertura</summary>
        public double PhaseMultiplierAbertura { get; set; } = 1.5;

        /// <summary>Multiplicador de fase: Leilão</summary>
        public double PhaseMultiplierLeilao   { get; set; } = 1.2;

        /// <summary>Multiplicador de fase: Meio</summary>
        public double PhaseMultiplierMeio     { get; set; } = 1.0;

        // ════════════════════════════════════════════
        // GRUPO 3 — FluxoDireto
        // ════════════════════════════════════════════

        /// <summary>Peso do delta score dentro do FluxoDireto (0–1)</summary>
        public double FluxoDeltaWeight { get; set; } = 0.60;

        /// <summary>Peso do CVD dentro do FluxoDireto (0–1) — complementar ao Delta</summary>
        public double FluxoCVDWeight   { get; set; } = 0.40;

        // ════════════════════════════════════════════
        // GRUPO 4 — Book
        // ════════════════════════════════════════════

        /// <summary>Peso da pressão bid/ask dentro do Book</summary>
        public double BookPressureWeight   { get; set; } = 0.40;

        /// <summary>Peso do desequilíbrio por níveis dentro do Book</summary>
        public double BookImbalanceWeight  { get; set; } = 0.30;

        /// <summary>Peso do Renewable dentro do Book</summary>
        public double BookRenewableWeight  { get; set; } = 0.20;

        /// <summary>Peso da distância VWAP dentro do Book</summary>
        public double BookVWAPWeight       { get; set; } = 0.10;

        /// <summary>Score fixo adicionado quando Renewable está ativo</summary>
        public double BookRenewableScore   { get; set; } = 50.0;

        // ════════════════════════════════════════════
        // GRUPO 5 — Detectores
        // ════════════════════════════════════════════

        /// <summary>Penalidade aplicada quando Spoof é detectado</summary>
        public double DetectorSpoofPenalty      { get; set; } = 50.0;

        /// <summary>Bônus aplicado quando Iceberg é detectado</summary>
        public double DetectorIcebergBonus      { get; set; } = 50.0;

        /// <summary>Penalidade aplicada quando Exaustão Renko é detectada</summary>
        public double DetectorExhaustionPenalty { get; set; } = 30.0;

        /// <summary>Penalidade aplicada quando Stop Hunt é detectado</summary>
        public double DetectorStopHuntPenalty   { get; set; } = 40.0;

        /// <summary>Limiar de CVD Divergence para ativar bônus/penalidade nos detectores</summary>
        public double DetectorCVDThreshold      { get; set; } = 50.0;

        /// <summary>Score adicionado/subtraído quando CVD Divergence ultrapassa o limiar</summary>
        public double DetectorCVDScore          { get; set; } = 20.0;

        // ════════════════════════════════════════════
        // Helpers
        // ════════════════════════════════════════════

        /// <summary>
        /// Normaliza os 4 pesos principais para que sempre somem 1.0.
        /// Chame após qualquer alteração nos pesos.
        /// </summary>
        public void NormalizeWeights()
        {
            double total = WeightBrokerFlow + WeightFluxoDireto + WeightBook + WeightDetectores;
            if (total <= 0) return;
            WeightBrokerFlow  /= total;
            WeightFluxoDireto /= total;
            WeightBook        /= total;
            WeightDetectores  /= total;
        }

        /// <summary>Retorna uma cópia dos valores atuais (para cancelar edições).</summary>
        public FlowScoreConfig Clone() => (FlowScoreConfig)MemberwiseClone();
    }
}
