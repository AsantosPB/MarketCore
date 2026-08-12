using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MarketCore.Engine;
using MarketCore.Models;
using MarketCore.WPF.Data;
using MarketCore.FlowSense;

namespace MarketCore.WPF.AnaliseQuantitativa
{
    public partial class AnaliseQuantitativaWindow : Window
    {
        // ── integração com o motor ──────────────────────────────────────
        private MarketEngine?    _marketEngine;
        private MarketDataManager? _marketData;
        private Func<bool>?      _isMarketLive;
        private bool             _liveFeedAttached;
        private bool             _isClosing;

        // ── miner ───────────────────────────────────────────────────────
        private CoordPlayerMiner? _miner;

        // ── UI collections ───────────────────────────────────────────────
        private readonly ObservableCollection<PatternRowVm>   _patternRows = new();
        private readonly ObservableCollection<ActiveClusterVm> _clusterRows = new();

        // ── singleton ───────────────────────────────────────────────────
        private static AnaliseQuantitativaWindow? _instance;
        public static AnaliseQuantitativaWindow GetInstance()
        {
            if (_instance == null || !_instance.IsLoaded)
                _instance = new AnaliseQuantitativaWindow(MainWindow.ActiveInstance);
            return _instance;
        }

        // ── timers ──────────────────────────────────────────────────────
        private readonly DispatcherTimer _uiTimer;

        // ── brushes ─────────────────────────────────────────────────────
        private static readonly SolidColorBrush BrGreen   = new(Color.FromRgb(0, 200, 83));
        private static readonly SolidColorBrush BrRed     = new(Color.FromRgb(255, 23, 68));
        private static readonly SolidColorBrush BrGray    = new(Color.FromRgb(170, 170, 170));
        private static readonly SolidColorBrush BrGreenDim = new(Color.FromRgb(26, 61, 31));
        private static readonly SolidColorBrush BrRedDim  = new(Color.FromRgb(61, 26, 26));
        private static readonly SolidColorBrush BrNeutral = new(Color.FromRgb(26, 26, 26));
        private static readonly SolidColorBrush BrBlue    = new(Color.FromRgb(30, 136, 229));
        private static readonly SolidColorBrush BrBorder  = new(Color.FromRgb(42, 42, 42));

        // ═══════════════════════════════════════════════════════════════
        //  CONSTRUTORES
        // ═══════════════════════════════════════════════════════════════

        public AnaliseQuantitativaWindow() : this(MainWindow.ActiveInstance, null) { }
        public AnaliseQuantitativaWindow(MainWindow? host) : this(host, host?.MarketEngine) { }

        public AnaliseQuantitativaWindow(MainWindow? host, MarketEngine? engine)
        {
            InitializeComponent();

            _marketEngine = engine;

            DgPatterns.ItemsSource = _patternRows;
            DgClusters.ItemsSource = _clusterRows;

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _uiTimer.Tick += OnUiTick;
            _uiTimer.Start();

            if (host != null)
            {
                _marketEngine = engine ?? host.MarketEngine;
                host.WireAnaliseQuantitativa(this);
            }
            else
            {
                Loaded          += (_, _) => AnaliseQuantLiveHub.TryWire(this);
                ContentRendered += (_, _) => AnaliseQuantLiveHub.TryWire(this);
            }

            Closing += (_, _) =>
            {
                _isClosing = true;
                try { _uiTimer.Stop(); } catch { /* ignore */ }
                try { _miner?.Stop(); } catch { /* ignore */ }
                try { AnaliseQuantLiveHub.Unregister(this); } catch { /* ignore */ }
                try { _marketEngine?.SetAnaliseQuantSink(null); } catch { /* ignore */ }
                _miner = null;
            };
        }

        // ═══════════════════════════════════════════════════════════════
        //  INTERFACE PÚBLICA (chamada pela MainWindow / Sink / Hub)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>Recebe trade em tempo real — encaminha para o miner.</summary>
        public void EnqueueTrade(TradeEvent trade)
        {
            if (_isClosing) return;
            _miner?.PushLiveTrade(trade);
        }

        public void OnTradeReceived(TradeEvent trade)
        {
            if (_isClosing) return;
            _miner?.PushLiveTrade(trade);
        }

        /// <summary>Alertas da infra antiga — ignorados na nova UI (mantido por compatibilidade).</summary>
        public void AdicionarAlerta(AlertaViewModel _) { }

        /// <summary>Métricas externas — ignoradas na nova UI.</summary>
        public void AtualizarMetricasExternas(MetricasViewModel _) { }

