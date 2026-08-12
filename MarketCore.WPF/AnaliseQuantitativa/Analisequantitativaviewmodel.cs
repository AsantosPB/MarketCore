using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows;
using MarketCore.Models;

namespace MarketCore.WPF.AnaliseQuantitativa
{
    // ═══════════════════════════════════════════════════════════════════
    // VIEW MODEL PRINCIPAL
    // ═══════════════════════════════════════════════════════════════════
    public class AnaliseQuantitativaViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? p = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

        // ─── Coleções ─────────────────────────────────────────────────
        public ObservableCollection<AlertaViewModel>   AlertasLive    { get; } = new();
        public ObservableCollection<AlertaViewModel>   AlertasFull    { get; } = new();
        public ObservableCollection<DetectorViewModel> Detectores     { get; } = new();
        public ObservableCollection<DetectorMiniViewModel> DetectoresMini { get; } = new();
        public ObservableCollection<TapeItemViewModel> TapeRecente    { get; } = new();
        public ObservableCollection<BacktestTradeViewModel> BacktestTrades { get; } = new();
        public ObservableCollection<string>            NomesDetectores { get; } = new();

        // ─── Métricas atuais ──────────────────────────────────────────
        private MetricasViewModel? _metricasAtuais;
        public MetricasViewModel? MetricasAtuais
        {
            get => _metricasAtuais;
            set { _metricasAtuais = value; OnPropertyChanged(); }
        }

        // ─── Contadores ───────────────────────────────────────────────
        public int    TotalAlertasHoje { get; set; } = 0;
        public double WinRateHoje      { get; set; } = 0;
        public int    TotalTradesHoje  { get; set; } = 0;
        public long   TotalTradesDB    { get; set; } = 0;
        public int    CvdPeriodoMinutos { get; set; } = 30;

