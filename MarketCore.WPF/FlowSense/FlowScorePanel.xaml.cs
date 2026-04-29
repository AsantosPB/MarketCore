using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace MarketCore.FlowSense
{
    public partial class FlowScorePanel : UserControl
    {
        private FlowScoreEngine?   _flowScoreEngine;
        private BrokerAccumulator? _brokerAccum;
        private DeltaEngine?       _deltaEngine;
        private DispatcherTimer?   _updateTimer;

        public FlowScorePanel()
        {
            InitializeComponent();
            BtnConfig.Click += BtnConfig_Click;
        }

        public void Initialize(
            FlowScoreEngine   flowScoreEngine,
            BrokerAccumulator brokerAccum,
            DeltaEngine       deltaEngine)
        {
            _flowScoreEngine = flowScoreEngine;
            _brokerAccum     = brokerAccum;
            _deltaEngine     = deltaEngine;

            _updateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _updateTimer.Tick += (s, e) => UpdateDisplay();
            _updateTimer.Start();
        }

        private void BtnConfig_Click(object sender, RoutedEventArgs e)
        {
            if (_flowScoreEngine == null) return;

            var window = new FlowScoreConfigWindow(_flowScoreEngine.Config)
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();
        }

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

            // Pesos (ocultos)
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
                bool isComp = _deltaEngine?.CVDDivergence > 0;
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

            // BrokerFlow ativo
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

        public void Shutdown() => _updateTimer?.Stop();
    }
}
