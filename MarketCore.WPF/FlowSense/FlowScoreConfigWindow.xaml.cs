using System;
using System.Windows;

namespace MarketCore.FlowSense
{
    public partial class FlowScoreConfigWindow : Window
    {
        private readonly FlowScoreConfig _config;
        private readonly FlowScoreConfig _backup;
        private bool _loading = true;

        public FlowScoreConfigWindow(FlowScoreConfig config)
        {
            InitializeComponent();

            _config = config;
            _backup = config.Clone();

            // ═══ Registrar TODOS os event handlers via código ═══
            SliderWeightBroker.ValueChanged += OnSliderChanged;
            SliderWeightFluxo.ValueChanged  += OnSliderChanged;
            SliderWeightBook.ValueChanged   += OnSliderChanged;
            SliderWeightDetect.ValueChanged += OnSliderChanged;

            SliderRvolMax.ValueChanged       += OnSliderChanged;
            SliderPhaseAbertura.ValueChanged += OnSliderChanged;
            SliderPhaseLeilao.ValueChanged   += OnSliderChanged;
            SliderPhaseMeio.ValueChanged     += OnSliderChanged;

            SliderFluxoDelta.ValueChanged += OnSliderChanged;
            SliderFluxoCVD.ValueChanged   += OnSliderChanged;

            SliderBookPressure.ValueChanged       += OnSliderChanged;
            SliderBookImbalance.ValueChanged      += OnSliderChanged;
            SliderBookRenewable.ValueChanged      += OnSliderChanged;
            SliderBookVWAP.ValueChanged           += OnSliderChanged;
            SliderBookRenewableScore.ValueChanged += OnSliderChanged;

            SliderSpoofPenalty.ValueChanged      += OnSliderChanged;
            SliderIcebergBonus.ValueChanged      += OnSliderChanged;
            SliderExhaustionPenalty.ValueChanged += OnSliderChanged;
            SliderStopHuntPenalty.ValueChanged   += OnSliderChanged;
            SliderCVDThreshold.ValueChanged      += OnSliderChanged;
            SliderCVDScore.ValueChanged          += OnSliderChanged;

            BtnApply.Click  += BtnApply_Click;
            BtnCancel.Click += BtnCancel_Click;
            BtnReset.Click  += BtnReset_Click;

            LoadValues();
            _loading = false;
        }

        // ══════════════════════════════════════════════════════
        // CARGA DE VALORES
        // ══════════════════════════════════════════════════════

        private void LoadValues()
        {
            SliderWeightBroker.Value = _config.WeightBrokerFlow;
            SliderWeightFluxo.Value  = _config.WeightFluxoDireto;
            SliderWeightBook.Value   = _config.WeightBook;
            SliderWeightDetect.Value = _config.WeightDetectores;

            SliderRvolMax.Value       = _config.BrokerRvolMaxMultiplier;
            SliderPhaseAbertura.Value = _config.PhaseMultiplierAbertura;
            SliderPhaseLeilao.Value   = _config.PhaseMultiplierLeilao;
            SliderPhaseMeio.Value     = _config.PhaseMultiplierMeio;

            SliderFluxoDelta.Value = _config.FluxoDeltaWeight;
            SliderFluxoCVD.Value   = _config.FluxoCVDWeight;

            SliderBookPressure.Value       = _config.BookPressureWeight;
            SliderBookImbalance.Value      = _config.BookImbalanceWeight;
            SliderBookRenewable.Value      = _config.BookRenewableWeight;
            SliderBookVWAP.Value           = _config.BookVWAPWeight;
            SliderBookRenewableScore.Value = _config.BookRenewableScore;

            SliderSpoofPenalty.Value      = _config.DetectorSpoofPenalty;
            SliderIcebergBonus.Value      = _config.DetectorIcebergBonus;
            SliderExhaustionPenalty.Value = _config.DetectorExhaustionPenalty;
            SliderStopHuntPenalty.Value   = _config.DetectorStopHuntPenalty;
            SliderCVDThreshold.Value      = _config.DetectorCVDThreshold;
            SliderCVDScore.Value          = _config.DetectorCVDScore;

            RefreshAllLabels();
        }

        // ══════════════════════════════════════════════════════
        // REFRESH DE LABELS
        // ══════════════════════════════════════════════════════