        // ─────────────────────────────────────────────────────────────
        // CARREGAR DETECTORES
        // ─────────────────────────────────────────────────────────────
        public void CarregarDetectores()
        {
            Detectores.Clear();
            DetectoresMini.Clear();
            NomesDetectores.Clear();
            NomesDetectores.Add("Todos");

            var lista = new List<DetectorViewModel>
            {
                // TAPE PATTERNS
                new() { Nome = "AbsorptionReversal",    Categoria = "Tape Patterns",   Descricao = "Detecta reversão por absorção: queda forte + agressão vendedora + preço trava + delta negativo.", WinRate = 0.74, ProfitFactor = 2.1, TotalSinais = 87,  Ativo = true  },
                new() { Nome = "AggressiveFlow",        Categoria = "Tape Patterns",   Descricao = "Sequência unidirecional de trades agressivos em janela curta (≥10 trades em 5s).",               WinRate = 0.68, ProfitFactor = 1.8, TotalSinais = 142, Ativo = true  },
                new() { Nome = "ReversalPattern",       Categoria = "Tape Patterns",   Descricao = "Fluxo forte seguido de reversão brusca >3 ticks em <10s. Identifica stop hunts.",                WinRate = 0.71, ProfitFactor = 1.9, TotalSinais = 63,  Ativo = true  },
                new() { Nome = "VolumeCluster",         Categoria = "Tape Patterns",   Descricao = "Concentração anormal de volume (>3x média 20min) sinalizando zona de interesse.",                WinRate = 0.61, ProfitFactor = 1.4, TotalSinais = 201, Ativo = false },
                new() { Nome = "SpeedBurst",            Categoria = "Tape Patterns",   Descricao = "Aceleração brusca na taxa de trades (>200/min vs normal 50-80/min). Pânico ou euforia.",         WinRate = 0.59, ProfitFactor = 1.3, TotalSinais = 178, Ativo = false },

                // PRICE ACTION
                new() { Nome = "BreakoutVelocity",     Categoria = "Price Action",    Descricao = "Velocidade de rompimento: ≥5 ticks em <10s com volume confirmatório.",                           WinRate = 0.72, ProfitFactor = 2.0, TotalSinais = 54,  Ativo = true  },
                new() { Nome = "FalseBreakout",         Categoria = "Price Action",    Descricao = "Rompimento com volume baixo (<50% média) + retorno em <30s. Fake breakout.",                     WinRate = 0.76, ProfitFactor = 2.4, TotalSinais = 41,  Ativo = true  },
                new() { Nome = "RangeCompression",      Categoria = "Price Action",    Descricao = "Redução progressiva de amplitude em 5min (<30% da média). Pré-explosão.",                        WinRate = 0.64, ProfitFactor = 1.6, TotalSinais = 89,  Ativo = false },

                // PRESSURE METRICS
                new() { Nome = "DeltaDivergence",      Categoria = "Pressure",        Descricao = "Delta acumula oposto ao preço em 5min (exaustão de tendência).",                                  WinRate = 0.73, ProfitFactor = 2.0, TotalSinais = 67,  Ativo = true  },
                new() { Nome = "CumulativeDeltaFlip",  Categoria = "Pressure",        Descricao = "Delta acumulado muda de sinal 2x em <3min. Mudança de controle comprador/vendedor.",              WinRate = 0.70, ProfitFactor = 1.9, TotalSinais = 38,  Ativo = true  },
                new() { Nome = "PassiveAbsorption",    Categoria = "Pressure",        Descricao = "Volume passivo absorvendo agressões (ratio passivo/agressivo >3:1). Mão forte.",                  WinRate = 0.78, ProfitFactor = 2.6, TotalSinais = 29,  Ativo = true  },

                // BROKER BEHAVIOR
                new() { Nome = "BrokerDominance",      Categoria = "Broker",          Descricao = "1 broker concentra >30% do volume em 5min. Player relevante ativo.",                             WinRate = 0.65, ProfitFactor = 1.7, TotalSinais = 112, Ativo = false },
                new() { Nome = "CoordinatedEntry",     Categoria = "Broker",          Descricao = "≥3 brokers no mesmo lado em <10s. Movimento institucional coordenado.",                          WinRate = 0.80, ProfitFactor = 3.1, TotalSinais = 17,  Ativo = true  },
                new() { Nome = "JPStackDetector",      Categoria = "Broker",          Descricao = "Detecta padrão de entrada do JP Morgan: acumulação passiva seguida de agressão.",                WinRate = 0.82, ProfitFactor = 3.4, TotalSinais = 12,  Ativo = true  },

                // CORRELATION
                new() { Nome = "WinWdoDivergence",     Categoria = "Correlação",      Descricao = "Correlação inversa WIN/WDO quebra (normal -0.90, alerta se >-0.7). Anomalia cambial.",           WinRate = 0.75, ProfitFactor = 2.2, TotalSinais = 34,  Ativo = true  },
                new() { Nome = "WSPLeading",            Categoria = "Correlação",      Descricao = "WSP antecipa movimento WIN/WDO com lead de 5-30s. Sinal preditivo via S&P.",                    WinRate = 0.69, ProfitFactor = 1.8, TotalSinais = 58,  Ativo = false },
                new() { Nome = "TripleAlignment",      Categoria = "Correlação",      Descricao = "WIN/WDO/WSP mesma direção por >1min. Movimento forte sincronizado.",                             WinRate = 0.77, ProfitFactor = 2.5, TotalSinais = 23,  Ativo = true  },

                // TEMPORAL
                new() { Nome = "OpeningRangeBreakout", Categoria = "Temporal",        Descricao = "Rompimento da range 9h00-9h15 com volume >1.5x média. Tendência do dia.",                        WinRate = 0.73, ProfitFactor = 2.1, TotalSinais = 47,  Ativo = true  },
                new() { Nome = "PowerHourSetup",       Categoria = "Temporal",        Descricao = "Padrão pré-fechamento 17h30-18h00 com volume >2x média do dia.",                                 WinRate = 0.70, ProfitFactor = 2.0, TotalSinais = 43,  Ativo = true  },
            };

            foreach (var d in lista)
            {
                Detectores.Add(d);
                NomesDetectores.Add(d.Nome);

                // Versão mini para o dashboard
                DetectoresMini.Add(new DetectorMiniViewModel
                {
                    Nome         = d.Nome,
                    WinRateStr   = $"WR: {d.WinRate:P0}",
                    StatusStr    = d.Ativo ? "ATIVO" : "OFF",
                    StatusCorFundo  = d.Ativo ? "#0D2A1A" : "#1A1A1A",
                    StatusCorTexto  = d.Ativo ? "#00C853" : "#616161",
                });
            }
        }

        // ─────────────────────────────────────────────────────────────
        // MÉTRICAS MOCK (até Python integrado)
        // ─────────────────────────────────────────────────────────────
        public void AdicionarTape(TapeItemViewModel item, int maxItens = 80)
        {
            TapeRecente.Insert(0, item);
            while (TapeRecente.Count > maxItens)
                TapeRecente.RemoveAt(TapeRecente.Count - 1);
        }

