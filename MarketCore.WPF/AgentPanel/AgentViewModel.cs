using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using MarketCore.AgentPanel.Detectors;

namespace MarketCore.AgentPanel
{
    public class AgentViewModel : INotifyPropertyChanged
    {
        private readonly DispatcherTimer  _updateTimer;
        private readonly DetectorRegistry _registry;
        private readonly SignalAggregator _aggregator;
        private readonly PatternHistory   _history;
        private AgentPanelWindow?         _window;

        // Controle de logs periódicos
        private int    _tickCount        = 0;
        private double _lastLoggedScore  = double.MaxValue;
        private string _lastLoggedPlayer = "";
        private double _lastBookImbalance = 0;

        public AgentViewModel()
        {
            _registry   = new DetectorRegistry();
            _aggregator = new SignalAggregator();
            _history    = new PatternHistory();

            _updateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _updateTimer.Tick += OnUpdate;
        }

        public void AttachWindow(AgentPanelWindow window) => _window = window;

        public void Start()
        {
            _updateTimer.Start();
            Log("GESTÃO", "Agent Panel iniciado. Monitoramento ativo.");
        }

        public void Stop()
        {
            _updateTimer.Stop();
            Log("GESTÃO", "Agent Panel pausado.");
        }

        public void OnMarketUpdate(MarketContext ctx) => CurrentContext = ctx;

        private void OnUpdate(object? sender, EventArgs e)
        {
            if (CurrentContext == null) return;

            _tickCount++;

            var ctx       = CurrentContext;
            var deteccoes = _registry.ExecutarTodos(ctx);
            var sinal     = _aggregator.Agregar(deteccoes, ctx);

            // ── Log de SINAL quando detectado ────────────────────────────────
            if (sinal.Detectado)
            {
                _history.Adicionar(sinal);
                string dir = sinal.Direcao == Direcao.Compra ? "Compra" : "Venda";
                Log("SINAL", $"{dir} | Conf: {sinal.Confianca:0%} | {sinal.Descricao}");
            }

            // ── Log de PLAYER a cada 15s se dominante mudou ──────────────────
            if (_tickCount % 15 == 0)
            {
                var topComprador = ctx.TopCompradores.FirstOrDefault();
                var topVendedor  = ctx.TopVendedores.FirstOrDefault();

                if (topComprador != null && topComprador.Nome != _lastLoggedPlayer)
                {
                    _lastLoggedPlayer = topComprador.Nome;
                    Log("PLAYER", $"{topComprador.Nome} lidera compras. " +
                        $"Vol: {topComprador.Volume:N0} lotes. " +
                        $"Perfil: {topComprador.Perfil}.");
                }

                if (topVendedor != null)
                    Log("PLAYER", $"{topVendedor.Nome} lidera vendas. " +
                        $"Vol: {topVendedor.Volume:N0} lotes.");
            }

            // ── Log de BOOK IMBALANCE a cada 10s se mudou significativamente ─
            if (_tickCount % 10 == 0)
            {
                double imbalance = ctx.BookImbalance * 100;
                if (Math.Abs(imbalance - _lastBookImbalance) > 10)
                {
                    _lastBookImbalance = imbalance;
                    string lado = imbalance > 50 ? "BID" : "ASK";
                    Log("PADRÃO", $"Book imbalance: {imbalance:0}% {lado}. " +
                        $"CVD aceleração: {ctx.CVDAceleracao5s:+0;-0}.");
                }
            }

            // ── Log de CORRELAÇÃO a cada 30s ─────────────────────────────────
            if (_tickCount % 30 == 0)
            {
                Log("CORRELAÇÃO", $"WIN/WSP: {ctx.CorrelacaoWinWsp:+0.00;-0.00} | " +
                    $"WIN/WDO: {ctx.CorrelacaoWinWdo:+0.00;-0.00} | " +
                    $"Lag WSP→WIN: {ctx.LagWinWsp}s.");
            }

            // ── Log de CONTEXTO a cada 60s ───────────────────────────────────
            if (_tickCount % 60 == 0)
            {
                string fase = ctx.Fase switch
                {
                    FaseSessao.Abertura   => "Abertura",
                    FaseSessao.Almoco     => "Leilão",
                    FaseSessao.Fechamento => "Pós-leilão",
                    _                     => "Meio"
                };
                Log("CONTEXTO", $"Fase: {fase} | RVOL: {ctx.RVOL:0.0}x | " +
                    $"FlowScore: {ctx.FlowScore:+0.00;-0.00} | " +
                    (ctx.ProximoEvento != null ? $"Próx. evento: {ctx.ProximoEvento}." : "Sem eventos macro."));
            }

            // ── Log de ALERTA se mercado fino ────────────────────────────────
            if (ctx.ThinMarket && _tickCount % 20 == 0)
            {
                Log("ALERTA", "Mercado fino detectado. Volume total do book baixo. Cautela.");
            }

            // ── Log de FLOWSCORE se mudou muito ─────────────────────────────
            if (Math.Abs(ctx.FlowScore - _lastLoggedScore) > 0.15)
            {
                _lastLoggedScore = ctx.FlowScore;
                string dir = ctx.FlowScore > 0 ? "comprador" : "vendedor";
                Log("SINAL", $"FlowScore {ctx.FlowScore:+0.00;-0.00} — pressão {dir}. " +
                    $"Score: {Math.Abs(ctx.FlowScore * 100):0}%.");
            }

            _window?.AtualizarPainel(ConstruirSnapshot(sinal, deteccoes));
        }

