using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace MarketCore.FlowSense
{
    public partial class FlowScoreConfigWindow : Window
    {
        private readonly FlowScoreConfig _config;
        private readonly FlowScoreConfig _backup;
        private bool _loading = true;

        // ── Auto-Calibrador ───────────────────────────────────────────────
        private List<FlowScoreSnapshot>? _snapshotsEmMemoria;
        private string? _diretorioGravacao;

        public FlowScoreConfigWindow(FlowScoreConfig config,
            List<FlowScoreSnapshot>? snapshotsEmMemoria = null,
            string? diretorioGravacao = null)
        {
            InitializeComponent();

            _config                = config;
            _backup                = config.Clone();
            _snapshotsEmMemoria    = snapshotsEmMemoria;
            _diretorioGravacao     = diretorioGravacao;

            // ── Sliders de calibração manual ──
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

            // ── Auto-Calibrador ──
            BtnAutoCalibrar.Click += BtnAutoCalibrar_Click;

            // Popular seletores
            PopularSeletorPeriodo();
            PopularSeletorMovMin();

            LoadValues();
            _loading = false;
        }

        // ══════════════════════════════════════════════════════════════════
        // SELETORES DO AUTO-CALIBRADOR
        // ══════════════════════════════════════════════════════════════════

        private void PopularSeletorPeriodo()
        {
            ComboPeriodo.Items.Clear();
            ComboPeriodo.Items.Add("5 min");
            ComboPeriodo.Items.Add("10 min");
            ComboPeriodo.Items.Add("15 min");
            ComboPeriodo.Items.Add("20 min");
            ComboPeriodo.Items.Add("25 min");
            ComboPeriodo.Items.Add("30 min");
            ComboPeriodo.Items.Add("60 min");
            ComboPeriodo.Items.Add("120 min");
            ComboPeriodo.Items.Add("1 dia");
            ComboPeriodo.Items.Add("3 dias");
            ComboPeriodo.Items.Add("5 dias");
            ComboPeriodo.Items.Add("10 dias");
            ComboPeriodo.SelectedIndex = 5; // 30 min padrão
        }

        private void PopularSeletorMovMin()
        {
            ComboMovMin.Items.Clear();
            for (int i = 5; i <= 300; i += 5)
                ComboMovMin.Items.Add($"{i} pts");
            ComboMovMin.SelectedIndex = 4; // 25pts padrão
        }

        private (int periodoMinutos, bool usarDisco) ParsePeriodo()
        {
            var sel = ComboPeriodo.SelectedItem?.ToString() ?? "30 min";
            return sel switch
            {
                "5 min"   => (5,    false),
                "10 min"  => (10,   false),
                "15 min"  => (15,   false),
                "20 min"  => (20,   false),
                "25 min"  => (25,   false),
                "30 min"  => (30,   false),
                "60 min"  => (60,   false),
                "120 min" => (120,  false),
                "1 dia"   => (1440, true),
                "3 dias"  => (4320, true),
                "5 dias"  => (7200, true),
                "10 dias" => (14400,true),
                _         => (30,   false),
            };
        }

        private int ParseMovMin()
        {
            var sel = ComboMovMin.SelectedItem?.ToString() ?? "25 pts";
            return int.TryParse(sel.Replace(" pts", ""), out int v) ? v : 25;
        }

        private int ParseDias()
        {
            var sel = ComboPeriodo.SelectedItem?.ToString() ?? "30 min";
            return sel switch
            {
                "1 dia"   => 1,
                "3 dias"  => 3,
                "5 dias"  => 5,
                "10 dias" => 10,
                _         => 0
            };
        }

        // ══════════════════════════════════════════════════════════════════
        // BOTÃO AUTO-CALIBRAR
        // ══════════════════════════════════════════════════════════════════

        private async void BtnAutoCalibrar_Click(object sender, RoutedEventArgs e)
        {
            BtnAutoCalibrar.IsEnabled = false;
            PainelProgresso.Visibility = Visibility.Visible;
            PainelResultado.Visibility = Visibility.Collapsed;
            LabelStatusCalib.Text = "Coletando dados...";
            ProgressoCalib.Value = 0;

            try
            {
                var (periodoMin, usarDisco) = ParsePeriodo();
                int movMin = ParseMovMin();

                List<FlowScoreSnapshot> snapshots = new();

                // Progresso 0-30%: coletar dados
                await Task.Run(() =>
                {
                    if (usarDisco && !string.IsNullOrEmpty(_diretorioGravacao))
                    {
                        int dias = ParseDias();
                        snapshots = AutoCalibradorEngine.LerArquivos(_diretorioGravacao, dias);
                    }
                    else
                    {
                        snapshots = _snapshotsEmMemoria ?? new List<FlowScoreSnapshot>();
                    }
                });

                Dispatcher.Invoke(() =>
                {
                    LabelStatusCalib.Text = $"Dados coletados: {snapshots.Count} snapshots";
                    ProgressoCalib.Value = 30;
                });

                await Task.Delay(200);

                // Progresso 30-80%: analisar janelas
                AutoCalibracaoResultado resultado = null!;
                int totalJanelas = Math.Max(1, periodoMin);

                await Task.Run(() =>
                {
                    resultado = AutoCalibradorEngine.Analisar(
                        snapshots, periodoMin, movMin, _config);
                });

                // Progresso animado 30→80%
                for (int p = 30; p <= 80; p += 5)
                {
                    await Task.Delay(80);
                    Dispatcher.Invoke(() =>
                    {
                        ProgressoCalib.Value = p;
                        LabelStatusCalib.Text = $"Analisando janelas... {p}%";
                    });
                }

                Dispatcher.Invoke(() => ProgressoCalib.Value = 80);
                await Task.Delay(100);

                if (resultado.DadosInsuficientes)
                {
                    Dispatcher.Invoke(() =>
                    {
                        ProgressoCalib.Value = 100;
                        LabelStatusCalib.Text = $"⚠ {resultado.Mensagem}";
                        LabelStatusCalib.Foreground = new SolidColorBrush(Color.FromRgb(255, 200, 0));
                    });
                    return;
                }

                // Progresso 80-100%: aplicar resultado
                await Task.Delay(200);
                Dispatcher.Invoke(() => ProgressoCalib.Value = 100);
                await Task.Delay(100);

                // Exibir resultado e preencher sliders
                Dispatcher.Invoke(() =>
                {
                    LabelStatusCalib.Text = $"✔ {resultado.Mensagem}";
                    LabelStatusCalib.Foreground = new SolidColorBrush(Color.FromRgb(0, 200, 83));

                    ExibirResultado(resultado);
                    PreencherSliders(resultado);

                    PainelResultado.Visibility = Visibility.Visible;
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    LabelStatusCalib.Text = $"Erro: {ex.Message}";
                    LabelStatusCalib.Foreground = new SolidColorBrush(Color.FromRgb(255, 80, 80));
                });
            }
            finally
            {
                Dispatcher.Invoke(() => BtnAutoCalibrar.IsEnabled = true);
            }
        }

        private void ExibirResultado(AutoCalibracaoResultado resultado)
        {
            // Linha BrokerFlow
            var bf = resultado.Componentes.FirstOrDefault(c => c.Nome == "BrokerFlow");
            var fd = resultado.Componentes.FirstOrDefault(c => c.Nome == "FluxoDireto");
            var bk = resultado.Componentes.FirstOrDefault(c => c.Nome == "Book");
            var dt = resultado.Componentes.FirstOrDefault(c => c.Nome == "Detectores");

            if (bf != null) PreencherLinhaResultado(
                TxtResultBrokerAcerto, TxtResultBrokerRatio,
                TxtResultBrokerAtual,  TxtResultBrokerSug, TxtResultBrokerTend, bf);

            if (fd != null) PreencherLinhaResultado(
                TxtResultFluxoAcerto, TxtResultFluxoRatio,
                TxtResultFluxoAtual,  TxtResultFluxoSug, TxtResultFluxoTend, fd);

            if (bk != null) PreencherLinhaResultado(
                TxtResultBookAcerto, TxtResultBookRatio,
                TxtResultBookAtual,  TxtResultBookSug, TxtResultBookTend, bk);

            if (dt != null) PreencherLinhaResultado(
                TxtResultDetectAcerto, TxtResultDetectRatio,
                TxtResultDetectAtual,  TxtResultDetectSug, TxtResultDetectTend, dt);

            TxtResultJanelas.Text = $"Janelas: {resultado.JanelasAnalisadas} | " +
                                    $"Movimentos válidos: {resultado.MovimentosValidos} | " +
                                    $"Mov. mín.: {resultado.MovMinPontos}pts";
        }

        private void PreencherLinhaResultado(
            System.Windows.Controls.TextBlock acerto,
            System.Windows.Controls.TextBlock ratio,
            System.Windows.Controls.TextBlock atual,
            System.Windows.Controls.TextBlock sug,
            System.Windows.Controls.TextBlock tend,
            ComponenteCalibrado c)
        {
            acerto.Text = $"{c.AcertoPct * 100:0}%";
            ratio.Text  = $"{c.Ratio:F1}x";
            atual.Text  = $"{c.PesoAtual * 100:0}%";
            sug.Text    = $"{c.PesoSugerido * 100:0}%";
            tend.Text   = c.Tendencia;
            tend.Foreground = c.Tendencia switch
            {
                "↑" => new SolidColorBrush(Color.FromRgb(0, 200, 83)),
                "↓" => new SolidColorBrush(Color.FromRgb(255, 80, 80)),
                _   => new SolidColorBrush(Color.FromRgb(170, 170, 170))
            };
        }

        private void PreencherSliders(AutoCalibracaoResultado resultado)
        {
            _loading = true;
            foreach (var c in resultado.Componentes)
            {
                switch (c.Nome)
                {
                    case "BrokerFlow":  SliderWeightBroker.Value = c.PesoSugerido; break;
                    case "FluxoDireto": SliderWeightFluxo.Value  = c.PesoSugerido; break;
                    case "Book":        SliderWeightBook.Value   = c.PesoSugerido; break;
                    case "Detectores":  SliderWeightDetect.Value = c.PesoSugerido; break;
                }
            }
            _loading = false;
            RefreshAllLabels();
        }

        // ══════════════════════════════════════════════════════════════════
        // CARGA DE VALORES
        // ══════════════════════════════════════════════════════════════════

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

        // ══════════════════════════════════════════════════════════════════
        // REFRESH DE LABELS
        // ══════════════════════════════════════════════════════════════════

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

        private void OnSliderChanged(object sender,
            RoutedPropertyChangedEventArgs<double> e)
        {
            if (_loading) return;
            RefreshAllLabels();
        }

        // ══════════════════════════════════════════════════════════════════
        // BOTÕES PRINCIPAIS
        // ══════════════════════════════════════════════════════════════════

        private void BtnApply_Click(object sender, RoutedEventArgs e)
        {
            _config.WeightBrokerFlow  = SliderWeightBroker.Value;
            _config.WeightFluxoDireto = SliderWeightFluxo.Value;
            _config.WeightBook        = SliderWeightBook.Value;
            _config.WeightDetectores  = SliderWeightDetect.Value;
            _config.NormalizeWeights();

            _config.BrokerRvolMaxMultiplier = SliderRvolMax.Value;
            _config.PhaseMultiplierAbertura = SliderPhaseAbertura.Value;
            _config.PhaseMultiplierLeilao   = SliderPhaseLeilao.Value;
            _config.PhaseMultiplierMeio     = SliderPhaseMeio.Value;

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