        /// <summary>Chamado pelo motor (via adapter + Dispatcher). Thread UI obrigatória.</summary>
        public void ProcessarTrade(TradeEvent trade)
        {
            if (trade.Aggressor != TradeAggressor.Buy && trade.Aggressor != TradeAggressor.Sell)
                return;

            bool isBuy = trade.Aggressor == TradeAggressor.Buy;
            AdicionarTape(new TapeItemViewModel
            {
                HoraStr = trade.Time.ToString("HH:mm:ss"),
                Preco = (int)trade.Price,
                Quantidade = trade.Volume,
                Lado = isBuy ? "buy" : "sell",
                Corretora = string.IsNullOrWhiteSpace(trade.Broker) ? "—" : trade.Broker,
            });
        }

        /// <summary>Alerta de detector C# / fluxo (thread UI).</summary>
        public void ProcessarAlerta(AlertaViewModel alerta, int maxLive = 100)
        {
            AlertasLive.Insert(0, alerta);
            AlertasFull.Insert(0, alerta);
            TotalAlertasHoje++;
            OnPropertyChanged(nameof(TotalAlertasHoje));

            while (AlertasLive.Count > maxLive)
                AlertasLive.RemoveAt(AlertasLive.Count - 1);
        }

        /// <summary>Executa ação na thread UI (coleções Observable).</summary>
        public static void RunOnUi(Action action)
        {
            var d = Application.Current?.Dispatcher;
            if (d == null || d.CheckAccess())
                action();
            else
                d.Invoke(action);
        }

        /// <summary>Métricas zeradas até o motor ao vivo conectar (não confundir com pregão real).</summary>
        public void InicializarMetricasVazias()
        {
            MetricasAtuais = new MetricasViewModel
            {
                Delta = 0,
                AggressaoBuy = 0.5,
                AggressaoSell = 0.5,
                Volume1Min = 0,
                VolumeZScore = 0,
                TradesPerSecond = 0,
                WinChange = 0,
                WdoChange = 0,
                Correlacao = -0.90,
            };
            TapeRecente.Clear();
        }

        public void CarregarMetricasMock()
        {
            MetricasAtuais = new MetricasViewModel
            {
                Delta           = 1250,
                AggressaoBuy    = 0.58,
                AggressaoSell   = 0.42,
                Volume1Min      = 320,
                VolumeZScore    = 1.3,
                TradesPerSecond = 4.2,
                WinChange       = 120,
                WdoChange       = -6,
                Correlacao      = -0.88,
                DivergenciaAtiva = false,
            };

            TotalTradesDB   = 3642;
            TotalTradesHoje = 3642;
            WinRateHoje     = 0.72;

            // Tape mock
            TapeRecente.Clear();
            var rng = new Random();
            for (int i = 0; i < 30; i++)
            {
                bool isBuy = rng.Next(2) == 0;
                TapeRecente.Add(new TapeItemViewModel
                {
                    HoraStr    = DateTime.Now.AddSeconds(-i * 3).ToString("HH:mm:ss"),
                    Preco      = 179_250 + rng.Next(-10, 10),
                    Quantidade = rng.Next(1, 20),
                    Lado       = isBuy ? "buy" : "sell",
                    Corretora  = new[] { "XP", "BTG", "Santander", "CM Capital", "Genial" }[rng.Next(5)],
                });
            }

            // Alertas mock
            if (AlertasLive.Count == 0)
            {
                AlertasLive.Add(new AlertaViewModel
                {
                    HoraStr    = "16:18:42",
                    Tipo       = "BUY",
                    Detector   = "AbsorptionReversal",
                    Mensagem   = "Absorção compradora detectada — preço travou após queda de 80pts",
                    Probabilidade = 0.81,
                    Preco      = 179_250,
                    Resultado  = "WIN",
                });
                AlertasLive.Add(new AlertaViewModel
                {
                    HoraStr    = "16:05:11",
                    Tipo       = "DIVERGÊNCIA",
                    Detector   = "WinWdoDivergence",
                    Mensagem   = "WIN subiu 120pts MAS WDO também subiu 8pts — correlação anômala",
                    Probabilidade = 0.76,
                    Preco      = 179_130,
                    Resultado  = "—",
                });
                AlertasLive.Add(new AlertaViewModel
                {
                    HoraStr    = "15:51:03",
                    Tipo       = "SELL",
                    Detector   = "FalseBreakout",
                    Mensagem   = "Fake breakout — rompimento com volume 40% abaixo da média",
                    Probabilidade = 0.79,
                    Preco      = 179_400,
                    Resultado  = "WIN",
                });
            }

            TotalAlertasHoje = AlertasLive.Count;
        }

