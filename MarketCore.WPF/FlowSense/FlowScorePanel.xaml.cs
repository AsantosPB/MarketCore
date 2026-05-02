using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MarketCore.AgentPanel;

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

        // ── Agent Panel ───────────────────────────────────────────────────
        private AgentViewModel?    _agentViewModel;
        private AgentBridge?       _agentBridge;
        private AgentPanelWindow?  _agentPanel;

        public FlowScorePanel()
        {
            InitializeComponent();
            BtnConfig.Click      += BtnConfig_Click;
            BtnAgentPanel.Click  += BtnAgentPanel_Click;  // ← botão robozinho
        }

        // ═══════════════════════════════════════════════════════
        // INITIALIZE — mesmo método existente + AgentPanel
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

            // Inicializa o AgentViewModel e o Bridge
            _agentViewModel = new AgentViewModel();
            _agentBridge    = new AgentBridge(
                _agentViewModel,
                flowScoreEngine,
                brokerAccum,
                deltaEngine,
                bookAnalyzer,
                detectors);

            // Timer principal — 250ms (mesmo intervalo de antes)
            _updateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _updateTimer.Tick += OnTick;
            _updateTimer.Start();
        }

        // ═══════════════════════════════════════════════════════
        // MÉTODO PARA ALIMENTAR CORRELAÇÕES (WIN/WDO/WSP)
        // Chame este método do MarketEngine sempre que os
        // preços do WDO e WSP forem atualizados
        // ═══════════════════════════════════════════════════════
        public void AtualizarCorrelacoes(
            double precoAtual,
            double wspPreco,    double wspVariacao,
            double wdoPreco,    double wdoVariacao,
            double winVariacao,
            double correlWinWdo, double correlWinWsp,
            int    lagWinWsp,    double gapWinWsp,
            bool   wspLiderando,
            double proximoSuporte,    double proximaResistencia,
            string proximoEvento = null)
        {
            if (_agentBridge == null) return;

            _agentBridge.PrecoAtual       = precoAtual;
            _agentBridge.WSP_Preco        = wspPreco;
            _agentBridge.WSP_Variacao     = wspVariacao;
            _agentBridge.WDO_Preco        = wdoPreco;
            _agentBridge.WDO_Variacao     = wdoVariacao;
            _agentBridge.WIN_Variacao     = winVariacao;
            _agentBridge.CorrelWinWdo     = correlWinWdo;
            _agentBridge.CorrelWinWsp     = correlWinWsp;
            _agentBridge.LagWinWsp        = lagWinWsp;
            _agentBridge.GapWinWsp        = gapWinWsp;
            _agentBridge.WSP_Liderando    = wspLiderando;
            _agentBridge.ProximoSuporte   = proximoSuporte;
            _agentBridge.ProximaResistencia = proximaResistencia;
            _agentBridge.ProximoEvento    = proximoEvento;
        }

        // ═══════════════════════════════════════════════════════
        // TICK PRINCIPAL — 250ms
        // ═══════════════════════════════════════════════════════
        private void OnTick(object sender, EventArgs e)
        {
            // 1. Atualiza display do FlowScore (comportamento original)
            UpdateDisplay();

            // 2. Atualiza o AgentPanel (novo)
            _agentBridge?.Atualizar();
        }

        // ═══════════════════════════════════════════════════════
        // BOTÃO ROBOZINHO — abre/foca a janela do agente
        // ═══════════════════════════════════════════════════════
        private void BtnAgentPanel_Click(object sender, RoutedEventArgs e)
        {
            if (_agentViewModel == null) return;

            // Se já está aberta, traz para frente
            if (_agentPanel != null && _agentPanel.IsLoaded)
            {
                _agentPanel.Activate();
                return;
            }

            // Cria a janela flutuante
            _agentPanel = new AgentPanelWindow();

            // Conecta o ViewModel à janela
            _agentViewModel.AttachWindow(_agentPanel);

            // Inicia o agente
            _agentViewModel.Start();

            _agentPanel.Show();
        }

        // ═══════════════════════════════════════════════════════
        // BOTÃO CALIBRAÇÃO — comportamento original inalterado
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
        // UPDATE DISPLAY — comportamento original inalterado
        // ═══════════════════════════════════════════════════════
        private void UpdateDisplay()
        {
            if (_flowScoreEngine == null) return;

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
                _                      => "—"
            } ?? "—";
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

            // Stop Hunt
            if (_deltaEngine?.StopHuntDetected == true)
            {
                StopHuntLabel.Text       = "detectado ⚠";
                StopHuntLabel.Foreground = Brushes.DarkRed;
            }
            else
            {
                StopHuntLabel.Text       = "—";
                StopHuntLabel.Foreground = Brushes.Gray;
            }

            // BrokerFlow ativo — Compradores
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

            // BrokerFlow ativo — Vendedores
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

            // Delta 3min
            Delta3minLabel.Text       = $"{_deltaEngine?.CurrentDelta3min:+0;-0;0}";
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
            _agentViewModel?.Stop();
        }
    }
}
