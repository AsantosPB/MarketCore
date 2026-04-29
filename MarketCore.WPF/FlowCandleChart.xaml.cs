using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MarketCore.WPF
{
    public enum CandleType { Up, Down, Exhaustion, BigVolume }

    public class FlowCandle
    {
        public DateTime Time { get; set; }
        public double Open { get; set; }
        public double High { get; set; }
        public double Low  { get; set; }
        public double Close { get; set; }
        public int TotalAggressionVolume { get; set; }
        public int BuyAggressionVolume { get; set; }
        public int SellAggressionVolume { get; set; }
        public int MaxSingleLot { get; set; }
        public CandleType Type { get; set; }
        public int Delta => BuyAggressionVolume - SellAggressionVolume;
    }

    public class FlowCandleConfig
    {
        // Renko: pontos necessários para fechar uma barra
        public double RenkoPoints { get; set; } = 5.0;

        public int BigLotThreshold { get; set; } = 300;
        public int ExhaustionMinVolume { get; set; } = 100;
        public double ExhaustionMaxPriceDelta { get; set; } = 1.0;
        public double CandleWidth { get; set; } = 8.0;
        public double BigCandleWidthMultiplier { get; set; } = 2.0;
        public double CandleSpacing { get; set; } = 2.0;
    }

    public partial class FlowCandleChart : UserControl
    {
        private static readonly Brush BrushUp         = new SolidColorBrush(Color.FromRgb(0,   200, 68));
        private static readonly Brush BrushDown       = new SolidColorBrush(Color.FromRgb(204, 34,  34));
        private static readonly Brush BrushExhaustion = new SolidColorBrush(Color.FromRgb(96,  96,  96));
        private static readonly Brush BrushBigVolume  = new SolidColorBrush(Color.FromRgb(255, 215,  0));
        private static readonly Brush BrushGrid       = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
        private static readonly Brush BrushAxisText   = new SolidColorBrush(Color.FromRgb(160, 160, 160));

        private readonly List<FlowCandle> _candles = new();
        private FlowCandleConfig _config = new();
        private double _visibleMin, _visibleMax, _currentPrice;
        private readonly object _lock = new();

        // ── Renko: estado do bloco em formação ──────────────────
        // _renkoBase: preço de abertura do bloco atual
        // _renkoVol / _renkoBuyVol / _renkoSellVol: volume acumulado
        private double _renkoBase      = 0;
        private int    _renkoVol       = 0;
        private int    _renkoBuyVol    = 0;
        private int    _renkoSellVol   = 0;
        private int    _renkoMaxLot    = 0;
        private DateTime _renkoStart   = DateTime.Now;

        // ── Timer de render ──────────────────────────────────────
        private readonly DispatcherTimer _renderTimer;

        // ── Escala manual ────────────────────────────────────────
        private bool   _scaleManual  = false;
        private bool   _isDragging   = false;
        private double _dragStartY   = 0;
        private double _dragStartMin = 0;
        private double _dragStartMax = 0;
        private DateTime _lastDragRender = DateTime.MinValue;

        // Botão direito no canvas principal → zoom de escala vertical
        private bool   _isRightDragging = false;
        private double _rightDragStartY = 0;
        private double _rightDragMin    = 0;
        private double _rightDragMax    = 0;

        public FlowCandleChart()
        {
            InitializeComponent();
            _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _renderTimer.Tick += (_, _) => Redraw();
            _renderTimer.Start();
            Loaded     += (_, _) => { SetupEvents(); Redraw(); };
            SizeChanged += (_, _) => Redraw();
            
            // ── Pan vertical com setas do teclado ────────────────
            Focusable = true;
            MouseDown += (_, _) => Focus();
            KeyDown   += OnChartKeyDown;
            
            GenerateDemoData();
        }

        private void SetupEvents()
        {
            btnConfig.Click       += (_, _) => OpenConfig();
            btnClear.Click        += (_, _) => ClearChart();
            btnApplyConfig.Click  += (_, _) => ApplyConfig();
            btnCloseConfig.Click  += (_, _) => configOverlay.Visibility = Visibility.Collapsed;
            btnCancelConfig.Click += (_, _) => configOverlay.Visibility = Visibility.Collapsed;
            canvasChart.MouseMove            += OnChartMouseMove;
            canvasChart.MouseLeave           += OnChartMouseLeave;
            canvasChart.MouseWheel           += OnChartMouseWheel;
            canvasChart.MouseRightButtonDown += OnChartRightDown;
            canvasChart.MouseRightButtonUp   += OnChartRightUp;
            canvasYAxis.MouseLeftButtonDown += OnYAxisDown;
            canvasYAxis.MouseMove           += OnYAxisMove;
            canvasYAxis.MouseLeftButtonUp   += OnYAxisUp;
            canvasYAxis.MouseLeave          += OnYAxisLeave;
            canvasYAxis.MouseWheel          += OnYAxisWheel;
        }

        // ══════════════════════════════════════════════════════════
        // API PÚBLICA
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Processa cada execução da tape.
        /// Fecha automaticamente blocos Renko quando o preço move X pontos.
        /// </summary>
        public void ProcessTrade(double price, int lot, bool isBuyAggression)
        {
            lock (_lock)
            {
                _currentPrice = price;

                // Inicializar base Renko no primeiro trade
                if (_renkoBase == 0)
                {
                    _renkoBase  = price;
                    _renkoStart = DateTime.Now;
                }

                // Acumular volume
                _renkoVol    += lot;
                _renkoMaxLot  = Math.Max(_renkoMaxLot, lot);
                if (isBuyAggression) _renkoBuyVol  += lot;
                else                 _renkoSellVol += lot;

                double pts = _config.RenkoPoints;

                // Verificar se fechou barra para CIMA
                while (price >= _renkoBase + pts)
                {
                    double newBase = _renkoBase + pts;
                    CloseRenkoBlock(_renkoBase, newBase);
                    _renkoBase = newBase;
                }

                // Verificar se fechou barra para BAIXO
                while (price <= _renkoBase - pts)
                {
                    double newBase = _renkoBase - pts;
                    CloseRenkoBlock(_renkoBase, newBase);
                    _renkoBase = newBase;
                }
            }

            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                RedrawCurrentPriceLine();
                RedrawYAxis();
            }));
        }

        private void CloseRenkoBlock(double openPrice, double closePrice)
        {
            // Chamado dentro de lock(_lock)
            var candle = new FlowCandle
            {
                Time                  = _renkoStart,
                Open                  = openPrice,
                Close                 = closePrice,
                High                  = Math.Max(openPrice, closePrice),
                Low                   = Math.Min(openPrice, closePrice),
                TotalAggressionVolume = _renkoVol,
                BuyAggressionVolume   = _renkoBuyVol,
                SellAggressionVolume  = _renkoSellVol,
                MaxSingleLot          = _renkoMaxLot
            };
            candle.Type = ClassifyCandle(candle);
            _candles.Add(candle);
            if (_candles.Count > 500) _candles.RemoveAt(0);

            // Resetar acumuladores para o próximo bloco
            _renkoVol     = 0;
            _renkoBuyVol  = 0;
            _renkoSellVol = 0;
            _renkoMaxLot  = 0;
            _renkoStart   = DateTime.Now;
        }

        public void SetSymbol(string symbol) => lblSymbol.Text = symbol;

        // ══════════════════════════════════════════════════════════
        // CLASSIFICAÇÃO
        // ══════════════════════════════════════════════════════════

        private CandleType ClassifyCandle(FlowCandle c)
        {
            if (c.MaxSingleLot >= _config.BigLotThreshold) return CandleType.BigVolume;
            if (c.TotalAggressionVolume >= _config.ExhaustionMinVolume
                && (c.High - c.Low) <= _config.ExhaustionMaxPriceDelta) return CandleType.Exhaustion;
            if (c.Close > c.Open) return CandleType.Up;
            if (c.Close < c.Open) return CandleType.Down;
            return CandleType.Exhaustion;
        }

        // ══════════════════════════════════════════════════════════
        // RENDER
        // ══════════════════════════════════════════════════════════

        private void Redraw()
        {
            if (!IsLoaded || canvasChart.ActualWidth < 10 || canvasChart.ActualHeight < 10) return;

            List<FlowCandle> all;
            lock (_lock)
            {
                all = new List<FlowCandle>(_candles);

                // Bloco em formação (não fechado ainda) — mostra como preview
                if (_renkoBase > 0 && _renkoVol > 0)
                {
                    double previewClose = _currentPrice;
                    var preview = new FlowCandle
                    {
                        Time                  = _renkoStart,
                        Open                  = _renkoBase,
                        Close                 = previewClose,
                        High                  = Math.Max(_renkoBase, previewClose),
                        Low                   = Math.Min(_renkoBase, previewClose),
                        TotalAggressionVolume = _renkoVol,
                        BuyAggressionVolume   = _renkoBuyVol,
                        SellAggressionVolume  = _renkoSellVol,
                        MaxSingleLot          = _renkoMaxLot
                    };
                    preview.Type = ClassifyCandle(preview);
                    all.Add(preview);
                }
            }

            if (all.Count == 0) return;
            // Recomputar escala se: automático OU escala inválida
            if (!_scaleManual || _visibleMax <= _visibleMin)
                ComputePriceRange(all);

            canvasCandles.Children.Clear();
            canvasGrid.Children.Clear();
            canvasXAxis.Children.Clear();

            DrawGridLines();
            DrawCandles(all);
            RedrawCurrentPriceLine();
            RedrawYAxis();
        }

        private void ComputePriceRange(List<FlowCandle> candles)
        {
            int maxV = (int)(canvasChart.ActualWidth / (_config.CandleWidth + _config.CandleSpacing)) + 2;
            var vis  = candles.Skip(Math.Max(0, candles.Count - maxV)).ToList();
            if (vis.Count == 0) return;
            double min = vis.Min(c => c.Low), max = vis.Max(c => c.High);
            double pad = Math.Max((max - min) * 0.15, _config.RenkoPoints * 3);
            _visibleMin = min - pad;
            _visibleMax = max + pad;
        }

        private void DrawGridLines()
        {
            double h = canvasChart.ActualHeight, w = canvasChart.ActualWidth;
            double step = CalculateGridStep(_visibleMax - _visibleMin);
            for (double p = Math.Ceiling(_visibleMin / step) * step; p <= _visibleMax; p += step)
            {
                double y = PriceToY(p, h);
                if (y < 0 || y > h) continue;
                canvasGrid.Children.Add(new Line
                    { X1 = 0, X2 = w, Y1 = y, Y2 = y, Stroke = BrushGrid, StrokeThickness = 0.5 });
            }
        }

        private void DrawCandles(List<FlowCandle> all)
        {
            double h = canvasChart.ActualHeight, w = canvasChart.ActualWidth;
            double stepX  = _config.CandleWidth + _config.CandleSpacing;
            int    maxV   = (int)(w / stepX) + 1;
            var    vis    = all.Skip(Math.Max(0, all.Count - maxV)).ToList();
            double startX = w - vis.Count * stepX;

            for (int i = 0; i < vis.Count; i++)
            {
                var c      = vis[i];
                bool isBig = c.Type == CandleType.BigVolume;
                bool isLast = i == vis.Count - 1;
                double bw  = isBig ? _config.CandleWidth * _config.BigCandleWidthMultiplier : _config.CandleWidth;
                Brush fill = GetCandleColor(c.Type);
                double cx  = startX + i * stepX + _config.CandleWidth / 2.0;

                double yTop    = PriceToY(Math.Max(c.Open, c.Close), h);
                double yBottom = PriceToY(Math.Min(c.Open, c.Close), h);
                double bht     = Math.Max(yBottom - yTop, 2.0);

                // Bloco em formação — leve transparência
                double opacity = isLast && _renkoVol > 0 ? 0.55 : 1.0;

                var body = new Rectangle
                {
                    Width = bw, Height = bht, Fill = fill,
                    StrokeThickness = isLast ? 0.8 : 0,
                    Stroke = isLast ? fill : Brushes.Transparent,
                    Opacity = opacity,
                    Tag = c
                };
                Canvas.SetLeft(body, cx - bw / 2.0);
                Canvas.SetTop(body, yTop);

                if (isBig)
                    body.Effect = new System.Windows.Media.Effects.DropShadowEffect
                        { Color = Colors.Gold, BlurRadius = 6, Opacity = 0.5, ShadowDepth = 0 };

                body.MouseEnter += (_, e) => ShowTooltip(c, e.GetPosition(canvasChart));
                body.MouseLeave += (_, _) => HideTooltip();
                canvasCandles.Children.Add(body);

                if (i % 10 == 0)
                {
                    var tl = new TextBlock
                    {
                        Text = c.Time.ToString("HH:mm"),
                        Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                        FontSize = 10, FontFamily = new FontFamily("Consolas"),
                        FontWeight = FontWeights.Medium
                    };
                    Canvas.SetLeft(tl, startX + i * stepX);
                    Canvas.SetTop(tl, 3);
                    canvasXAxis.Children.Add(tl);
                }
            }
        }

        // ══════════════════════════════════════════════════════════
        // EIXO Y
        // ══════════════════════════════════════════════════════════

        private void RedrawYAxis()
        {
            canvasYAxis.Children.Clear();
            double h = canvasChart.ActualHeight, wAxis = canvasYAxis.ActualWidth;
            if (h <= 0 || wAxis <= 0) return;

            double step = CalculateGridStep(_visibleMax - _visibleMin);
            for (double p = Math.Ceiling(_visibleMin / step) * step; p <= _visibleMax; p += step)
            {
                double y = PriceToY(p, h);
                if (y < 4 || y > h - 4) continue;
                var lbl = new TextBlock
                {
                    Text = p.ToString("F0", CultureInfo.InvariantCulture),
                    Foreground = BrushAxisText, FontSize = 11,
                    FontFamily = new FontFamily("Consolas"),
                    FontWeight = FontWeights.Medium
                };
                Canvas.SetLeft(lbl, 4);
                Canvas.SetTop(lbl, y - 7);
                canvasYAxis.Children.Add(lbl);
                canvasYAxis.Children.Add(new Line
                    { X1 = 0, X2 = 4, Y1 = y, Y2 = y,
                      Stroke = new SolidColorBrush(Color.FromRgb(100, 100, 100)),
                      StrokeThickness = 1.0 });
            }

            // Badge último preço
            if (_currentPrice > 0)
            {
                double y = PriceToY(_currentPrice, h);
                if (y >= 0 && y <= h)
                {
                    var badge = new Border
                    {
                        Width = wAxis - 2, Height = 18,
                        Background   = new SolidColorBrush(Color.FromRgb(0, 210, 255)),
                        CornerRadius = new CornerRadius(3),
                        Child = new TextBlock
                        {
                            Text = _currentPrice.ToString("F0", CultureInfo.InvariantCulture),
                            Foreground = Brushes.Black,
                            FontSize = 11, FontWeight = FontWeights.Bold,
                            FontFamily = new FontFamily("Consolas"),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment   = VerticalAlignment.Center
                        }
                    };
                    Canvas.SetLeft(badge, 1);
                    Canvas.SetTop(badge, y - 8);
                    canvasYAxis.Children.Add(badge);
                }
            }
        }

        private void RedrawCurrentPriceLine()
        {
            if (_currentPrice <= 0 || canvasChart.ActualHeight < 10) return;
            double y = PriceToY(_currentPrice, canvasChart.ActualHeight);
            lineCurrentPrice.Y1 = lineCurrentPrice.Y2 = y;
            lineCurrentPrice.X2 = canvasChart.ActualWidth;
        }

        // ══════════════════════════════════════════════════════════
        // INTERAÇÃO — Mouse
        // ══════════════════════════════════════════════════════════

        private void OnChartMouseMove(object sender, MouseEventArgs e)
        {
            var pos = e.GetPosition(canvasChart);

            // Botão direito pressionado → zoom de escala vertical (sem throttle agressivo)
            if (_isRightDragging)
            {
                if ((DateTime.Now - _lastDragRender).TotalMilliseconds >= 16)
                {
                    _lastDragRender = DateTime.Now;
                    double dy     = pos.Y - _rightDragStartY;
                    double range  = _rightDragMax - _rightDragMin;
                    double factor = Math.Max(0.05, Math.Min(20.0, 1.0 + dy * 0.005));
                    double center = (_rightDragMax + _rightDragMin) / 2.0;
                    double newRange = Math.Max(5, range * factor);
                    _visibleMin  = center - newRange / 2.0;
                    _visibleMax  = center + newRange / 2.0;
                    _scaleManual = true;
                    DrawGridLines();
                    RedrawCurrentPriceLine();
                    RedrawYAxis();
                }
                return;
            }

            // Crosshair normal
            crossHairH.Y1 = crossHairH.Y2 = pos.Y;
            crossHairH.X2 = canvasChart.ActualWidth;
            crossHairV.X1 = crossHairV.X2 = pos.X;
            crossHairV.Y2 = canvasChart.ActualHeight;
        }

        private void OnChartMouseLeave(object sender, MouseEventArgs e)
        {
            if (_isRightDragging)
            {
                _isRightDragging = false;
                canvasChart.ReleaseMouseCapture();
                Redraw();
            }
            crossHairH.Y1 = crossHairH.Y2 = -100;
            crossHairV.X1 = crossHairV.X2 = -100;
            HideTooltip();
        }

        // Botão direito DOWN → inicia zoom de escala
        private void OnChartRightDown(object sender, MouseButtonEventArgs e)
        {
            _isRightDragging = true;
            _rightDragStartY = e.GetPosition(canvasChart).Y;
            _rightDragMin    = _visibleMin;
            _rightDragMax    = _visibleMax;
            canvasChart.CaptureMouse();
            e.Handled = true;
        }

        // Botão direito UP → finaliza e redraw completo
        private void OnChartRightUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isRightDragging) return;
            _isRightDragging = false;
            canvasChart.ReleaseMouseCapture();
            Redraw();
            e.Handled = true;
        }

        // Scroll = zoom de largura de barras
        private void OnChartMouseWheel(object sender, MouseWheelEventArgs e)
        {
            double delta = e.Delta > 0 ? 1.5 : -1.5;
            _config.CandleWidth = Math.Max(3, Math.Min(40, _config.CandleWidth + delta));
            Redraw();
        }

        // ── Eixo Y ───────────────────────────────────────────────

        private void OnYAxisDown(object sender, MouseButtonEventArgs e)
        {
            _isDragging   = true;
            _dragStartY   = e.GetPosition(canvasYAxis).Y;
            _dragStartMin = _visibleMin;
            _dragStartMax = _visibleMax;
            _scaleManual  = true;
            canvasYAxis.CaptureMouse();
        }

        private void OnYAxisMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging) return;
            if ((DateTime.Now - _lastDragRender).TotalMilliseconds < 33) return;
            _lastDragRender = DateTime.Now;
            double dy     = e.GetPosition(canvasYAxis).Y - _dragStartY;
            double range  = _dragStartMax - _dragStartMin;
            double factor = Math.Max(0.1, Math.Min(10.0, 1.0 + dy * 0.004));
            double center = (_dragStartMax + _dragStartMin) / 2.0;
            double newRange = Math.Max(5, range * factor);
            _visibleMin = center - newRange / 2.0;
            _visibleMax = center + newRange / 2.0;
            DrawGridLines();
            RedrawCurrentPriceLine();
            RedrawYAxis();
        }

        private void OnYAxisUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDragging) return;
            _isDragging = false;
            canvasYAxis.ReleaseMouseCapture();
            Redraw();
        }

        private void OnYAxisLeave(object sender, MouseEventArgs e)
        {
            if (_isDragging) { _isDragging = false; canvasYAxis.ReleaseMouseCapture(); Redraw(); }
        }

        private void OnYAxisWheel(object sender, MouseWheelEventArgs e)
        {
            _scaleManual  = true;
            double center = (_visibleMax + _visibleMin) / 2.0;
            double range  = (_visibleMax - _visibleMin) * (e.Delta > 0 ? 0.85 : 1.18);
            _visibleMin = center - Math.Max(5, range) / 2.0;
            _visibleMax = center + Math.Max(5, range) / 2.0;
            Redraw();
        }

        // ── Pan vertical com setas do teclado ────────────────────
        
        private void OnChartKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Up && e.Key != Key.Down) return;

            _scaleManual = true;
            double step  = (_visibleMax - _visibleMin) * 0.05; // move 5% do range por tecla

            if (e.Key == Key.Up)
            {
                // Seta para cima → gráfico sobe
                _visibleMin += step;
                _visibleMax += step;
            }
            else
            {
                // Seta para baixo → gráfico desce
                _visibleMin -= step;
                _visibleMax -= step;
            }

            Redraw();
            e.Handled = true;
        }

        // ══════════════════════════════════════════════════════════
        // TOOLTIP
        // ══════════════════════════════════════════════════════════

        private void ShowTooltip(FlowCandle c, Point pos)
        {
            tooltipPrice.Text      = string.Format("O:{0:F0}  C:{1:F0}  ({2:+0;-0}pts)",
                                        c.Open, c.Close, c.Close - c.Open);
            tooltipVolume.Text     = string.Format("Vol: {0:N0}  MaxLote: {1}", c.TotalAggressionVolume, c.MaxSingleLot);
            tooltipAggression.Text = string.Format("Delta: {0}  ({1}C / {2}V)", c.Delta, c.BuyAggressionVolume, c.SellAggressionVolume);
            double left = pos.X + 12, top = pos.Y - 50;
            if (left + 210 > canvasChart.ActualWidth) left = pos.X - 220;
            if (top < 0) top = 5;
            Canvas.SetLeft(tooltipBorder, left);
            Canvas.SetTop(tooltipBorder, top);
            tooltipBorder.Visibility = Visibility.Visible;
        }

        private void HideTooltip() => tooltipBorder.Visibility = Visibility.Collapsed;

        // ══════════════════════════════════════════════════════════
        // CONFIG
        // ══════════════════════════════════════════════════════════

        private void OpenConfig()
        {
            txtRenkoPoints.Text          = _config.RenkoPoints.ToString("F0", CultureInfo.InvariantCulture);
            txtBigLot.Text               = _config.BigLotThreshold.ToString();
            txtExhaustionVol.Text        = _config.ExhaustionMinVolume.ToString();
            txtExhaustionPriceDelta.Text = _config.ExhaustionMaxPriceDelta.ToString("F1", CultureInfo.InvariantCulture);
            txtCandleWidth.Text          = _config.CandleWidth.ToString("F0", CultureInfo.InvariantCulture);
            txtBigCandleMultiplier.Text  = _config.BigCandleWidthMultiplier.ToString("F1", CultureInfo.InvariantCulture);
            lblConfigError.Visibility    = Visibility.Collapsed;
            configOverlay.Visibility     = Visibility.Visible;
        }

        private void ApplyConfig()
        {
            lblConfigError.Visibility = Visibility.Collapsed;
            try
            {
                double renkoPoints = double.Parse(txtRenkoPoints.Text, CultureInfo.InvariantCulture);
                int    bigLot      = int.Parse(txtBigLot.Text);
                int    exhVol      = int.Parse(txtExhaustionVol.Text);
                double exhDelta    = double.Parse(txtExhaustionPriceDelta.Text, CultureInfo.InvariantCulture);
                double candleW     = double.Parse(txtCandleWidth.Text, CultureInfo.InvariantCulture);
                double bigMult     = double.Parse(txtBigCandleMultiplier.Text, CultureInfo.InvariantCulture);

                if (renkoPoints < 1) throw new Exception("Pontos Renko deve ser >= 1.");
                if (bigLot < 1 || exhVol < 1 || exhDelta < 0 || candleW < 2 || bigMult < 1)
                    throw new Exception("Valores fora do intervalo.");

                // Verificar se o tamanho do Renko mudou — só nesse caso limpa o histórico
                bool renkoChanged = Math.Abs(_config.RenkoPoints - renkoPoints) > 0.001;

                _config.RenkoPoints              = renkoPoints;
                _config.BigLotThreshold          = bigLot;
                _config.ExhaustionMinVolume       = exhVol;
                _config.ExhaustionMaxPriceDelta   = exhDelta;
                _config.CandleWidth               = candleW;
                _config.BigCandleWidthMultiplier  = bigMult;

                lblBigLotFilter.Text = bigLot.ToString();
                lblRenkoFilter.Text  = renkoPoints.ToString("F0") + "pts";

                configOverlay.Visibility = Visibility.Collapsed;

                if (renkoChanged)
                {
                    // Só reseta quando o tamanho do bloco Renko muda
                    lock (_lock)
                    {
                        _candles.Clear();
                        _renkoBase    = _currentPrice > 0 ? _currentPrice : 0;
                        _renkoVol     = 0;
                        _renkoBuyVol  = 0;
                        _renkoSellVol = 0;
                        _renkoMaxLot  = 0;
                        _renkoStart   = DateTime.Now;
                    }
                }
                else
                {
                    // Apenas reclassifica as barras existentes com os novos filtros
                    // NÃO toca em _candles, apenas atualiza as classificações
                    lock (_lock)
                    {
                        foreach (var c in _candles)
                            c.Type = ClassifyCandle(c);
                        // Importante: manter _renkoBase e estado do bloco em formação intactos
                    }
                }

                Redraw();
            }
            catch (Exception ex)
            {
                lblConfigError.Text       = "Erro: " + ex.Message;
                lblConfigError.Visibility = Visibility.Visible;
            }
        }

        private void ClearChart()
        {
            lock (_lock)
            {
                _candles.Clear();
                _renkoBase    = 0;
                _renkoVol     = 0;
                _renkoBuyVol  = 0;
                _renkoSellVol = 0;
                _renkoMaxLot  = 0;
                _renkoStart   = DateTime.Now;
                _scaleManual  = false;
                _visibleMin   = 0;
                _visibleMax   = 0;
            }
            Redraw();
        }

        // ══════════════════════════════════════════════════════════
        // HELPERS
        // ══════════════════════════════════════════════════════════

        private double PriceToY(double price, double h) =>
            _visibleMax <= _visibleMin ? h / 2.0
            : ((_visibleMax - price) / (_visibleMax - _visibleMin)) * h;

        private static Brush GetCandleColor(CandleType t) => t switch
        {
            CandleType.Up        => BrushUp,
            CandleType.Down      => BrushDown,
            CandleType.BigVolume => BrushBigVolume,
            _                    => BrushExhaustion
        };

        private static double CalculateGridStep(double range)
        {
            if (range <= 0) return 5;
            double m = Math.Pow(10, Math.Floor(Math.Log10(range / 6.0)));
            double n = (range / 6.0) / m;
            return n < 1.5 ? m : n < 3.5 ? 2 * m : n < 7.5 ? 5 * m : 10 * m;
        }

        private static FlowCandle CloneCandle(FlowCandle s) => new()
        {
            Time = s.Time, Open = s.Open, High = s.High, Low = s.Low, Close = s.Close,
            TotalAggressionVolume = s.TotalAggressionVolume,
            BuyAggressionVolume   = s.BuyAggressionVolume,
            SellAggressionVolume  = s.SellAggressionVolume,
            MaxSingleLot = s.MaxSingleLot, Type = s.Type
        };

        // ══════════════════════════════════════════════════════════
        // DEMO DATA — simula movimento Renko
        // ══════════════════════════════════════════════════════════

        private void GenerateDemoData()
        {
            var rng   = new Random(42);
            double price = 193700;
            double pts   = _config.RenkoPoints;
            var time     = DateTime.Now.AddMinutes(-60);
            int count    = 0;

            for (int i = 0; i < 2000 && count < 80; i++)
            {
                double move = (rng.NextDouble() - 0.48) * pts * 0.8;
                price += move;
                int lot = rng.Next(5, 150);
                bool isBuy = rng.NextDouble() > 0.5;

                _currentPrice = price;
                if (_renkoBase == 0) { _renkoBase = price; _renkoStart = time; }

                _renkoVol    += lot;
                _renkoMaxLot  = Math.Max(_renkoMaxLot, lot);
                if (isBuy) _renkoBuyVol += lot; else _renkoSellVol += lot;

                while (price >= _renkoBase + pts)
                {
                    double newBase = _renkoBase + pts;
                    var c = new FlowCandle
                    {
                        Time = time.AddSeconds(i * 2), Open = _renkoBase, Close = newBase,
                        High = newBase, Low = _renkoBase,
                        TotalAggressionVolume = _renkoVol, BuyAggressionVolume = _renkoBuyVol,
                        SellAggressionVolume = _renkoSellVol, MaxSingleLot = _renkoMaxLot
                    };
                    if (count % 18 == 0) c.MaxSingleLot = rng.Next(300, 600);
                    c.Type = ClassifyCandle(c);
                    _candles.Add(c);
                    _renkoBase = newBase;
                    _renkoVol = _renkoBuyVol = _renkoSellVol = _renkoMaxLot = 0;
                    _renkoStart = time.AddSeconds(i * 2);
                    count++;
                }
                while (price <= _renkoBase - pts)
                {
                    double newBase = _renkoBase - pts;
                    var c = new FlowCandle
                    {
                        Time = time.AddSeconds(i * 2), Open = _renkoBase, Close = newBase,
                        High = _renkoBase, Low = newBase,
                        TotalAggressionVolume = _renkoVol, BuyAggressionVolume = _renkoBuyVol,
                        SellAggressionVolume = _renkoSellVol, MaxSingleLot = _renkoMaxLot
                    };
                    if (count % 12 == 0) { c.TotalAggressionVolume = 150; c.MaxSingleLot = 80; }
                    c.Type = ClassifyCandle(c);
                    _candles.Add(c);
                    _renkoBase = newBase;
                    _renkoVol = _renkoBuyVol = _renkoSellVol = _renkoMaxLot = 0;
                    _renkoStart = time.AddSeconds(i * 2);
                    count++;
                }
            }
        }
    }
}