        // ─────────────────────────────────────────────────────────────
        // BACKTEST (stub — integra com Python na Fase 7)
        // ─────────────────────────────────────────────────────────────
        public async Task<BacktestResultadoViewModel> RodarBacktestAsync(
            string detector, string symbol, DateTime inicio, DateTime fim)
        {
            // Simula delay de backtest real
            await Task.Delay(1500);

            // TODO (Fase 7): chamar Python via subprocess/socket
            // Por ora retorna dados mock realistas
            var rng = new Random();
            int total  = rng.Next(30, 150);
            double wr  = 0.65 + rng.NextDouble() * 0.20;
            int wins   = (int)(total * wr);
            int losses = total - wins;

            var trades = new List<BacktestTradeViewModel>();
            double equity = 0;
            for (int i = 0; i < total; i++)
            {
                bool isWin = rng.NextDouble() < wr;
                int entrada = 179_000 + rng.Next(0, 1000);
                int pnl = isWin ? rng.Next(10, 35) : -rng.Next(8, 18);
                int saida = entrada + pnl;
                equity += pnl;
                trades.Add(new BacktestTradeViewModel
                {
                    DataHoraStr = inicio.AddDays(rng.NextDouble() * (fim - inicio).TotalDays).ToString("dd/MM HH:mm"),
                    Entrada     = entrada,
                    Saida       = saida,
                    Pnl         = pnl,
                    Resultado   = isWin ? "WIN" : "LOSS",
                });
            }

            double avgWin  = trades.Where(t => t.Pnl > 0).Average(t => t.Pnl);
            double avgLoss = Math.Abs(trades.Where(t => t.Pnl < 0).Average(t => t.Pnl));

            return new BacktestResultadoViewModel
            {
                WinRate      = wr,
                ProfitFactor = (wins * avgWin) / (losses * avgLoss),
                SharpeRatio  = wr > 0.70 ? 1.5 + rng.NextDouble() : 0.8 + rng.NextDouble() * 0.5,
                TotalTrades  = total,
                MaxDrawdown  = rng.Next(20, 80),
                AvgWin       = avgWin,
                AvgLoss      = avgLoss,
                Trades       = trades,
            };
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // VIEW MODELS DE SUPORTE
    // ═══════════════════════════════════════════════════════════════════

    public class MetricasViewModel
    {
        public int    Delta             { get; set; }
        public double AggressaoBuy      { get; set; }
        public double AggressaoSell     { get; set; }
        public int    Volume1Min        { get; set; }
        public double VolumeZScore      { get; set; }
        public double TradesPerSecond   { get; set; }
        public int    WinChange         { get; set; }
        public int    WdoChange         { get; set; }
        public double Correlacao        { get; set; }
        public bool   DivergenciaAtiva  { get; set; }
        public string DivergenciaMensagem { get; set; } = "";
    }

    public class AlertaViewModel
    {
        public string HoraStr     { get; set; } = "";
        public string Tipo        { get; set; } = "";
        public string Detector    { get; set; } = "";
        public string Mensagem    { get; set; } = "";
        public double Probabilidade { get; set; }
        public int    Preco       { get; set; }
        public string Resultado   { get; set; } = "—";

        // Computed para binding visual
        public string PrecoStr => Preco > 0 ? Preco.ToString("N0") : "—";
        public string ProbStr  => $"{Probabilidade:P0}";

        public SolidColorBrush TipoCor => Tipo switch
        {
            "BUY"        => new SolidColorBrush(Color.FromRgb(0,   200, 83)),
            "SELL"       => new SolidColorBrush(Color.FromRgb(255, 23,  68)),
            "DIVERGÊNCIA"=> new SolidColorBrush(Color.FromRgb(255, 214, 0)),
            _            => new SolidColorBrush(Color.FromRgb(189, 189, 189)),
        };

        public SolidColorBrush BarraCor => Tipo switch
        {
            "BUY"        => new SolidColorBrush(Color.FromRgb(0,   200, 83)),
            "SELL"       => new SolidColorBrush(Color.FromRgb(255, 23,  68)),
            "DIVERGÊNCIA"=> new SolidColorBrush(Color.FromRgb(255, 214, 0)),
            _            => new SolidColorBrush(Color.FromRgb(66,  66,  66)),
        };

        public SolidColorBrush FundoCor => new SolidColorBrush(Color.FromRgb(20, 20, 20));
        public SolidColorBrush BordaCor => new SolidColorBrush(Color.FromRgb(42, 42, 42));

        public SolidColorBrush ProbFundo => Probabilidade >= 0.75
            ? new SolidColorBrush(Color.FromRgb(0, 30, 10))
            : new SolidColorBrush(Color.FromRgb(30, 20, 0));

        public SolidColorBrush ProbCor => Probabilidade >= 0.75
            ? new SolidColorBrush(Color.FromRgb(0, 200, 83))
            : new SolidColorBrush(Color.FromRgb(255, 214, 0));

        public SolidColorBrush ResultadoCor => Resultado switch
        {
            "WIN"  => new SolidColorBrush(Color.FromRgb(0,   200, 83)),
            "LOSS" => new SolidColorBrush(Color.FromRgb(255, 23,  68)),
            _      => new SolidColorBrush(Color.FromRgb(117, 117, 117)),
        };
    }

    public class DetectorViewModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? p = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

        public string Nome         { get; set; } = "";
        public string Categoria    { get; set; } = "";
        public string Descricao    { get; set; } = "";
        public double WinRate      { get; set; }
        public double ProfitFactor { get; set; }
        public int    TotalSinais  { get; set; }
        public double WinRateLive  { get; set; }

        private bool _ativo;
        public bool Ativo
        {
            get => _ativo;
            set { _ativo = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusStr)); }
        }

        public List<DetectorSinalViewModel> Historico { get; set; } = new();

        // Computed
        public string WinRateStr     => $"WR: {WinRate:P0} | {TotalSinais} sinais";
        public string ProfitFactorStr => $"{ProfitFactor:F2}";
        public string WinRateLiveStr  => WinRateLive > 0 ? $"{WinRateLive:P0}" : "—";
        public string StatusStr       => Ativo ? "ATIVO" : "INATIVO";

        public SolidColorBrush WinRateCor => WinRate >= 0.70
            ? new SolidColorBrush(Color.FromRgb(0, 200, 83))
            : WinRate >= 0.65
                ? new SolidColorBrush(Color.FromRgb(255, 214, 0))
                : new SolidColorBrush(Color.FromRgb(255, 23, 68));
    }

    public class DetectorMiniViewModel
    {
        public string Nome           { get; set; } = "";
        public string WinRateStr     { get; set; } = "";
        public string StatusStr      { get; set; } = "";
        public string StatusCorFundo { get; set; } = "#1A1A1A";
        public string StatusCorTexto { get; set; } = "#616161";
    }

    public class DetectorSinalViewModel
    {
        public string DataHoraStr { get; set; } = "";
        public string Sinal       { get; set; } = "";
        public string PrecoStr    { get; set; } = "";
        public string ProbStr     { get; set; } = "";
        public string Resultado   { get; set; } = "";

        public SolidColorBrush ResultadoCor => Resultado switch
        {
            "WIN"  => new SolidColorBrush(Color.FromRgb(0,   200, 83)),
            "LOSS" => new SolidColorBrush(Color.FromRgb(255, 23,  68)),
            _      => new SolidColorBrush(Color.FromRgb(117, 117, 117)),
        };
    }

    public class TapeItemViewModel
    {
        public string HoraStr    { get; set; } = "";
        public int    Preco      { get; set; }
        public int    Quantidade { get; set; }
        public string Lado       { get; set; } = "";
        public string Corretora  { get; set; } = "";

        public string PrecoStr  => Preco.ToString("N0");
        public string LadoStr   => Lado == "buy" ? "C" : "V";

        public SolidColorBrush LadoCor => Lado == "buy"
            ? new SolidColorBrush(Color.FromRgb(0,   200, 83))
            : new SolidColorBrush(Color.FromRgb(255, 23,  68));
    }

    public class BacktestTradeViewModel
    {
        public string DataHoraStr { get; set; } = "";
        public int    Entrada     { get; set; }
        public int    Saida       { get; set; }
        public double Pnl         { get; set; }
        public string Resultado   { get; set; } = "";

        public string EntradaStr  => Entrada.ToString("N0");
        public string SaidaStr    => Saida.ToString("N0");
        public string PnlStr      => Pnl >= 0 ? $"+{Pnl:F0}" : $"{Pnl:F0}";

        public SolidColorBrush PnlCor => Pnl >= 0
            ? new SolidColorBrush(Color.FromRgb(0,   200, 83))
            : new SolidColorBrush(Color.FromRgb(255, 23,  68));

        public SolidColorBrush ResultadoCor => Resultado == "WIN"
            ? new SolidColorBrush(Color.FromRgb(0,   200, 83))
            : new SolidColorBrush(Color.FromRgb(255, 23,  68));
    }

    public class BacktestResultadoViewModel
    {
        public double WinRate      { get; set; }
        public double ProfitFactor { get; set; }
        public double SharpeRatio  { get; set; }
        public int    TotalTrades  { get; set; }
        public double MaxDrawdown  { get; set; }
        public double AvgWin       { get; set; }
        public double AvgLoss      { get; set; }
        public List<BacktestTradeViewModel> Trades { get; set; } = new();
    }
}