using System;
using System.Collections.Generic;
using System.Linq;
using MarketCore.FlowSense;

namespace MarketCore.AgentPanel
{
    /// <summary>
    /// AgentBridge — ponte entre FlowSenseEngine e AgentViewModel
    /// Fica na pasta AgentPanel mas usa tipos do FlowSense via using
    /// </summary>
    public class AgentBridge
    {
        private readonly AgentViewModel     _agentViewModel;
        private readonly FlowScoreEngine    _flowScoreEngine;
        private readonly BrokerAccumulator  _brokerAccum;
        private readonly DeltaEngine        _deltaEngine;
        private readonly BookAnalyzer       _bookAnalyzer;
        private readonly DetectorAggregator _detectors;

        private readonly Queue<double> _historicoPrecos = new(10);
        private readonly Queue<int>    _historicoCVD    = new(10);

        // Alimentados pelo FlowScorePanel via AtualizarCorrelacoes()
        public double  PrecoAtual        { get; set; }
        public double  WSP_Preco         { get; set; }
        public double  WSP_Variacao      { get; set; }
        public double  WDO_Preco         { get; set; }
        public double  WDO_Variacao      { get; set; }
        public double  WIN_Variacao      { get; set; }
        public double  CorrelWinWdo      { get; set; } = -0.75;
        public double  CorrelWinWsp      { get; set; } = 0.65;
        public int     LagWinWsp         { get; set; } = 20;
        public double  GapWinWsp         { get; set; }
        public bool    WSP_Liderando     { get; set; } = true;
        public double  ProximoSuporte    { get; set; }
        public double  ProximaResistencia{ get; set; }
        public string? ProximoEvento     { get; set; }

        public AgentBridge(
            AgentViewModel     agentViewModel,
            FlowScoreEngine    flowScoreEngine,
            BrokerAccumulator  brokerAccum,
            DeltaEngine        deltaEngine,
            BookAnalyzer       bookAnalyzer,
            DetectorAggregator detectors)
        {
            _agentViewModel  = agentViewModel;
            _flowScoreEngine = flowScoreEngine;
            _brokerAccum     = brokerAccum;
            _deltaEngine     = deltaEngine;
            _bookAnalyzer    = bookAnalyzer;
            _detectors       = detectors;
        }

        public void Atualizar()
        {
            try
            {
                AtualizarHistorico();
                _agentViewModel.OnMarketUpdate(ConstruirContexto());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AgentBridge] {ex.Message}");
            }
        }