        // ─────────────────────────────────────────────────────────────
        //  ATTACH DO FEED AO VIVO (chamado por WireAnaliseQuantitativa)
        // ─────────────────────────────────────────────────────────────
        public void AttachLiveFeed(
            DeltaEngine deltaEngine,
            Func<long> getSessionDelta,
            Func<(long, long)> getAggression1Min,
            Func<long> getTradesPerSec,
            MarketDataManager marketData,
            Func<bool> isMarketLive)
        {
            _marketData    = marketData;
            _isMarketLive  = isMarketLive;
            _liveFeedAttached = true;

            AnaliseQuantLiveHub.Register(this);

            // inicia o miner com a connection string do MarketDataManager
            StartMiner(marketData);
            AtualizarStatusDb();
        }

        // ═══════════════════════════════════════════════════════════════
        //  MINER — INICIALIZAÇÃO E EVENTOS
        // ═══════════════════════════════════════════════════════════════

        private void StartMiner(MarketDataManager marketData)
        {
            if (_miner != null) return;

            // lê a connection string reflectindo via propriedade ou campo interno
            string? connStr = TryGetConnectionString(marketData);
            if (string.IsNullOrEmpty(connStr))
            {
                TbDbStatus.Text = "PostgreSQL — sem connection string";
                return;
            }

            // detecta símbolo do ticker principal
            string symbol = TryGetPrimarySymbol();

            _miner = new CoordPlayerMiner(connStr, symbol);
            _miner.OnSignal           += OnMinerSignal;
            _miner.OnPatternsUpdated  += OnPatternsUpdated;
            _miner.Start();

            // sincroniza o campo WR com o valor default do miner
            TxtMinWinRate.Text = (_miner.MinWinRateSignal * 100).ToString("F0",
                System.Globalization.CultureInfo.InvariantCulture);

            TbDbStatus.Text = "PostgreSQL OK — miner iniciado";
            DbDot.Fill = BrGreen;
        }