        private AgentSnapshot ConstruirSnapshot(ResultadoAgregado sinal, List<ResultadoDeteccao> deteccoes)
        {
            var ctx = CurrentContext!;

            return new AgentSnapshot
            {
                Sinal      = sinal.Direcao == Direcao.Compra ? Sinal.Compra :
                             sinal.Direcao == Direcao.Venda  ? Sinal.Venda  : Sinal.Neutro,
                Preco      = ctx.PrecoAtual,
                Confianca  = sinal.Confianca * 100,
                FlowScore  = ctx.FlowScore,

                Stop       = sinal.Stop,
                StopPts    = (int)(ctx.PrecoAtual - sinal.Stop),
                Alvo       = sinal.Alvo,
                AlvoPts    = (int)(sinal.Alvo - ctx.PrecoAtual),
                RR         = sinal.RR,
                Lote       = sinal.Lote,

                Contexto   = deteccoes.Where(d => d.Detectado).Take(6).Select(d => new ContextItem
                {
                    Icon  = d.Confianca > 0.75 ? "✅" : d.Confianca > 0.5 ? "⚠️" : "❌",
                    Texto = d.Descricao
                }).ToList(),

                Compradores = ctx.TopCompradores.Select(b => new PlayerItem
                {
                    Nome   = b.Nome,
                    Tipo   = b.Perfil == PerfilBroker.Iniciador ? "🔥" : b.Perfil == PerfilBroker.Absorvedor ? "⚡" : "",
                    VolStr = $"{b.Volume:N0} lotes",
                    Pct    = ctx.MaxVolumeComprador > 0 ? (b.Volume / (double)ctx.MaxVolumeComprador) * 100 : 0
                }).ToList(),

                Vendedores = ctx.TopVendedores.Select(b => new PlayerItem
                {
                    Nome   = b.Nome,
                    Tipo   = b.Perfil == PerfilBroker.Iniciador ? "🔥" : b.Perfil == PerfilBroker.Absorvedor ? "⚡" : "",
                    VolStr = $"{b.Volume:N0} lotes",
                    Pct    = ctx.MaxVolumeVendedor > 0 ? (b.Volume / (double)ctx.MaxVolumeVendedor) * 100 : 0
                }).ToList(),

                BookImbalance = ctx.BookImbalance * 100,
                CVDAcc        = ctx.CVDAceleracao5s,

                Padroes = _history.ObterUltimos(5).Select(p => new PatternItem
                {
                    Hora         = p.Timestamp.ToString("HH:mm"),
                    Nome         = p.Padrao,
                    Resultado    = $"{(p.Sucesso ? "▲" : "▼")} {(p.Sucesso ? "+" : "")}{p.Pontos}pts",
                    ResultadoCor = p.Sucesso ? "#00FF88" : "#FF4466",
                    Icone        = p.Sucesso ? "✅" : "❌"
                }).ToList(),
                PatOk   = _history.ContarSucessos(),
                PatFail = _history.ContarFalhas(),
                PatWR   = _history.WinRate() * 100,

                Correlacoes = new List<CorrelItem>
                {
                    new CorrelItem { Ativo = "WSP", Valor = ctx.WSP_Preco.ToString("N1"),  PctStr = $"{(ctx.WSP_Variacao >= 0 ? "▲" : "▼")} {Math.Abs(ctx.WSP_Variacao):0.00}%", Cor = "#38BDF8", PctCor = ctx.WSP_Variacao >= 0 ? "#00FF88" : "#FF4466", Extra = ctx.WSP_Liderando ? "● lidera" : "" },
                    new CorrelItem { Ativo = "WIN", Valor = ctx.PrecoAtual.ToString("N0"), PctStr = $"{(ctx.WIN_Variacao >= 0 ? "▲" : "▼")} {Math.Abs(ctx.WIN_Variacao):0.00}%", Cor = "#00FF88", PctCor = ctx.WIN_Variacao >= 0 ? "#00FF88" : "#FF4466", Extra = $"● lag {ctx.LagWinWsp}s" },
                    new CorrelItem { Ativo = "WDO", Valor = ctx.WDO_Preco.ToString("F3"),  PctStr = $"{(ctx.WDO_Variacao >= 0 ? "▲" : "▼")} {Math.Abs(ctx.WDO_Variacao):0.00}%", Cor = "#FF4466", PctCor = ctx.WDO_Variacao >= 0 ? "#00FF88" : "#FF4466", Extra = "● normal" },
                },
                CorrWinWdo = ctx.CorrelacaoWinWdo,
                CorrWinWsp = ctx.CorrelacaoWinWsp,
                CorrGap    = $"WIN tem espaço de {ctx.GapWinWsp:+0.00;-0.00}% para igualar WSP",

                WinRate     = _history.WinRate() * 100,
                Regime      = DeterminarRegime(),
                ProxEvento  = ctx.ProximoEvento ?? "Nenhum evento próximo"
            };
        }

        private string DeterminarRegime()
        {
            var wr = _history.WinRate();
            return wr > 0.7 ? "✅ Ótimo" : wr > 0.55 ? "✅ Normal" : wr > 0.4 ? "⚠️ Cauteloso" : "🔴 Pausado";
        }

        private void Log(string tag, string mensagem) => _window?.AdicionarLog(tag, mensagem);

        public MarketContext? CurrentContext { get; private set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
