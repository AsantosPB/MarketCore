using System;

namespace MarketCore.FlowSense
{
    /// <summary>
    /// Snapshot do FlowScore em um momento específico.
    /// Gravado a cada 1 segundo no WIN_flowscore.bin.
    /// </summary>
    public class FlowScoreSnapshot
    {
        public DateTime Timestamp       { get; set; }
        public double   Preco           { get; set; }
        public double   ScoreTotal      { get; set; }
        public double   BrokerFlow      { get; set; }
        public double   FluxoDireto     { get; set; }
        public double   Book            { get; set; }
        public double   Detectores      { get; set; }

        /// <summary>56 bytes por snapshot no arquivo binário.</summary>
        public const int TamanhoBytes = 56;
    }
}