        private MarketContext ConstruirContexto()
        {
            var buyers  = _brokerAccum.GetActiveBuyers60s();
            var sellers = _brokerAccum.GetActiveSellers60s();

            var topCompradores = buyers.Take(5).Select(b => new BrokerInfo
            {
                Nome   = b.BrokerName,
                Volume = (int)b.ActiveBuyVol60s,
                Perfil = ClassificarPerfil(b.BrokerName)
            }).ToList();

            var topVendedores = sellers.Take(5).Select(b => new BrokerInfo
            {
                Nome   = b.BrokerName,
                Volume = (int)b.ActiveSellVol60s,
                Perfil = ClassificarPerfil(b.BrokerName)
            }).ToList();

            var volTotalCompra = topCompradores.Sum(b => b.Volume);
            var volTotalVenda  = topVendedores.Sum(b => b.Volume);
            var top2           = topCompradores.Take(2).Sum(b => b.Volume) + topVendedores.Take(2).Sum(b => b.Volume);
            var concentracao   = (volTotalCompra + volTotalVenda) > 0
                ? top2 / (double)(volTotalCompra + volTotalVenda) : 0;

            var agressaoCompra = (int)buyers.Sum(b => b.ActiveBuyVol60s);
            var agressaoVenda  = (int)sellers.Sum(b => b.ActiveSellVol60s);

            // Normaliza bookImbalance de [-1,1] para [0,1]
            var bookImbalance = (_bookAnalyzer.GetBidAskPressure() + 1) / 2;

            return new MarketContext
            {
                Timestamp    = DateTime.Now,
                PrecoAtual   = PrecoAtual,
                FlowScore    = _flowScoreEngine.FlowScore / 100.0,

                BookImbalance             = Math.Clamp(bookImbalance, 0, 1),
                ThinMarket                = _bookAnalyzer.GetLevelImbalance() < 0.1,
                ProximoSuporteLiquidez    = ProximoSuporte    > 0 ? ProximoSuporte    : PrecoAtual - 10,
                ProximaResistenciaLiquidez= ProximaResistencia > 0 ? ProximaResistencia : PrecoAtual + 10,

                CVDAceleracao5s   = (int)(_deltaEngine.CVDDivergence * 0.1),
                CVDAceleracao30s  = (int)(_deltaEngine.CurrentDelta1min * 0.05),
                CVDAceleracao5min = (int)(_deltaEngine.CurrentDelta3min * 0.02),
                AgressaoCompra60s = agressaoCompra,
                AgressaoVenda60s  = agressaoVenda,
                TickImbalance     = Math.Clamp(agressaoCompra - agressaoVenda, -35, 35),

                TopCompradores     = topCompradores,
                TopVendedores      = topVendedores,
                MaxVolumeComprador = topCompradores.Any() ? topCompradores.Max(b => b.Volume) : 1,
                MaxVolumeVendedor  = topVendedores.Any()  ? topVendedores.Max(b => b.Volume)  : 1,
                BrokerDominante    = topCompradores.FirstOrDefault()?.Nome ?? "",
                ConcentracaoFlow   = concentracao,

                WSP_Preco        = WSP_Preco,
                WSP_Variacao     = WSP_Variacao,
                WSP_Liderando    = WSP_Liderando,
                WDO_Preco        = WDO_Preco,
                WDO_Variacao     = WDO_Variacao,
                WIN_Variacao     = WIN_Variacao,
                CorrelacaoWinWdo = CorrelWinWdo,
                CorrelacaoWinWsp = CorrelWinWsp,
                LagWinWsp        = LagWinWsp,
                GapWinWsp        = GapWinWsp,

                IcebergDetectadoBid = _detectors.IsIcebergDetected(),
                SpoofingDetectado   = _detectors.IsSpoofDetected(),

                Fase          = ConverterFase(_deltaEngine.CurrentSessionPhase),
                RVOL          = _deltaEngine.RVOL,
                ProximoEvento = ProximoEvento,

                UltimosPrecos = _historicoPrecos.ToList(),
                UltimosCVD    = _historicoCVD.ToList(),
            };
        }

        private void AtualizarHistorico()
        {
            if (PrecoAtual > 0)
            {
                if (_historicoPrecos.Count >= 10) _historicoPrecos.Dequeue();
                _historicoPrecos.Enqueue(PrecoAtual);
            }
            if (_historicoCVD.Count >= 10) _historicoCVD.Dequeue();
            _historicoCVD.Enqueue((int)_deltaEngine.CVDDivergence);
        }

        private FaseSessao ConverterFase(SessionPhase phase) => phase switch
        {
            SessionPhase.Abertura  => FaseSessao.Abertura,
            SessionPhase.Meio      => FaseSessao.Meio,
            SessionPhase.Leilao    => FaseSessao.Almoco,
            SessionPhase.PosLeilao => FaseSessao.Fechamento,
            _                      => FaseSessao.Meio
        };

        private PerfilBroker ClassificarPerfil(string nome)
        {
            var iniciadores  = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BTG","XP","GENIAL","ITAU","BRADESCO" };
            var absorvedores = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "GNL","MODAL","CLEAR" };
            if (iniciadores.Contains(nome))  return PerfilBroker.Iniciador;
            if (absorvedores.Contains(nome)) return PerfilBroker.Absorvedor;
            return PerfilBroker.Noise;
        }
    }
}