        private void OnMinerSignal(CoordSignal signal)
        {
            if (_isClosing) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (_isClosing) return;
                try { ApplySignal(signal); } catch { /* window teardown */ }
            }, DispatcherPriority.Normal);
        }

        private void OnPatternsUpdated(List<ClusterPattern> patterns)
        {
            if (_isClosing) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (_isClosing) return;
                try { RefreshPatternGrid(patterns); } catch { /* window teardown */ }
            }, DispatcherPriority.Background);
        }

        // ═══════════════════════════════════════════════════════════════
        //  RENDERIZAÇÃO DO SINAL
        // ═══════════════════════════════════════════════════════════════

        private void ApplySignal(CoordSignal signal)
        {
            switch (signal.Direction)
            {
                case CoordSignalDir.Comprar:
                    TbSignalLabel.Text       = "COMPRAR";
                    TbSignalLabel.Foreground = BrGreen;
                    SignalCard.Background    = BrGreenDim;
                    SignalCard.BorderBrush   = BrGreen;
                    TbSignalSub.Text         = $"padrão de compra detectado — {signal.PatternKey}";
                    break;

                case CoordSignalDir.Vender:
                    TbSignalLabel.Text       = "VENDER";
                    TbSignalLabel.Foreground = BrRed;
                    SignalCard.Background    = BrRedDim;
                    SignalCard.BorderBrush   = BrRed;
                    TbSignalSub.Text         = $"padrão de venda detectado — {signal.PatternKey}";
                    break;

                default:
                    TbSignalLabel.Text       = "AGUARDAR";
                    TbSignalLabel.Foreground = BrGray;
                    SignalCard.Background    = BrNeutral;
                    SignalCard.BorderBrush   = BrBorder;
                    TbSignalSub.Text         = "sem padrão detectado";
                    break;
            }

            // barra de confiança
            double conf = signal.Confidence;
            TbConfPct.Text    = signal.Direction == CoordSignalDir.Aguardar ? "—" : $"{conf:P0}";
            double barWidth   = Math.Max(0, Math.Min(1, conf)) * (ConfBar.Parent is FrameworkElement parent ? parent.ActualWidth : 280);
            ConfBar.Width     = barWidth;
            ConfBar.Background = signal.Direction == CoordSignalDir.Comprar ? BrGreen
                               : signal.Direction == CoordSignalDir.Vender  ? BrRed
                               : BrBlue;

            // impacto médio
            TbImpact.Text = signal.Direction != CoordSignalDir.Aguardar
                ? $"impacto médio histórico: {signal.AvgImpactTicks} pts"
                : "";

            // brokers ativos
            if (signal.ActiveBrokers.Length > 0)
            {
                TbActiveBrokers.Text = string.Join("  +  ", signal.ActiveBrokers);
                TbActiveTime.Text    = $"sinal gerado em {signal.GeneratedAt:HH:mm:ss}";
            }
            else
            {
                TbActiveBrokers.Text = "—";
                TbActiveTime.Text    = "";
            }

            TbSignalTime.Text = $"último sinal: {signal.GeneratedAt:HH:mm:ss}";

            // atualiza clusters detectados agora
            RefreshClusterGrid(signal);
        }

        private void RefreshClusterGrid(CoordSignal signal)
        {
            _clusterRows.Clear();
            if (signal.Direction == CoordSignalDir.Aguardar || signal.ActiveBrokers.Length == 0)
                return;

            _clusterRows.Add(new ActiveClusterVm
            {
                Corretoras = string.Join(", ", signal.ActiveBrokers),
                Lado       = signal.Direction == CoordSignalDir.Comprar ? "COMPRA" : "VENDA",
                Contratos  = 0,   // TODO: somar contratos do liveQueue
                Confianca  = signal.Confidence,
                Status     = signal.Direction == CoordSignalDir.Comprar ? "✓ ATIVO" : "✓ ATIVO",
            });
        }

        private void RefreshPatternGrid(List<ClusterPattern> patterns)
        {
            _patternRows.Clear();
            foreach (var p in patterns)
            {
                _patternRows.Add(new PatternRowVm
                {
                    Corretoras = string.Join(", ", p.Brokers),
                    Lado       = p.Side == "B" ? "COMPRA" : "VENDA",
                    Obs        = p.Observations,
                    WR         = p.WinRate,
                    Impacto    = p.AvgImpactTicks,
                    Score      = p.Score,
                    UltimaVez  = p.LastSeen.ToString("HH:mm"),
                });
            }

            TbPatternsSubtitle.Text = $"{patterns.Count} padrões — score mínimo {(patterns.Count > 0 ? patterns[^1].Score:0):N1}";
        }

        // ═══════════════════════════════════════════════════════════════
        //  TIMER DE UI — stats a cada 1s
        // ═══════════════════════════════════════════════════════════════

        private void OnUiTick(object? sender, EventArgs e)
        {
            if (_isClosing) return;
            try
            {
                if (!_liveFeedAttached)
                    AnaliseQuantLiveHub.TryWire(this);

                var miner = _miner;
                if (miner != null)
                {
                    TbPatternCount.Text    = miner.PatternCount.ToString();
                    int newP = miner.NewPatternsLastCycle;
                    int updP = miner.UpdatedPatternsLastCycle;
                    if (newP == 0 && updP == 0)
                        TbCycleDelta.Text = "sem novos";
                    else
                        TbCycleDelta.Text = $"+{newP} novos / {updP} atualizados";
                    var cursor = miner.LastMinedTradeTs;
                    string cursorTxt = cursor == DateTime.MinValue ? "—" : cursor.ToString("dd/MM HH:mm:ss");
                    string dbRange = miner.DbTotalRows == 0
                        ? "DB vazio"
                        : $"DB {miner.DbTotalRows:N0} negócios ({miner.DbMinTs:dd/MM HH:mm}→{miner.DbMaxTs:dd/MM HH:mm})";
                    TbDbTrades.Text = $"+{miner.DbTradesLastMining:N0} negócios novos\ncursor: {cursorTxt}\n{dbRange}";
                    TbLastMine.Text        = miner.LastMiningRun == DateTime.MinValue
                                             ? "—"
                                             : miner.LastMiningRun.ToString("HH:mm:ss");
                    if (miner.IsMiningRunning)
                    {
                        TbMineStatus.Text       = "minerando…";
                        TbMineStatus.Foreground = BrGreen;
                    }
                    else
                    {
                        int nextIn = miner.NextMiningInSec;
                        TbMineStatus.Text       = nextIn > 0 ? $"próxima em {nextIn}s" : "em espera";
                        TbMineStatus.Foreground = BrGray;
                    }
                    TbLiveQueueCount.Text   = miner.LiveQueueCount.ToString("N0");
                    TbDetectInfo.Text       = miner.LastDetectInfo;

                    // atualiza contador de expiração no sub-texto do sinal
                    int secsLeft = miner.SignalSecondsLeft;
                    var sig = miner.LastSignal;
                    if (sig.Direction != CoordSignalDir.Aguardar && secsLeft > 0)
                    {
                        TbSignalSub.Text       = $"padrão {(sig.Direction == CoordSignalDir.Comprar ? "de compra" : "de venda")} detectado — {sig.PatternKey}";
                        TbSignalCountdown.Text = $"{secsLeft}s";
                    }
                    else
                    {
                        TbSignalCountdown.Text = "";
                    }

                    // Badge de renovação: aparece por 5s quando o sinal foi reconfirmado
                    int renewalLeft = miner.RenewalSecondsLeft;
                    var renewalDir  = miner.LastRenewalDir;
                    if (renewalLeft > 0 && renewalDir != CoordSignalDir.Aguardar
                                        && renewalDir == sig.Direction)
                    {
                        string label = renewalDir == CoordSignalDir.Comprar
                            ? "↻ COMPRAR CONFIRMADO"
                            : "↻ VENDER CONFIRMADO";
                        var color = renewalDir == CoordSignalDir.Comprar
                            ? System.Windows.Media.Brushes.LimeGreen
                            : System.Windows.Media.Brushes.OrangeRed;
                        TbRenewalLabel.Text = label;
                        TbRenewalLabel.Foreground = color;
                        RenewalBadge.BorderBrush  = color;
                        TbRenewalCountdown.Text   = $"{renewalLeft}s";
                        RenewalBadge.Visibility   = System.Windows.Visibility.Visible;
                    }
                    else
                    {
                        RenewalBadge.Visibility = System.Windows.Visibility.Collapsed;
                    }
                }

                bool live = _isMarketLive?.Invoke() ?? false;
                TbLiveStatus.Text       = live ? "● AO VIVO" : "● OFFLINE";
                TbLiveStatus.Foreground = live ? BrGreen : BrGray;

                TbLastUpdate.Text = $"atualização: {DateTime.Now:HH:mm:ss}";
            }
            catch { /* durante teardown a UI pode estar sendo destruída */ }
        }

        // ═══════════════════════════════════════════════════════════════
        //  BOTÕES
        // ═══════════════════════════════════════════════════════════════

        private async void BtnMineNow_Click(object sender, RoutedEventArgs e)
        {
            if (_miner == null) return;
            BtnMineNow.IsEnabled = false;
            BtnMineNow.Content   = "⟳  MINERANDO…";
            try   { await _miner.ForceMineCycleAsync(); }
            finally
            {
                BtnMineNow.IsEnabled = true;
                BtnMineNow.Content   = "⟳  MINERAR AGORA";
            }
        }

        private void BtnClearPatterns_Click(object sender, RoutedEventArgs e)
        {
            _miner?.ClearPatterns();
            _patternRows.Clear();
            _clusterRows.Clear();
            ApplySignal(new CoordSignal { Direction = CoordSignalDir.Aguardar });
        }

        private void TxtMinWinRate_LostFocus(object sender, RoutedEventArgs e) => ApplyMinWinRate();

        private void TxtMinWinRate_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                ApplyMinWinRate();
                Keyboard.ClearFocus();
            }
        }

        private void ApplyMinWinRate()
        {
            if (_miner == null) return;
            var raw = TxtMinWinRate.Text.Trim().Replace(",", ".").TrimEnd('%');
            if (double.TryParse(raw, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out double pct))
            {
                // aceita "51" ou "0.51"
                double wr = pct > 1 ? pct / 100.0 : pct;
                wr = Math.Max(0, Math.Min(1, wr));
                _miner.MinWinRateSignal = wr;
                TxtMinWinRate.Text = (wr * 100).ToString("F0",
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                // reverte texto ao valor atual do miner
                TxtMinWinRate.Text = (_miner.MinWinRateSignal * 100).ToString("F0",
                    System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  UTILITÁRIOS
        // ═══════════════════════════════════════════════════════════════

        private void AtualizarStatusDb()
        {
            bool ok = _marketData?.IsConnected ?? false;
            DbDot.Fill      = ok ? BrGreen : BrGray;
            TbDbStatus.Text = ok ? "PostgreSQL OK" : "PostgreSQL —";
        }

        private static string? TryGetConnectionString(MarketDataManager mgr)
        {
            // Acessa via reflection o campo privado _connectionString do MarketDataManager
            try
            {
                var field = typeof(MarketDataManager)
                    .GetField("_connectionString",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                return field?.GetValue(mgr) as string;
            }
            catch { return null; }
        }

        private static string TryGetPrimarySymbol()
        {
            try
            {
                var mw = MainWindow.ActiveInstance;
                if (mw == null) return "";
                // lê o campo _bookOfferTicker via reflection
                var field = typeof(MainWindow)
                    .GetField("_bookOfferTicker",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var val = field?.GetValue(mw) as string;
                // extrai raiz (ex: WINQ26 → WIN)
                if (!string.IsNullOrEmpty(val) && val.Length >= 3)
                    return val[..3];
                return "";
            }
            catch { return ""; }
        }
    }
}