        private void RefreshAllLabels()
        {
            LabelWeightBroker.Text = $"{SliderWeightBroker.Value * 100:0}%";
            LabelWeightFluxo.Text  = $"{SliderWeightFluxo.Value  * 100:0}%";
            LabelWeightBook.Text   = $"{SliderWeightBook.Value   * 100:0}%";
            LabelWeightDetect.Text = $"{SliderWeightDetect.Value * 100:0}%";

            double sum = (SliderWeightBroker.Value + SliderWeightFluxo.Value +
                          SliderWeightBook.Value   + SliderWeightDetect.Value) * 100;
            WeightSumLabel.Text = $"Soma: {sum:0}%  (será normalizado para 100%)";
            WeightSumLabel.Foreground = Math.Abs(sum - 100) < 2
                ? System.Windows.Media.Brushes.Gray
                : System.Windows.Media.Brushes.Orange;

            LabelRvolMax.Text       = $"{SliderRvolMax.Value:F1}×";
            LabelPhaseAbertura.Text = $"{SliderPhaseAbertura.Value:F2}×";
            LabelPhaseLeilao.Text   = $"{SliderPhaseLeilao.Value:F2}×";
            LabelPhaseMeio.Text     = $"{SliderPhaseMeio.Value:F2}×";

            LabelFluxoDelta.Text = $"{SliderFluxoDelta.Value * 100:0}%";
            LabelFluxoCVD.Text   = $"{SliderFluxoCVD.Value   * 100:0}%";

            LabelBookPressure.Text       = $"{SliderBookPressure.Value   * 100:0}%";
            LabelBookImbalance.Text      = $"{SliderBookImbalance.Value  * 100:0}%";
            LabelBookRenewable.Text      = $"{SliderBookRenewable.Value  * 100:0}%";
            LabelBookVWAP.Text           = $"{SliderBookVWAP.Value       * 100:0}%";
            LabelBookRenewableScore.Text = $"{SliderBookRenewableScore.Value:0}";

            LabelSpoofPenalty.Text      = $"{SliderSpoofPenalty.Value:0}";
            LabelIcebergBonus.Text      = $"{SliderIcebergBonus.Value:0}";
            LabelExhaustionPenalty.Text = $"{SliderExhaustionPenalty.Value:0}";
            LabelStopHuntPenalty.Text   = $"{SliderStopHuntPenalty.Value:0}";
            LabelCVDThreshold.Text      = $"{SliderCVDThreshold.Value:0}";
            LabelCVDScore.Text          = $"{SliderCVDScore.Value:0}";
        }

        // ══════════════════════════════════════════════════════
        // EVENTO ÚNICO PARA TODOS OS SLIDERS
        // ══════════════════════════════════════════════════════

        private void OnSliderChanged(object sender,
            RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loading) return;
            RefreshAllLabels();
        }

        // ══════════════════════════════════════════════════════
        // BOTÕES
        // ══════════════════════════════════════════════════════

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            _config.WeightBrokerFlow  = SliderWeightBroker.Value;
            _config.WeightFluxoDireto = SliderWeightFluxo.Value;
            _config.WeightBook        = SliderWeightBook.Value;
            _config.WeightDetectores  = SliderWeightDetect.Value;
            _config.NormalizeWeights();

            _config.BrokerRvolMaxMultiplier  = SliderRvolMax.Value;
            _config.PhaseMultiplierAbertura  = SliderPhaseAbertura.Value;
            _config.PhaseMultiplierLeilao    = SliderPhaseLeilao.Value;
            _config.PhaseMultiplierMeio      = SliderPhaseMeio.Value;

            _config.FluxoDeltaWeight = SliderFluxoDelta.Value;
            _config.FluxoCVDWeight   = SliderFluxoCVD.Value;

            _config.BookPressureWeight      = SliderBookPressure.Value;
            _config.BookImbalanceWeight     = SliderBookImbalance.Value;
            _config.BookRenewableWeight     = SliderBookRenewable.Value;
            _config.BookVWAPWeight          = SliderBookVWAP.Value;
            _config.BookRenewableScore      = SliderBookRenewableScore.Value;

            _config.DetectorSpoofPenalty      = SliderSpoofPenalty.Value;
            _config.DetectorIcebergBonus      = SliderIcebergBonus.Value;
            _config.DetectorExhaustionPenalty = SliderExhaustionPenalty.Value;
            _config.DetectorStopHuntPenalty   = SliderStopHuntPenalty.Value;
            _config.DetectorCVDThreshold      = SliderCVDThreshold.Value;
            _config.DetectorCVDScore          = SliderCVDScore.Value;

            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            var defaults = new FlowScoreConfig();
            _loading = true;
            SliderWeightBroker.Value       = defaults.WeightBrokerFlow;
            SliderWeightFluxo.Value        = defaults.WeightFluxoDireto;
            SliderWeightBook.Value         = defaults.WeightBook;
            SliderWeightDetect.Value       = defaults.WeightDetectores;
            SliderRvolMax.Value            = defaults.BrokerRvolMaxMultiplier;
            SliderPhaseAbertura.Value      = defaults.PhaseMultiplierAbertura;
            SliderPhaseLeilao.Value        = defaults.PhaseMultiplierLeilao;
            SliderPhaseMeio.Value          = defaults.PhaseMultiplierMeio;
            SliderFluxoDelta.Value         = defaults.FluxoDeltaWeight;
            SliderFluxoCVD.Value           = defaults.FluxoCVDWeight;
            SliderBookPressure.Value       = defaults.BookPressureWeight;
            SliderBookImbalance.Value      = defaults.BookImbalanceWeight;
            SliderBookRenewable.Value      = defaults.BookRenewableWeight;
            SliderBookVWAP.Value           = defaults.BookVWAPWeight;
            SliderBookRenewableScore.Value = defaults.BookRenewableScore;
            SliderSpoofPenalty.Value       = defaults.DetectorSpoofPenalty;
            SliderIcebergBonus.Value       = defaults.DetectorIcebergBonus;
            SliderExhaustionPenalty.Value  = defaults.DetectorExhaustionPenalty;
            SliderStopHuntPenalty.Value    = defaults.DetectorStopHuntPenalty;
            SliderCVDThreshold.Value       = defaults.DetectorCVDThreshold;
            SliderCVDScore.Value           = defaults.DetectorCVDScore;
            _loading = false;
            RefreshAllLabels();
        }
    }
}
