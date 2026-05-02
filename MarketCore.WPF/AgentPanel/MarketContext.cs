using System;
using System.Collections.Generic;

namespace MarketCore.AgentPanel
{
    public class MarketContext
    {
        public DateTime Timestamp       { get; set; } = DateTime.Now;
        public double   PrecoAtual      { get; set; }
        public double   FlowScore       { get; set; }

        // Book
        public double BookImbalance            { get; set; }
        public double SpreadAtual              { get; set; }
        public double SpreadMedio              { get; set; }
        public int    VolumeTotalBook          { get; set; }
        public bool   ThinMarket               { get; set; }
        public double ProximoSuporteLiquidez   { get; set; }
        public double ProximaResistenciaLiquidez { get; set; }

        // Tape & CVD
        public int CVDAceleracao5s   { get; set; }
        public int CVDAceleracao30s  { get; set; }
        public int CVDAceleracao5min { get; set; }
        public int AgressaoCompra60s { get; set; }
        public int AgressaoVenda60s  { get; set; }
        public int TickImbalance     { get; set; }

        // Players
        public List<BrokerInfo> TopCompradores  { get; set; } = new();
        public List<BrokerInfo> TopVendedores   { get; set; } = new();
        public int    MaxVolumeComprador         { get; set; }
        public int    MaxVolumeVendedor          { get; set; }
        public string BrokerDominante            { get; set; } = "";
        public double ConcentracaoFlow           { get; set; }

        // Correlações
        public double WSP_Preco       { get; set; }
        public double WSP_Variacao    { get; set; }
        public bool   WSP_Liderando   { get; set; }
        public double WDO_Preco       { get; set; }
        public double WDO_Variacao    { get; set; }
        public double WIN_Variacao    { get; set; }
        public double CorrelacaoWinWdo { get; set; }
        public double CorrelacaoWinWsp { get; set; }
        public int    LagWinWsp        { get; set; }
        public double GapWinWsp        { get; set; }

        // Contexto temporal
        public FaseSessao Fase          { get; set; }
        public double     RVOL          { get; set; }
        public string?    ProximoEvento { get; set; }

        // Histórico
        public List<double> UltimosPrecos { get; set; } = new();
        public List<int>    UltimosCVD   { get; set; } = new();

        // Flags de detectores nativos do FlowSense
        public bool IcebergDetectadoBid { get; set; }
        public bool IcebergDetectadoAsk { get; set; }
        public bool SpoofingDetectado   { get; set; }
    }

    public enum FaseSessao
    {
        Abertura, AltaLiquidez, Almoco, Americana, Fechamento, Meio
    }

    public enum PerfilBroker
    {
        Iniciador, Absorvedor, Iceberg, Noise
    }

    public class BrokerInfo
    {
        public string       Nome        { get; set; } = "";
        public int          Volume      { get; set; }
        public PerfilBroker Perfil      { get; set; }
        public double       PriceImpact { get; set; }
    }
}
