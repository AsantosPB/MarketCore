using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MarketCore.WPF;

namespace MarketCore.FlowSense
{
    public partial class FlowScorePanel : UserControl
    {
        private FlowScoreEngine?    _flowScoreEngine;
        private BrokerAccumulator?  _brokerAccum;
        private DeltaEngine?        _deltaEngine;
        private BookAnalyzer?       _bookAnalyzer;
        private DetectorAggregator? _detectors;
        private DispatcherTimer?    _updateTimer;

        // ── Auto-Calibrador ───────────────────────────────────────────────
        private List<FlowScoreSnapshot>? _snapshots;
        private string? _diretorioGravacao;

        /// <summary>Disparado quando o usuário clica no botão robozinho — o MainWindow decide o que abrir
        /// (janela de Leitura de Fluxo, que substitui o antigo Agent Panel).</summary>
        public event EventHandler? LeituraFluxoRequested;

        public FlowScorePanel()
        {
            InitializeComponent();
            BtnConfig.Click      += BtnConfig_Click;
            BtnAgentPanel.Click  += BtnAgentPanel_Click;  // ← botão robozinho
        }

        // ═══════════════════════════════════════════════════════
        // INITIALIZE - mesmo método existente + AgentPanel
        // ═══════════════════════════════════════════════════════
        public void Initialize(
            FlowScoreEngine         flowScoreEngine,
            BrokerAccumulator       brokerAccum,
            DeltaEngine             deltaEngine,
            BookAnalyzer            bookAnalyzer,
            DetectorAggregator      detectors,
            List<FlowScoreSnapshot>? snapshots         = null,
            string?                  diretorioGravacao = null)
        {
            _flowScoreEngine   = flowScoreEngine;
            _brokerAccum       = brokerAccum;
            _deltaEngine       = deltaEngine;
            _bookAnalyzer      = bookAnalyzer;
            _detectors         = detectors;
            _snapshots         = snapshots;
            _diretorioGravacao = diretorioGravacao;

            // Timer principal - 250ms (mesmo intervalo de antes)
            _updateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _updateTimer.Tick += OnTick;
            _updateTimer.Start();
        }

        // ═══════════════════════════════════════════════════════
        // TICK PRINCIPAL - 250ms
        // ═══════════════════════════════════════════════════════
        private void OnTick(object sender, EventArgs e)
        {
            try
            {
                UpdateDisplay();
            }
            catch (Exception ex)
            {
                App.AppendCrashLog("FlowScorePanel.OnTick", ex);
            }
        }

        // ═══════════════════════════════════════════════════════
        // BOTÃO ROBOZINHO - avisa o MainWindow para abrir a Leitura de Fluxo
        // (a janela e o motor de captura vivem no MainWindow, que tem acesso
        // direto ao fluxo de TradeEvent — este UserControl só repassa o clique)
        // ═══════════════════════════════════════════════════════
        private void BtnAgentPanel_Click(object sender, RoutedEventArgs e)
        {
            LeituraFluxoRequested?.Invoke(this, EventArgs.Empty);
        }

        // ═══════════════════════════════════════════════════════
        // BOTÃO CALIBRAÇÃO - comportamento original inalterado
        // ═══════════════════════════════════════════════════════
        private void BtnConfig_Click(object sender, RoutedEventArgs e)
        {
            if (_flowScoreEngine == null) return;

            var window = new FlowScoreConfigWindow(
                _flowScoreEngine.Config,
                _snapshots,
                _diretorioGravacao)
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();
        }

