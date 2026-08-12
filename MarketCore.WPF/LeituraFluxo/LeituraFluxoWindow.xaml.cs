using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace MarketCore.WPF.LeituraFluxo
{
    /// <summary>
    /// Janela "Leitura de Fluxo" — substitui o antigo Agent Panel.
    /// Só lê do <see cref="FlowReadingEngine"/> (nunca escreve em disco diretamente — a gravação em
    /// Postgres já é feita por <c>MarketDataManager</c> em paralelo, fora desta janela).
    /// Sem MVVM/binding: os controles das 3 janelas de agressão são fixos no XAML (x:Name) e as 7
    /// colunas de corretora são montadas em código (mesma filosofia usada no resto do MarketCore.WPF).
    /// </summary>
    public partial class LeituraFluxoWindow : Window
    {
        /// <summary>Lista de corretoras suportadas nas 7 colunas (tokens já normalizados como chegam em TradeEvent.Broker).</summary>
        public static readonly string[] BrokerCatalog =
        {
            "JPM", "GOLDMAN", "MERRILL", "MORGAN", "NECTON", "XP", "BGC",
            "CITI", "ELLIOT", "C6", "SANTANDER", "NUL", "NF", "INTER",
            "GENIAL", "SAFRA", "ATIVA", "ITAU", "TULLETT", "SANT", "IDEAL",
            "TERRA", "STONEX", "UBS", "BTG", "AGORA", "MIRAE"
        };

        private static readonly string[] DefaultColumnBrokers =
        {
            "BTG", "XP", "ITAU", "SAFRA", "GENIAL", "SANTANDER", "MORGAN"
        };

        private readonly FlowReadingEngine _engine;
        private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly BrokerColumnControls[] _columns = new BrokerColumnControls[7];

        private static readonly string _posFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MarketCore", "leiturafluxo_pos.json");

        public LeituraFluxoWindow(FlowReadingEngine engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            InitializeComponent();

            BuildColumns();

            _clockTimer.Tick += (_, _) => ClockLabel.Text = DateTime.Now.ToString("HH:mm:ss");
            _uiTimer.Tick += OnUiTick;

            // A janela nunca é realmente fechada (OnClosing cancela e chama Hide(), veja abaixo) — ela
            // só fica escondida. Antes os dois timers rodavam sem parar desde a criação da janela, incluindo
            // o OnUiTick de 1s que varre até 15 milhões de negócios por corretora rodando 5 detectores, para
            // as 7 colunas, TODO SEGUNDO, mesmo com a janela escondida. Como esta janela e a janela principal
            // do book (MarketCore FlowSense) compartilham a MESMA thread de UI/Dispatcher (é uma só por
            // processo WPF), esse trabalho pesado invisível travava periodicamente a thread de UI inteira —
            // era exatamente a causa do "atualização em blocos" relatado no book principal. Agora os timers só
            // rodam enquanto esta janela está de fato visível na tela.
            IsVisibleChanged += (_, e) =>
            {
                if ((bool)e.NewValue)
                {
                    _uiTimer.Start();
                    _clockTimer.Start();
                }
                else
                {
                    _uiTimer.Stop();
                    _clockTimer.Stop();
                }
            };
        }

        // ══════════════════════════════════════════════════════════
        // Construção das 7 colunas de corretora (código, sem XAML/DataTemplate)
        // ══════════════════════════════════════════════════════════

        private sealed class BrokerColumnControls
        {
            public ComboBox BrokerCombo = null!;
            public TextBlock VolumeText = null!;
            public ColumnDefinition BuyBarCol = null!;
            public ColumnDefinition SellBarCol = null!;
            public TextBlock BuyPctText = null!;
            public TextBlock SellPctText = null!;
            public StackPanel PatternsHost = null!;
            public TextBlock EmptyHint = null!;
        }

        private void BuildColumns()
        {
            ColumnsHost.Children.Clear();

            for (int i = 0; i < _columns.Length; i++)
            {
                var controls = new BrokerColumnControls();

                var root = new StackPanel();

                var combo = new ComboBox
                {
                    Style = (Style)Resources["DarkCombo"],
                    Margin = new Thickness(0, 0, 0, 8),
                    ItemsSource = BrokerCatalog
                };
                combo.SelectedItem = i < DefaultColumnBrokers.Length ? DefaultColumnBrokers[i] : BrokerCatalog[i % BrokerCatalog.Length];
                combo.SelectionChanged += (_, _) => RefreshColumn(i);
                controls.BrokerCombo = combo;
                root.Children.Add(combo);

                var volumeText = new TextBlock
                {
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                    Margin = new Thickness(0, 0, 0, 6),
                    Text = "0 lotes | 0 negócios"
                };
                controls.VolumeText = volumeText;
                root.Children.Add(volumeText);

                var barGrid = new Grid { Height = 16, Margin = new Thickness(0, 0, 0, 4) };
                var buyCol = new ColumnDefinition { Width = new GridLength(50, GridUnitType.Star) };
                var sellCol = new ColumnDefinition { Width = new GridLength(50, GridUnitType.Star) };
                barGrid.ColumnDefinitions.Add(buyCol);
                barGrid.ColumnDefinitions.Add(sellCol);
                controls.BuyBarCol = buyCol;
                controls.SellBarCol = sellCol;

                var buyBorder = new Border { Background = (Brush)Resources["AccentGreen"], CornerRadius = new CornerRadius(3, 0, 0, 3) };
                Grid.SetColumn(buyBorder, 0);
                var sellBorder = new Border { Background = (Brush)Resources["AccentRed"], CornerRadius = new CornerRadius(0, 3, 3, 0) };
                Grid.SetColumn(sellBorder, 1);
                barGrid.Children.Add(buyBorder);
                barGrid.Children.Add(sellBorder);

                var buyPctText = new TextBlock
                {
                    Text = "50%", FontFamily = new FontFamily("Consolas"), FontSize = 10, FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x04, 0x22, 0x0F)),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(buyPctText, 0);
                var sellPctText = new TextBlock
                {
                    Text = "50%", FontFamily = new FontFamily("Consolas"), FontSize = 10, FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x2A, 0x00, 0x06)),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(sellPctText, 1);
                controls.BuyPctText = buyPctText;
                controls.SellPctText = sellPctText;
                barGrid.Children.Add(buyPctText);
                barGrid.Children.Add(sellPctText);

                root.Children.Add(barGrid);

                root.Children.Add(new Separator { Background = new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(0, 4, 0, 6) });

                root.Children.Add(new TextBlock
                {
                    Text = "ÚLTIMOS 5 PADRÕES ENCONTRADOS",
                    FontFamily = new FontFamily("Consolas"), FontSize = 9.5, FontWeight = FontWeights.Bold,
                    Foreground = (Brush)Resources["AccentPurple"], Margin = new Thickness(0, 0, 0, 4)
                });

                var patternsHost = new StackPanel();
                controls.PatternsHost = patternsHost;

                var emptyHint = new TextBlock
                {
                    Text = "Aguardando negócios desta corretora…",
                    FontFamily = new FontFamily("Consolas"), FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
                    TextWrapping = TextWrapping.Wrap
                };
                controls.EmptyHint = emptyHint;
                patternsHost.Children.Add(emptyHint);

                var patternsScroll = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = patternsHost
                };
                root.Children.Add(patternsScroll);

                var colBorder = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0F, 0x1A)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(8),
                    Margin = new Thickness(3),
                    Child = root
                };

                _columns[i] = controls;
                ColumnsHost.Children.Add(colBorder);
            }
        }

        // ══════════════════════════════════════════════════════════
        // Atualização periódica
        // ══════════════════════════════════════════════════════════

        private void OnUiTick(object? sender, EventArgs e)
        {
            try
            {
                TbTradeCount.Text = _engine.TotalTradeCount.ToString("N0", CultureInfo.InvariantCulture);

                string backfill = _engine.BackfillStatus;
                if (backfill.Length > 0)
                {
                    StatusLabel.Text = backfill.ToUpperInvariant();
                    StatusLabel.Foreground = (Brush)Resources["AccentYellow"];
                    StatusDot.Fill = (Brush)Resources["AccentYellow"];
                }
                else
                {
                    StatusLabel.Text = "CAPTURANDO";
                    StatusLabel.Foreground = (Brush)Resources["AccentGreen"];
                    StatusDot.Fill = (Brush)Resources["AccentGreen"];
                }

                UpdateAggressionWindow(TxQty1, BuyCol1, SellCol1, TbBuyPct1, TbSellPct1, TbQtyReal1, TbPoints1);
                UpdateAggressionWindow(TxQty2, BuyCol2, SellCol2, TbBuyPct2, TbSellPct2, TbQtyReal2, TbPoints2);
                UpdateAggressionWindow(TxQty3, BuyCol3, SellCol3, TbBuyPct3, TbSellPct3, TbQtyReal3, TbPoints3);

                var snapshots = new List<BrokerFlowSnapshot>(_columns.Length);
                for (int i = 0; i < _columns.Length; i++)
                {
                    var snap = RefreshColumn(i);
                    if (snap != null) snapshots.Add(snap);
                }

                UpdateNextExecutions(snapshots);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LeituraFluxoWindow] OnUiTick: {ex.Message}");
            }
        }

        private void UpdateAggressionWindow(
            TextBox qtyBox, ColumnDefinition buyCol, ColumnDefinition sellCol,
            TextBlock buyPctText, TextBlock sellPctText, TextBlock qtyRealText, TextBlock pointsText)
        {
            if (!int.TryParse(qtyBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int qty) || qty <= 0)
                qty = 1000;
            qty = Math.Clamp(qty, 1000, 20000);

            var result = _engine.GetAggressionWindow(qty);

            buyCol.Width = new GridLength(Math.Max(result.BuyPct, 2), GridUnitType.Star);
            sellCol.Width = new GridLength(Math.Max(result.SellPct, 2), GridUnitType.Star);
            buyPctText.Text = $"{result.BuyPct:0}%";
            sellPctText.Text = $"{result.SellPct:0}%";
            qtyRealText.Text = $"{result.ActualQty:N0} de {result.TargetQty:N0} lotes";

            pointsText.Text = $"{(result.PointsMoved >= 0 ? "+" : "")}{result.PointsMoved} pts";
            pointsText.Foreground = result.PointsMoved >= 0
                ? (Brush)Resources["AccentGreen"]
                : (Brush)Resources["AccentRed"];
        }

        /// <summary>Atualiza a coluna e devolve o snapshot usado — assim quem chama (OnUiTick) pode reaproveitar
        /// os padrões já calculados pro resumo de "Próximas Execuções Previstas" sem rodar a detecção de novo.</summary>
        private BrokerFlowSnapshot? RefreshColumn(int index)
        {
            if (index < 0 || index >= _columns.Length) return null;
            var controls = _columns[index];
            if (controls?.BrokerCombo == null) return null;

            string? broker = controls.BrokerCombo.SelectedItem as string;
            var snap = _engine.GetBrokerSnapshot(broker);

            long total = snap.BuyVolume + snap.SellVolume;
            controls.VolumeText.Text = $"{total:N0} lotes | {snap.TradeCount:N0} negócios";

            controls.BuyBarCol.Width = new GridLength(Math.Max(snap.BuyPct, 2), GridUnitType.Star);
            controls.SellBarCol.Width = new GridLength(Math.Max(snap.SellPct, 2), GridUnitType.Star);
            controls.BuyPctText.Text = $"{snap.BuyPct:0}%";
            controls.SellPctText.Text = $"{snap.SellPct:0}%";

            controls.PatternsHost.Children.Clear();
            if (snap.LastPatterns.Count == 0)
            {
                // "Aguardando negócios" só faz sentido quando realmente não há negócio nenhum ainda.
                // Antes essa mensagem aparecia mesmo com centenas de milhares de negócios já capturados
                // (ex.: corretora com 406 mil negócios mostrando "aguardando"), o que é enganoso — o motor
                // já analisou tudo, só não achou nenhum dos 3 padrões. São duas situações bem diferentes.
                controls.EmptyHint.Text = snap.TradeCount == 0
                    ? "Aguardando negócios desta corretora…"
                    : $"{snap.TradeCount:N0} negócios analisados — nenhum dos 3 padrões (segundo fixo, intervalo regular, impacto no preço) identificado até agora.";
                controls.PatternsHost.Children.Add(controls.EmptyHint);
                return snap;
            }

            foreach (var pattern in snap.LastPatterns)
                controls.PatternsHost.Children.Add(BuildPatternEntry(pattern));

            return snap;
        }

        private UIElement BuildPatternEntry(FlowPatternMatch match)
        {
            var (label, brushKey) = match.Type switch
            {
                FlowPatternType.SegundoFixo => ("SEGUNDO FIXO", "AccentBlue"),
                FlowPatternType.IntervaloRegular => ("INTERVALO REGULAR", "AccentPurple"),
                FlowPatternType.ImpactoPreco => ("IMPACTO NO PREÇO", "AccentOrange"),
                FlowPatternType.CicloFixo => ("CICLO FIXO", "AccentTeal"),
                FlowPatternType.RajadaVolume => ("RAJADA DE VOLUME", "AccentPink"),
                _ => ("PADRÃO", "AccentBlue")
            };
            var typeBrush = (Brush)Resources[brushKey];

            var container = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

            var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
            headerRow.Children.Add(new TextBlock
            {
                // LastConfirmedAt (não FoundAt): o motor reavalia os padrões a cada segundo, e quando o MESMO
                // padrão continua válido ele atualiza LastConfirmedAt (FoundAt só é gravado uma vez, na primeira
                // detecção). Mostrar FoundAt fazia parecer que a análise tinha parado (ex.: MORGAN "travado" às
                // 09:49:26) quando na verdade ela seguia rodando — só a hora exibida nunca mudava.
                Text = match.LastConfirmedAt.ToString("HH:mm:ss"),
                ToolTip = $"Detectado pela primeira vez às {match.FoundAt:HH:mm:ss} · última confirmação às {match.LastConfirmedAt:HH:mm:ss}",
                FontFamily = new FontFamily("Consolas"), FontSize = 10, FontWeight = FontWeights.Bold,
                Foreground = Brushes.White, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center
            });
            headerRow.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                BorderBrush = typeBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(0, 0, 4, 0),
                Child = new TextBlock
                {
                    Text = label, FontFamily = new FontFamily("Consolas"), FontSize = 9, FontWeight = FontWeights.Bold,
                    Foreground = typeBrush
                }
            });

            // Lado da execução (compra/venda): os 3 detectores rodam separadamente para cada lado no motor,
            // então todo padrão aqui é sempre 100% de um lado só — nunca uma mistura dos dois.
            var sideBrush = (Brush)Resources[match.IsBuySide ? "AccentGreen" : "AccentRed"];
            headerRow.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                BorderBrush = sideBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(4, 1, 4, 1),
                Child = new TextBlock
                {
                    Text = match.IsBuySide ? "COMPRA" : "VENDA",
                    FontFamily = new FontFamily("Consolas"), FontSize = 9, FontWeight = FontWeights.Bold,
                    Foreground = sideBrush
                }
            });
            container.Children.Add(headerRow);

            container.Children.Add(new TextBlock
            {
                Text = match.Detail,
                FontFamily = new FontFamily("Consolas"), FontSize = 10.5, Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3)
            });

            // Previsão de próxima execução — só existe pra padrões periódicos (Segundo Fixo, Ciclo Fixo,
            // Intervalo Regular); recalculada a cada tick a partir de agora, então sempre mostra o próximo
            // horário a partir deste instante, não uma previsão congelada de quando o padrão foi achado.
            if (match.NextExpectedAt.HasValue)
            {
                container.Children.Add(new TextBlock
                {
                    Text = $"Próxima execução prevista: {match.NextExpectedAt.Value:HH:mm:ss}",
                    FontFamily = new FontFamily("Consolas"), FontSize = 10, FontWeight = FontWeights.Bold,
                    Foreground = (Brush)Resources["AccentYellow"],
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3)
                });
            }

            // Execuções reais por trás da estatística — sem isso não tinha como conferir na fita/Profit Chart
            // se o negócio contado pelo padrão realmente aconteceu (só dava pra ver o resumo "41 de 331").
            if (match.Examples.Count > 0)
            {
                var examplesText = string.Join("   ", match.Examples.Select(ex =>
                    $"{ex.Time:HH:mm:ss} · {ex.Price:0.##} · {ex.Volume}c"));
                container.Children.Add(new TextBlock
                {
                    Text = "Confira na fita: " + examplesText,
                    FontFamily = new FontFamily("Consolas"), FontSize = 9, FontStyle = FontStyles.Italic,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 3)
                });
            }

            string footer;
            Brush footerBrush;
            if (match.ConfidencePct.HasValue)
            {
                footer = $"Confiança: {match.ConfidencePct.Value:0}%";
                footerBrush = match.ConfidencePct.Value >= 75
                    ? (Brush)Resources["AccentGreen"]
                    : match.ConfidencePct.Value >= 60
                        ? (Brush)Resources["AccentYellow"]
                        : Brushes.White;
            }
            else if (match.PointsMoved.HasValue)
            {
                footer = $"{(match.PointsMoved.Value >= 0 ? "+" : "")}{match.PointsMoved.Value} pts movidos";
                footerBrush = match.PointsMoved.Value >= 0 ? (Brush)Resources["AccentGreen"] : (Brush)Resources["AccentRed"];
            }
            else
            {
                footer = "";
                footerBrush = Brushes.White;
            }

            if (footer.Length > 0)
            {
                container.Children.Add(new TextBlock
                {
                    Text = footer, FontFamily = new FontFamily("Consolas"), FontSize = 10, FontWeight = FontWeights.Bold,
                    Foreground = footerBrush
                });
            }

            return new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 0, 0, 6),
                Margin = new Thickness(0, 0, 0, 2),
                Child = container
            };
        }

        // ══════════════════════════════════════════════════════════
        // Próximas Execuções Previstas — resumo consolidado das 7 colunas
        // ══════════════════════════════════════════════════════════

        private static readonly (string Label, string BrushKey)[] _typeLabels =
        {
            ("SEG. FIXO", "AccentBlue"), ("INTERVALO", "AccentPurple"), ("IMPACTO", "AccentOrange"),
            ("CICLO FIXO", "AccentTeal"), ("RAJADA", "AccentPink")
        };

        /// <summary>Junta os padrões com previsão de horário (<see cref="FlowPatternMatch.NextExpectedAt"/>)
        /// das 7 colunas visíveis num resumo só, ordenado do mais próximo pro mais distante — pra não precisar
        /// abrir cada coluna e ler card por card só pra saber o que vem a seguir e quando.</summary>
        private void UpdateNextExecutions(List<BrokerFlowSnapshot> snapshots)
        {
            // Corte de 10s: Intervalo Regular e Rajada de Volume preveem "último evento + intervalo médio",
            // que pode cair no passado se a execução prevista atrasar (só é recalculada pra frente quando o
            // padrão é reconfirmado de novo). Sem esse corte, uma previsão vencida ficava presa no topo da
            // lista (ordenar por horário bruto põe o passado antes do futuro), parecendo "fora de ordem" e
            // nunca sumindo. Filtra fora qualquer previsão vencida há mais de 10s antes mesmo de ordenar.
            DateTime cutoff = DateTime.Now.AddSeconds(-10);
            var upcoming = new List<(string Broker, FlowPatternMatch Match)>();
            foreach (var snap in snapshots)
            {
                foreach (var match in snap.LastPatterns)
                {
                    if (match.NextExpectedAt.HasValue && match.NextExpectedAt.Value >= cutoff)
                        upcoming.Add((snap.Broker, match));
                }
            }
            upcoming.Sort((a, b) => a.Match.NextExpectedAt!.Value.CompareTo(b.Match.NextExpectedAt!.Value));

            NextExecList.Items.Clear();
            if (upcoming.Count == 0)
            {
                NextExecList.Items.Add(new TextBlock
                {
                    Text = "Nenhum padrão com horário previsível no momento.",
                    FontFamily = new FontFamily("Consolas"), FontSize = 10.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8))
                });
                return;
            }

            foreach (var (broker, match) in upcoming)
                NextExecList.Items.Add(BuildNextExecRow(broker, match));
        }

        private UIElement BuildNextExecRow(string broker, FlowPatternMatch match)
        {
            var sideBrush = (Brush)Resources[match.IsBuySide ? "AccentGreen" : "AccentRed"];
            var (typeLabel, typeBrushKey) = match.Type switch
            {
                FlowPatternType.SegundoFixo => _typeLabels[0],
                FlowPatternType.IntervaloRegular => _typeLabels[1],
                FlowPatternType.ImpactoPreco => _typeLabels[2],
                FlowPatternType.CicloFixo => _typeLabels[3],
                FlowPatternType.RajadaVolume => _typeLabels[4],
                _ => ("PADRÃO", "AccentBlue")
            };
            var typeBrush = (Brush)Resources[typeBrushKey];

            var grid = new Grid { Margin = new Thickness(0, 0, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(65) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });

            var timeText = new TextBlock
            {
                Text = match.NextExpectedAt!.Value.ToString("HH:mm:ss"), FontFamily = new FontFamily("Consolas"),
                FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = (Brush)Resources["AccentYellow"]
            };
            Grid.SetColumn(timeText, 0);

            var brokerText = new TextBlock { Text = broker, FontFamily = new FontFamily("Consolas"), FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
            Grid.SetColumn(brokerText, 1);

            var sideText = new TextBlock { Text = match.IsBuySide ? "COMPRA" : "VENDA", FontFamily = new FontFamily("Consolas"), FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = sideBrush };
            Grid.SetColumn(sideText, 2);

            var volText = new TextBlock
            {
                Text = match.ExpectedVolume.HasValue ? $"~{match.ExpectedVolume.Value:N0} lotes" : "—",
                FontFamily = new FontFamily("Consolas"), FontSize = 10.5, Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8))
            };
            Grid.SetColumn(volText, 3);

            var typeText = new TextBlock { Text = typeLabel, FontFamily = new FontFamily("Consolas"), FontSize = 9.5, FontWeight = FontWeights.Bold, Foreground = typeBrush };
            Grid.SetColumn(typeText, 4);

            grid.Children.Add(timeText);
            grid.Children.Add(brokerText);
            grid.Children.Add(sideText);
            grid.Children.Add(volText);
            grid.Children.Add(typeText);
            return grid;
        }

        // ══════════════════════════════════════════════════════════
        // Posição da janela
        // ══════════════════════════════════════════════════════════

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (File.Exists(_posFile))
                {
                    var json = File.ReadAllText(_posFile);
                    var pos = JsonSerializer.Deserialize<WindowPos>(json);
                    if (pos != null)
                    {
                        Left = pos.Left;
                        Top = pos.Top;
                        Width = pos.Width > 0 ? pos.Width : 1700;
                        Height = pos.Height > 0 ? pos.Height : 920;
                    }
                }
            }
            catch { /* posição opcional — ignora falhas */ }
        }

        /// <summary>Sempre falso até o MainWindow chamar <see cref="ForceClose"/> durante o encerramento do app.
        /// Enquanto for falso, o botão X apenas esconde a janela — uma <c>Window</c> do WPF não pode ser
        /// reaberta com <c>Show()</c> depois de realmente fechada, e este objeto é reaproveitado toda vez
        /// que o usuário reabre a Leitura de Fluxo pelo botão robozinho.</summary>
        private bool _allowRealClose;

        /// <summary>Fecha a janela de verdade. Chamar apenas quando o programa inteiro está encerrando.</summary>
        public void ForceClose()
        {
            _allowRealClose = true;
            Close();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!_allowRealClose)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_posFile)!);
                var pos = new WindowPos(Left, Top, Width, Height);
                File.WriteAllText(_posFile, JsonSerializer.Serialize(pos));
            }
            catch { /* posição opcional — ignora falhas */ }

            _uiTimer.Stop();
            _clockTimer.Stop();
        }

        private record WindowPos(double Left = 100, double Top = 100, double Width = 1700, double Height = 920);
    }
}