        // ═══════════════════════════════════════════════════════
        // UPDATE DISPLAY - comportamento original inalterado
        // ═══════════════════════════════════════════════════════
        private void UpdateDisplay()
        {
            if (_flowScoreEngine == null) return;

            bool aggregatedBook = _flowScoreEngine.Config.PreferAggregatedBookSignals;

            // Score principal
            ScoreLabel.Text       = $"{_flowScoreEngine.FlowScore:+0;-0;0}";
            ScoreLabel.Foreground = GetColorForScore(_flowScoreEngine.FlowScore);

            // Componentes
            BrokerFlowLabel.Text       = $"{_flowScoreEngine.BrokerFlowComponent:+0;-0;0}";
            BrokerFlowLabel.Foreground = GetColorForScore(_flowScoreEngine.BrokerFlowComponent);

            FluxoLabel.Text       = $"{_flowScoreEngine.FluxoDirectoComponent:+0;-0;0}";
            FluxoLabel.Foreground = GetColorForScore(_flowScoreEngine.FluxoDirectoComponent);

            BookLabel.Text       = $"{_flowScoreEngine.BookComponent:+0;-0;0}";
            BookLabel.Foreground = GetColorForScore(_flowScoreEngine.BookComponent);

            DetectoresLabel.Text       = $"{_flowScoreEngine.DetectoresComponent:+0;-0;0}";
            DetectoresLabel.Foreground = GetColorForScore(_flowScoreEngine.DetectoresComponent);

            // Pesos
            WeightBrokerLabel.Text = $"{_flowScoreEngine.Config.WeightBrokerFlow * 100:0}%";
            WeightFluxoLabel.Text  = $"{_flowScoreEngine.Config.WeightFluxoDireto * 100:0}%";
            WeightBookLabel.Text   = $"{_flowScoreEngine.Config.WeightBook * 100:0}%";
            WeightDetectLabel.Text = $"{_flowScoreEngine.Config.WeightDetectores * 100:0}%";

            // RVOL
            RVOLLabel.Text        = $"{_deltaEngine?.RVOL:F1}x";
            RVOLContextLabel.Text = $"{_deltaEngine?.RVOL:F1}x média";

            // Session Phase
            string phaseText = _deltaEngine?.CurrentSessionPhase switch
            {
                SessionPhase.Abertura  => "Abertura",
                SessionPhase.Meio      => "Meio",
                SessionPhase.Leilao    => "Leilão",
                SessionPhase.PosLeilao => "Pós-leilão",
                _                      => "-"
            } ?? "-";
            SessionLabel.Text        = phaseText;
            SessionContextLabel.Text = phaseText;

            // VWAP
            if (_deltaEngine?.SessionVWAP > 0)
            {
                VWAPLabel.Text       = "calculando";
                VWAPLabel.Foreground = Brushes.Gray;
            }

            // CVD Divergence
            if (Math.Abs(_deltaEngine?.CVDDivergence ?? 0) > 50)
            {
                bool isComp      = _deltaEngine?.CVDDivergence > 0;
                CVDLabel.Text       = isComp ? "ATIVA (COMP.)" : "ATIVA (VEND.)";
                CVDLabel.Foreground = isComp ? Brushes.Lime : Brushes.Red;
            }
            else
            {
                CVDLabel.Text       = "NEUTRO";
                CVDLabel.Foreground = Brushes.Gray;
            }

            // Stop Hunt (penalidade só entra no score se PreferAggregatedBookSignals == false)
            if (aggregatedBook)
            {
                StopHuntLabel.Text       = "-";
                StopHuntLabel.Foreground = Brushes.Gray;
            }
            else if (_deltaEngine?.StopHuntDetected == true)
            {
                StopHuntLabel.Text       = "detectado ⚠";
                StopHuntLabel.Foreground = Brushes.DarkRed;
            }
            else
            {
                StopHuntLabel.Text       = "-";
                StopHuntLabel.Foreground = Brushes.Gray;
            }

            // BrokerFlow ativo - Compradores / Vendedores (desativado no modo livro agregado)
            if (aggregatedBook)
            {
                BuyerActivityLabel.Text = "-";
                TopSellerLabel.Text = "-";
            }
            else
            {
                var activeBuyers = _brokerAccum?.GetActiveBuyers60s();
                if (activeBuyers != null && activeBuyers.Count > 0)
                {
                    var lines = new System.Text.StringBuilder();
                    for (int i = 0; i < Math.Min(3, activeBuyers.Count); i++)
                    {
                        if (i > 0) lines.Append('\n');
                        lines.Append($"{activeBuyers[i].BrokerName} +{activeBuyers[i].ActiveBuyVol60s:F0}");
                    }
                    BuyerActivityLabel.Text = lines.ToString();
                }

                var activeSellers = _brokerAccum?.GetActiveSellers60s();
                if (activeSellers != null && activeSellers.Count > 0)
                {
                    var lines = new System.Text.StringBuilder();
                    for (int i = 0; i < Math.Min(3, activeSellers.Count); i++)
                    {
                        if (i > 0) lines.Append('\n');
                        lines.Append($"{activeSellers[i].BrokerName} -{activeSellers[i].ActiveSellVol60s:F0}");
                    }
                    TopSellerLabel.Text = lines.ToString();
                }
            }

            // Delta 3min
            Delta3minLabel.Foreground = GetColorForScore(_deltaEngine?.CurrentDelta3min ?? 0);
        }

        private Brush GetColorForScore(double score)
        {
            if (score > 50)       return Brushes.DarkGreen;
            else if (score > 20)  return Brushes.Green;
            else if (score < -50) return Brushes.DarkRed;
            else if (score < -20) return Brushes.Red;
            else                  return Brushes.Gray;
        }

        public void Shutdown()
        {
            _updateTimer?.Stop();
        }
    }
}
