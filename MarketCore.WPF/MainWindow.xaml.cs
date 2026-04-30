using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MarketCore.Engine;
using MarketCore.Engine.Detectors;
using MarketCore.Models;
using MarketCore.Providers.Simulator;
using MarketCore.Providers.Nelogica;
using MarketCore.Contracts;
using MarketCore.FlowSense;

namespace MarketCore.WPF
{
    
    public class TapeRecord
    {
        public string? Time { get; set; }
        public string? Broker { get; set; }
        public string? Price { get; set; }
        public string? Volume { get; set; }
        public string? Side { get; set; }
        public Brush PriceColor { get; set; } = new SolidColorBrush(Color.FromRgb(238, 238, 238));
        public Brush VolColor { get; set; } = new SolidColorBrush(Color.FromRgb(204, 204, 204));
        public Brush SideColor { get; set; } = new SolidColorBrush(Color.FromRgb(0, 200, 83));
        public Brush RowBg { get; set; } = new SolidColorBrush(Color.FromRgb(10, 10, 10));
        public string VolWeight { get; set; } = "Normal";
    }

public partial class MainWindow : Window
    {
        // ── Engine ────────────────────────────────────────────────────────────
        private MarketEngine _engine = null!;
        private SimulatorProvider _simulator = null!;

        // ── Estado ────────────────────────────────────────────────────────────
        private long _tradeCount;
        private long _bookCount;
        private long _delta;
        private int _spoofCount;
        private int _icebergCount;
        private int _renewableCount;
        private int _exhaustionCount;
        private decimal _lastBid;
        private decimal _lastAsk;

        // ── Configurações ─────────────────────────────────────────────────────
        private int _levels = 30;
        private int _groupingPts = 0;          // 0 = sem agrupamento
        private int _highlightThreshold = 300;
        private ObservableCollection<TapeRecord> _tapeRecords = new();
        private decimal _tapeVolMin = 0;
        private decimal _tapeMoveMin = 0;
        private decimal _lastTradePrice = 0;   // Para calcular movimento de preço
        private readonly List<BrokerFilter> _activeFilters = new();

        // ── Detectores ativos por nível de preço ──────────────────────────────
        // Key: preço formatado, Value: bitfield (bit0=Spoof, bit1=Iceberg, bit2=Renewable)
        private readonly Dictionary<string, int> _detectorsByPrice = new();

        // ── Máximo volume no book (para calcular barras proporcionais) ─────────
        private double _maxBidVol = 1;
        private double _maxAskVol = 1;

        // ── Throttle da barra de pressão ──────────────────────────────────────
        private long _buyAggression;
        private long _sellAggression;

        // ── Janela móvel 1 ────────────────────────────────────────────────────
        private int _windowMinutes = 1;
        private readonly System.Collections.Generic.Queue<(DateTime Time, long Buy, long Sell)> _aggressionWindow = new();
        private long _windowBuy;
        private long _windowSell;

        // ── Janela móvel 2 ────────────────────────────────────────────────────
        private int _windowMinutes2 = 3;
        private readonly System.Collections.Generic.Queue<(DateTime Time, long Buy, long Sell)> _aggressionWindow2 = new();
        private long _windowBuy2;
        private long _windowSell2;

                // ── Timer UI ──────────────────────────────────────────────────────────
        private readonly DispatcherTimer _uiTimer;
        private readonly DispatcherTimer _clockTimer;
        private long _tradesLastSec;
        private long _booksLastSec;
        private long _tradesThisSec;
        private long _booksThisSec;

        // ── Tape scroll manual ────────────────────────────────────────────────
        private bool _userScrolledTape;

        // ── Último snapshot para re-render ao mudar filtros ───────────────────
        private BookSnapshot? _lastSnapshot;

        // ── Alertas: agrupamento ─────────────────────────────────────────────
        private readonly Dictionary<string, AlertViewModel> _alertByKey = new();
        
        // ── Filtros de volume mínimo por tipo de alerta ──────────────────────
        private int _spoofMinVol = 0;
        private int _icebergMinVol = 0;
        private int _renewableMinVol = 0;
        private int _exhaustionMinVol = 0;

        // ── FlowSense Engines ─────────────────────────────────────────────────
        private FlowScoreConfig _flowScoreConfig = null!;
        private BrokerAccumulator _brokerAccum = null!;
        private DeltaEngine _deltaEngine = null!;
        private BookAnalyzer _bookAnalyzer = null!;
        private DetectorAggregator _detectorAggregator = null!;
        private FlowScoreEngine _flowScoreEngine = null!;
        private DispatcherTimer _flowScoreTimer = null!;
        private readonly List<FlowScoreSnapshot> _flowScoreSnapshots = new();
        private string _recordingsPath = "";

        // ── ViewModels ────────────────────────────────────────────────────────
        public ObservableCollection<BookRowViewModel> BookRows { get; } = new();
        public ObservableCollection<AlertViewModel> AlertItems { get; } = new();
        public ObservableCollection<BrokerFilter> ActiveFilters { get; } = new();
        private readonly ObservableCollection<SpoofNotificationViewModel> _spoofNotifications = new();

        // ── Credenciais Profit ────────────────────────────────────────────────
        private readonly ProfitCredentials _profitCredentials;
        private readonly bool _isRealMarket;

        // ─────────────────────────────────────────────────────────────────────
        public MainWindow(ProfitCredentials credentials, bool isRealMarket)
        {
            _profitCredentials = credentials;
            _isRealMarket      = isRealMarket;

            InitializeComponent();
            DataContext = this;

            IcActiveFilters.ItemsSource = ActiveFilters;

            // Timers
            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _uiTimer.Tick += UiTimer_Tick;
            _uiTimer.Start();

            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += (_, _) =>
            {
                TbClock.Text = DateTime.Now.ToString("HH:mm:ss");
                _tradesLastSec = _tradesThisSec;
                _booksLastSec  = _booksThisSec;
                _tradesThisSec = 0;
                _booksThisSec  = 0;
            };
            _clockTimer.Start();

            Loaded += MainWindow_Loaded;

            // Registrar eventos dos controles (sem event handlers no XAML)
            BtnAddFilter.Click             += BtnAddFilter_Click;
            CbWindowPeriod.SelectionChanged  += CbWindowPeriod_SelectionChanged;
            CbWindowPeriod2.SelectionChanged += CbWindowPeriod2_SelectionChanged;
            BtnClearFilters.Click          += BtnClearFilters_Click;
            CbLevels.SelectionChanged      += CbLevels_SelectionChanged;
            CbGrouping.SelectionChanged    += CbGrouping_SelectionChanged;
            TxHighlightThreshold.LostFocus += TxHighlightThreshold_LostFocus;
            TxTapeVolMin.TextChanged += TxTapeVolMin_TextChanged;
            TxTapeMoveMin.TextChanged += TxTapeMoveMin_TextChanged;
            TapeScrollViewer.ScrollChanged += TapeScrollViewer_ScrollChanged;
            BtnClearAlerts.Click           += BtnClearAlerts_Click;
            
            // ═══ Event handlers do Popup de Configuração de Alertas ═══
            BtnConfigAlerts.Click += (s, e) => PopupConfigAlerts.IsOpen = true;
            BtnApplyAlertConfig.Click += (s, e) =>
            {
                // Atualizar filtros
                if (int.TryParse(TxSpoofMinVol.Text, out int sVal)) _spoofMinVol = sVal;
                if (int.TryParse(TxIcebergMinVol.Text, out int iVal)) _icebergMinVol = iVal;
                if (int.TryParse(TxRenewableMinVol.Text, out int rVal)) _renewableMinVol = rVal;
                if (int.TryParse(TxExhaustionMinVol.Text, out int eVal)) _exhaustionMinVol = eVal;
                
                // ═══ LIMPAR TODOS OS DETECTORES ANTIGOS ═══
                lock (_detectorsByPrice)
                {
                    _detectorsByPrice.Clear();
                }
                
                // Re-renderizar o book para remover os indicadores antigos
                if (_lastSnapshot != null)
                {
                    Dispatcher.InvokeAsync(() => RenderBook(_lastSnapshot));
                }
                
                PopupConfigAlerts.IsOpen = false;
                
                // Mostrar mensagem de confirmação
                MessageBox.Show(
                    $"Filtros aplicados com sucesso!\n\n" +
                    $"Spoof (S): >= {_spoofMinVol} lotes\n" +
                    $"Iceberg (I): >= {_icebergMinVol} lotes\n" +
                    $"Renewable (R): >= {_renewableMinVol} lotes\n" +
                    $"Exhaustion (E): >= {_exhaustionMinVol} lotes\n\n" +
                    $"Detectores antigos foram limpos do book.",
                    "Configuração de Alertas",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information
                );
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // ── Vincular ItemsSource da Tape ──
            IcTape.ItemsSource = _tapeRecords;
            IcSpoofNotifications.ItemsSource = _spoofNotifications;
            BtnPower.Click += BtnPower_Click;
            BtnRecordingConfig.Click += BtnRecordingConfig_Click;

            // ── Escolher provider conforme modo de operação ──
            IMarketDataProvider provider;
            if (_isRealMarket)
            {
                provider = new ProfitDLLProvider();
            }
            else
            {
                _simulator = new SimulatorProvider();
                provider   = _simulator;
            }

            _engine = new MarketEngine(provider);

            _engine.OnTrade          += Engine_OnTrade;
            _engine.OnBookSnapshot   += Engine_OnBookSnapshot;
            _engine.OnConnectionChanged += Engine_OnConnectionChanged;
            _engine.Spoof.OnSpoofDetected           += (d) => HandleSpoof(d);
            _engine.Iceberg.OnIcebergDetected       += (d) => HandleIceberg(d);
            _engine.Renewable.OnRenewableDetected   += (d) => HandleRenewable(d);
            _engine.Exhaustion.OnExhaustionDetected += (d) => HandleExhaustion(d);

            var providerCredentials = _isRealMarket
                ? new ProviderCredentials(
                    _profitCredentials.ActivationKey,
                    _profitCredentials.Username,
                    _profitCredentials.Password)
                : new ProviderCredentials("", "", "");

            // ── Inicializar FlowSense ──────────────────────────────────────
            _flowScoreConfig    = new FlowScoreConfig();
            _brokerAccum       = new BrokerAccumulator();
            _deltaEngine       = new DeltaEngine();
            _bookAnalyzer      = new BookAnalyzer();
            _detectorAggregator = new DetectorAggregator();
            _flowScoreEngine   = new FlowScoreEngine(_brokerAccum, _deltaEngine, _bookAnalyzer, _detectorAggregator, _flowScoreConfig);

            // Timer para gravar FlowScore a cada 1 segundo
            _flowScoreTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _flowScoreTimer.Tick += (_, _) =>
            {
                _flowScoreEngine.CalculateScore();

                var snap = new FlowScoreSnapshot
                {
                    Timestamp   = DateTime.Now,
                    Preco       = (double)(_lastBid > 0 ? _lastBid : _lastAsk),
                    ScoreTotal  = _flowScoreEngine.FlowScore,
                    BrokerFlow  = _flowScoreEngine.BrokerFlowComponent,
                    FluxoDireto = _flowScoreEngine.FluxoDirectoComponent,
                    Book        = _flowScoreEngine.BookComponent,
                    Detectores  = _flowScoreEngine.DetectoresComponent
                };
                _flowScoreSnapshots.Add(snap);
                if (_flowScoreSnapshots.Count > 7200) _flowScoreSnapshots.RemoveAt(0);
                _engine.GravarFlowScore(snap.Preco, snap.ScoreTotal, snap.BrokerFlow,
                                        snap.FluxoDireto, snap.Book, snap.Detectores);
            };
            _flowScoreTimer.Start();

            // Inicializar painel FlowScore com as engines e snapshots
            FlowScorePanelControl.Initialize(_flowScoreEngine, _brokerAccum, _deltaEngine,
                                              _flowScoreSnapshots, _recordingsPath);

            // ── Ativar gravação automática ──────────────────────────────────
            var recordingConfig = RecordingConfig.Load();
            var recordingsPath  = recordingConfig.RecordingsPath;
            _recordingsPath     = recordingsPath;

            // Se o drive não existir (HD externo desconectado), usa padrão
            var root = System.IO.Path.GetPathRoot(recordingsPath);
            if (!string.IsNullOrEmpty(root) && !Directory.Exists(root))
            {
                recordingsPath = RecordingConfig.GetDefaultPath();
                // Drive não encontrado — usando caminho padrão
            }

            bool isSimulator = _engine.ProviderName == "Simulator";
_engine.HabilitarGravacao(recordingsPath, isSimulator);

            _engine.ConnectAsync(providerCredentials).ContinueWith(t =>
{
    if (t.IsFaulted)
        Console.WriteLine($"[ConnectAsync] ERRO: {t.Exception?.InnerException?.Message}");
});
            _engine.Subscribe(TbTicker.Text.Trim().ToUpper());
        }

        // ══════════════════════════════════════════════════════════════════════
        //  CALLBACKS DA ENGINE
        // ══════════════════════════════════════════════════════════════════════

        private void Engine_OnConnectionChanged(ConnectionChangedEvent evt)
        {
            Dispatcher.InvokeAsync(() =>
            {
                bool connected = evt.Status == ConnectionStatus.Connected;
                EllipseConnection.Fill = connected
                    ? new SolidColorBrush(Color.FromRgb(0, 200, 83))
                    : new SolidColorBrush(Color.FromRgb(255, 23, 68));
                TbConnectionStatus.Text = connected ? "CONECTADO" : evt.Status.ToString().ToUpper();
            });
        }

        private void Engine_OnTrade(TradeEvent trade)
        {
            _tradeCount++;
            _tradesThisSec++;

            // FlowCandle Renko - integrado ao Engine
            bool isBuyFlow = trade.Aggressor == TradeAggressor.Buy;
            Dispatcher.InvokeAsync(() => flowCandleChart.ProcessTrade((double)trade.Price, (int)trade.Volume, isBuyFlow));


            // ──── Acumuladores de Delta e Pressão (sempre executa) ────
            if (trade.Aggressor == TradeAggressor.Buy)
            {
                _buyAggression  += trade.Volume;
                _delta          += trade.Volume;
            }
            else
            {
                _sellAggression += trade.Volume;
                _delta          -= trade.Volume;
            }

            // ──── Janelas Móveis (sempre executa) ────
            var buyVol  = trade.Aggressor == TradeAggressor.Buy  ? trade.Volume : 0;
            var sellVol = trade.Aggressor == TradeAggressor.Sell ? trade.Volume : 0;

            lock (_aggressionWindow)
            {
                _aggressionWindow.Enqueue((trade.Time, buyVol, sellVol));
                _windowBuy  += buyVol;
                _windowSell += sellVol;
                var cutoff = trade.Time.AddMinutes(-_windowMinutes);
                while (_aggressionWindow.Count > 0 && _aggressionWindow.Peek().Time < cutoff)
                {
                    var removed = _aggressionWindow.Dequeue();
                    _windowBuy  = Math.Max(0, _windowBuy  - removed.Buy);
                    _windowSell = Math.Max(0, _windowSell - removed.Sell);
                }
            }

            lock (_aggressionWindow2)
            {
                _aggressionWindow2.Enqueue((trade.Time, buyVol, sellVol));
                _windowBuy2  += buyVol;
                _windowSell2 += sellVol;
                var cutoff2 = trade.Time.AddMinutes(-_windowMinutes2);
                while (_aggressionWindow2.Count > 0 && _aggressionWindow2.Peek().Time < cutoff2)
                {
                    var removed = _aggressionWindow2.Dequeue();
                    _windowBuy2  = Math.Max(0, _windowBuy2  - removed.Buy);
                    _windowSell2 = Math.Max(0, _windowSell2 - removed.Sell);
                }
            }

            // ──── FlowSense — alimenta BrokerAccumulator e DeltaEngine ────
            _brokerAccum.OnTrade(
                trade.Broker,
                (double)trade.Volume,
                trade.Aggressor == TradeAggressor.Buy,
                trade.Time);

            _deltaEngine.OnTrade(
                (double)trade.Price,
                trade.Aggressor == TradeAggressor.Buy  ? (double)trade.Volume : 0,
                trade.Aggressor == TradeAggressor.Sell ? (double)trade.Volume : 0,
                trade.Time);

            // ──── FILTRO DE VOLUME MÍNIMO ────
            if (_tapeVolMin > 0 && trade.Volume < _tapeVolMin)
                return;

            // ──── FILTRO DE MOVIMENTO DE PREÇO ────
            decimal priceMove = 0;
            if (_lastTradePrice > 0)
            {
                priceMove = Math.Abs(trade.Price - _lastTradePrice);
                if (_tapeMoveMin > 0 && priceMove < _tapeMoveMin)
                    return;
            }
            _lastTradePrice = trade.Price;

            // ──── ADICIONAR À TAPE (thread-safe) ────
            Dispatcher.Invoke(() =>
            {
                _tapeRecords.Insert(0, new TapeRecord
                {
                    Time = trade.Time.ToString("HH:mm:ss"),
                    Broker = trade.Broker.Length > 6 ? trade.Broker[..6] : trade.Broker,
                    Price = trade.Price.ToString("N0"),
                    Volume = trade.Volume.ToString(),
                    Side = trade.Aggressor == TradeAggressor.Buy ? "Compra" : "Venda",
                    PriceColor = new SolidColorBrush(Color.FromRgb(238, 238, 238)),
                    VolColor = trade.Volume >= 500
                        ? new SolidColorBrush(Color.FromRgb(0, 200, 83))
                        : new SolidColorBrush(Color.FromRgb(204, 204, 204)),
                    SideColor = trade.Aggressor == TradeAggressor.Buy
                        ? new SolidColorBrush(Color.FromRgb(0, 200, 83))
                        : new SolidColorBrush(Color.FromRgb(255, 23, 68)),
                    RowBg = new SolidColorBrush(Color.FromRgb(10, 10, 10)),
                    VolWeight = trade.Volume >= 500 ? "Bold" : "Normal"
                });

                // Limitar a 500 trades
                while (_tapeRecords.Count > 500)
                    _tapeRecords.RemoveAt(_tapeRecords.Count - 1);

                // Atualizar contador
                TbTapeTotal.Text = $"{_tradeCount} trades";

                // Autoscroll se habilitado
                if (ChkAutoscroll.IsChecked == true && !_userScrolledTape)
                    TapeScrollViewer.ScrollToBottom();
            });
        }

        private void Engine_OnBookSnapshot(BookSnapshot snapshot)
        {
            _bookCount++;
            _booksThisSec++;

            if (snapshot.Bids.Count == 0 && snapshot.Asks.Count == 0) return;

            _lastBid = snapshot.Bids.Count > 0 ? snapshot.Bids[0].Price : 0;
            _lastAsk = snapshot.Asks.Count > 0 ? snapshot.Asks[0].Price : 0;

            _lastSnapshot = snapshot;

            // ── FlowSense — alimenta BookAnalyzer e DetectorAggregator ──
            var bidPrices = snapshot.Bids.Select(b => (double)b.Price).ToList();
            var bidQtys   = snapshot.Bids.Select(b => (double)b.Volume).ToList();
            var askPrices = snapshot.Asks.Select(a => (double)a.Price).ToList();
            var askQtys   = snapshot.Asks.Select(a => (double)a.Volume).ToList();

            _bookAnalyzer.OnBookSnapshot(bidPrices, bidQtys, askPrices, askQtys);
            _bookAnalyzer.SetVWAPDistance((double)_lastBid, _deltaEngine.SessionVWAP);

            Dispatcher.InvokeAsync(() => RenderBook(snapshot));
        }

        // ══════════════════════════════════════════════════════════════════════
        //  RENDERIZAÇÃO DO BOOK BILATERAL
        // ══════════════════════════════════════════════════════════════════════

        // Verifica se um nível passa nos filtros ativos
        private bool PassaFiltro(BookLevel level)
        {
            if (_activeFilters.Count == 0) return true;
            return _activeFilters.Any(f =>
                (f.Broker == "(todas)" || string.Equals(f.Broker, level.Broker, StringComparison.OrdinalIgnoreCase))
                && level.Volume >= f.VolMin
                && level.Volume <= f.VolMax);
        }

        private void RenderBook(BookSnapshot snapshot)
        {
            int levels = _levels;

            // Aplicar filtros: se há filtros ativos, manter só níveis que passam
            var bids = snapshot.Bids
                .Where(b => PassaFiltro(b))
                .Take(levels).ToList();
            var asks = snapshot.Asks
                .Where(a => PassaFiltro(a))
                .Take(levels).ToList();

            // Calcular máximo de volume para barras proporcionais
            _maxBidVol = bids.Count > 0 ? (double)bids.Max(b => b.Volume) : 1;
            _maxAskVol = asks.Count > 0 ? (double)asks.Max(a => a.Volume) : 1;
            double maxVol = Math.Max(_maxBidVol, _maxAskVol);
            if (maxVol < 1) maxVol = 1;

            int rows = Math.Max(bids.Count, asks.Count);

            while (BookRows.Count < rows) BookRows.Add(new BookRowViewModel());
            while (BookRows.Count > rows) BookRows.RemoveAt(BookRows.Count - 1);

            double maxBarWidth = 80;

            for (int i = 0; i < rows; i++)
            {
                var row = BookRows[i];

                var bid = i < bids.Count ? bids[i] : null;
                var ask = i < asks.Count ? asks[i] : null;

                // ── BID ──
                if (bid != null)
                {
                    string bidPriceKey = bid.Price.ToString("N0");
                    bool isBigBid = bid.Volume >= _highlightThreshold;
                    bool bidSpoof     = (_detectorsByPrice.TryGetValue(bidPriceKey, out int bdflags) && (bdflags & 1) != 0);
                    bool bidIceberg   = (bdflags & 2) != 0;
                    bool bidRenewable = (bdflags & 4) != 0;
                    bool bidExhaustion = (bdflags & 8) != 0;

                    row.BidBroker     = bid.Broker.Length > 7 ? bid.Broker[..7] : bid.Broker;
                    row.BidVolume     = bid.Volume.ToString();
                    row.BidPrice      = bidPriceKey;
                    row.BidVolColor   = isBigBid ? "#00E676" : "#CCCCCC";
                    row.BidVolWeight  = isBigBid ? FontWeights.Bold : FontWeights.Normal;
                    row.BidBarWidth   = (double)bid.Volume / maxVol * maxBarWidth;
                    row.BidBarColor   = isBigBid ? "#00E676" : "#00C853";
                    row.BidBarOpacity = isBigBid ? 0.9 : 0.45;
                    row.BidSpoofColor   = bidSpoof   ? "#FF1744" : "#1A1A1A";
                    row.BidIcebergColor = bidIceberg ? "#2979FF" : "#1A1A1A";
                    
                    // ═══ NOVO: Montar texto do detector ═══
                    string bidDetectorText = "";
                    string bidDetectorColor = "#FFFFFF";
                    if (bidSpoof)      { bidDetectorText += "S"; bidDetectorColor = "#FF1744"; }
                    else if (bidIceberg)    { bidDetectorText += "I"; bidDetectorColor = "#2979FF"; }
                    else if (bidRenewable)  { bidDetectorText += "R"; bidDetectorColor = "#00C853"; }
                    else if (bidExhaustion) { bidDetectorText += "E"; bidDetectorColor = "#FFD600"; }
                    row.BidDetector = bidDetectorText;
                    row.BidDetectorColor = bidDetectorColor;
                    
                    row.RowBackground = isBigBid
                        ? new SolidColorBrush(Color.FromArgb(40, 0, 200, 83))
                        : Brushes.Transparent;
                }
                else
                {
                    row.BidBroker = ""; row.BidVolume = ""; row.BidPrice = "";
                    row.BidBarWidth = 0;
                    row.BidSpoofColor = "#1A1A1A"; row.BidIcebergColor = "#1A1A1A";
                    row.BidDetector = "";
                    row.RowBackground = Brushes.Transparent;
                }

                // ── ASK ──
                if (ask != null)
                {
                    string askPriceKey = ask.Price.ToString("N0");
                    bool isBigAsk   = ask.Volume >= _highlightThreshold;
                    bool askSpoof     = (_detectorsByPrice.TryGetValue(askPriceKey, out int adflags) && (adflags & 1) != 0);
                    bool askIceberg   = (adflags & 2) != 0;
                    bool askRenewable = (adflags & 4) != 0;
                    bool askExhaustion = (adflags & 8) != 0;

                    row.AskBroker     = ask.Broker.Length > 7 ? ask.Broker[..7] : ask.Broker;
                    row.AskVolume     = ask.Volume.ToString();
                    row.AskPrice      = askPriceKey;
                    row.AskVolColor   = isBigAsk ? "#FF4569" : "#CCCCCC";
                    row.AskVolWeight  = isBigAsk ? FontWeights.Bold : FontWeights.Normal;
                    row.AskBarWidth   = (double)ask.Volume / maxVol * maxBarWidth;
                    row.AskBarColor   = isBigAsk ? "#FF4569" : "#FF1744";
                    row.AskBarOpacity = isBigAsk ? 0.9 : 0.45;
                    row.AskSpoofColor   = askSpoof   ? "#FF1744" : "#1A1A1A";
                    row.AskIcebergColor = askIceberg ? "#2979FF" : "#1A1A1A";
                    
                    // ═══ NOVO: Montar texto do detector ═══
                    string askDetectorText = "";
                    string askDetectorColor = "#FFFFFF";
                    if (askSpoof)      { askDetectorText += "S"; askDetectorColor = "#FF1744"; }
                    else if (askIceberg)    { askDetectorText += "I"; askDetectorColor = "#2979FF"; }
                    else if (askRenewable)  { askDetectorText += "R"; askDetectorColor = "#00C853"; }
                    else if (askExhaustion) { askDetectorText += "E"; askDetectorColor = "#FFD600"; }
                    row.AskDetector = askDetectorText;
                    row.AskDetectorColor = askDetectorColor;
                    
                    if (isBigAsk)
                        row.RowBackground = new SolidColorBrush(Color.FromArgb(40, 255, 23, 68));
                }
                else
                {
                    row.AskBroker = ""; row.AskVolume = ""; row.AskPrice = "";
                    row.AskBarWidth = 0;
                    row.AskSpoofColor = "#1A1A1A"; row.AskIcebergColor = "#1A1A1A";
                    row.AskDetector = "";
                }
            }

            // Spread
            if (_lastBid > 0 && _lastAsk > 0)
            {
                decimal spread = (_lastAsk - _lastBid) / 5m;
                TbSpread.Text = $"{spread:N0} pts";
            }
            TbDepth.Text = $"{rows} níveis";

            // Preço last
            if (_lastBid > 0)
            {
                TbLastPrice.Text = _lastBid.ToString("N0");
                TbLastPrice.Foreground = _delta >= 0
                    ? new SolidColorBrush(Color.FromRgb(0, 200, 83))
                    : new SolidColorBrush(Color.FromRgb(255, 23, 68));
            }

            // Footer bid/ask
            TbFooterBid.Text = _lastBid > 0 ? _lastBid.ToString("N0") : "--";
            TbFooterAsk.Text = _lastAsk > 0 ? _lastAsk.ToString("N0") : "--";
        }

        // ══════════════════════════════════════════════════════════════════════
        //  TIMER UI — atualiza elementos não críticos a 5Hz
        // ══════════════════════════════════════════════════════════════════════

        private void UiTimer_Tick(object? sender, EventArgs e)
        {
            // Delta
            string deltaStr = _delta >= 0 ? $"+{_delta}" : $"{_delta}";
            TbDelta.Text = deltaStr;
            TbDelta.Foreground = _delta >= 0
                ? new SolidColorBrush(Color.FromRgb(0, 200, 83))
                : new SolidColorBrush(Color.FromRgb(255, 23, 68));
            TbFooterDelta.Text = deltaStr;
            TbFooterDelta.Foreground = TbDelta.Foreground;

            // Barra de pressão
            long total = _buyAggression + _sellAggression;
            if (total > 0)
            {
                double buyPct = (double)_buyAggression / total;
                double sellPct = 1.0 - buyPct;

                TbBuyPct.Text  = $"Comp {buyPct:P0}";
                TbSellPct.Text = $"Vend {sellPct:P0}";

                // Calcular largura das barras
                var pressureContainer = BidPressureBar.Parent as Grid;
                double containerWidth = pressureContainer?.ActualWidth ?? 200;
                BidPressureBar.Width = Math.Max(0, containerWidth * buyPct);
                AskPressureBar.Width = Math.Max(0, containerWidth * sellPct);
            }

            // Contadores detectores
            TbSpoofCount.Text      = _spoofCount.ToString();
            TbIcebergCount.Text    = _icebergCount.ToString();
            TbRenewableCount.Text  = _renewableCount.ToString();
            TbExhaustionCount.Text = _exhaustionCount.ToString();

            // Barra janela móvel 2
            long w2Total = _windowBuy2 + _windowSell2;
            if (w2Total > 0)
            {
                double w2BuyPct  = (double)_windowBuy2  / w2Total;
                double w2SellPct = (double)_windowSell2 / w2Total;
                TbBuyPctWindow2.Text  = $"Comp {w2BuyPct:P0}";
                TbSellPctWindow2.Text = $"Vend {w2SellPct:P0}";
                var w2Container = BidWindow2Bar.Parent as Grid;
                double w2Width  = w2Container?.ActualWidth ?? 200;
                BidWindow2Bar.Width = w2Width * w2BuyPct;
                AskWindow2Bar.Width = w2Width * w2SellPct;
            }

            // Barra janela móvel 1
            long wTotal = _windowBuy + _windowSell;
            if (wTotal > 0)
            {
                double wBuyPct  = (double)_windowBuy  / wTotal;
                double wSellPct = (double)_windowSell / wTotal;
                TbBuyPctWindow.Text  = $"Comp {wBuyPct:P0}";
                TbSellPctWindow.Text = $"Vend {wSellPct:P0}";
                var wContainer = BidWindowBar.Parent as Grid;
                double wWidth  = wContainer?.ActualWidth ?? 200;
                BidWindowBar.Width = wWidth * wBuyPct;
                AskWindowBar.Width = wWidth * wSellPct;
                TbBuyPctWindow.Foreground  = wBuyPct > 0.6
                    ? new SolidColorBrush(Color.FromRgb(0, 230, 118))
                    : new SolidColorBrush(Color.FromRgb(0, 180, 80));
                TbSellPctWindow.Foreground = wSellPct > 0.6
                    ? new SolidColorBrush(Color.FromRgb(255, 80, 80))
                    : new SolidColorBrush(Color.FromRgb(200, 50, 50));
            }

            // Observabilidade
            TbTradesPerSec.Text = $"{_tradesLastSec}/s";
            TbBooksPerSec.Text  = $"{_booksLastSec}/s";
            TbHeapMb.Text       = $"{GC.GetTotalMemory(false) / 1024.0 / 1024.0:N1} MB";

            // Status bar
            TbTradeCount.Text = _tradeCount.ToString();
            TbBookCount.Text  = _bookCount.ToString();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  DETECTORES
        // ══════════════════════════════════════════════════════════════════════

        private void HandleSpoof(SpoofEvent d)
        {
            if (_spoofMinVol > 0 && d.VolumeBefore < _spoofMinVol) return;

            string key = d.Price.ToString("N0");

            // O spoof ocorre quando a ordem some — preço pode não estar mais no book.
            // Marca o preço exato E o preço vizinho mais próximo visível no book.
            MarkPriceDetector(key, 0);

            if (_lastSnapshot != null)
            {
                if (d.Side == "COMPRA")
                {
                    // Busca bid mais próximo do preço do spoof
                    var nearest = _lastSnapshot.Bids
                        .OrderBy(b => Math.Abs((double)(b.Price - d.Price)))
                        .FirstOrDefault();
                    if (nearest != null)
                        MarkPriceDetector(nearest.Price.ToString("N0"), 0);
                }
                else
                {
                    // Busca ask mais próximo do preço do spoof
                    var nearest = _lastSnapshot.Asks
                        .OrderBy(a => Math.Abs((double)(a.Price - d.Price)))
                        .FirstOrDefault();
                    if (nearest != null)
                        MarkPriceDetector(nearest.Price.ToString("N0"), 0);
                }
            }

            Dispatcher.InvokeAsync(() =>
            {
                _spoofCount++;
                AddAlert("S", d.Price, $"{d.Side} | {d.Broker} | {d.VolumeBefore}→{d.VolumeAfter}");
                AddSpoofNotification(d.Side, d.Broker, d.VolumeBefore, d.Price, isCyclic: false);
            });
            ClearPriceDetectorAfter(key, 0);
        }

        private void AddSpoofNotification(string side, string broker, int vol, decimal price, bool isCyclic)
        {
            bool isCompra      = side.Contains("COMPRA") || side == "C";
            string brokerShort = broker.Length > 6 ? broker[..6] : broker;

            var vm = new SpoofNotificationViewModel
            {
                Time       = DateTime.Now.ToString("HH:mm:ss"),
                SideLetter = isCompra ? "C" : "V",
                SideColor  = isCompra ? "#00E676" : "#FF4444",
                Broker     = brokerShort.ToUpper(),
                Vol        = vol.ToString(),
                Price      = price.ToString("N0"),
                TypeLabel  = isCyclic ? "CICL" : "CLASS",
                TypeColor  = isCyclic ? "#FFD600" : "#FF6B6B",
                RowBg      = "Transparent"
            };

            _spoofNotifications.Insert(0, vm);
            while (_spoofNotifications.Count > 8)
                _spoofNotifications.RemoveAt(_spoofNotifications.Count - 1);

            // Esconde placeholder quando há notificações
            if (TbSpoofEmpty != null)
                TbSpoofEmpty.Visibility = Visibility.Collapsed;
        }

        private void HandleIceberg(IcebergEvent d)
        {
            if (_icebergMinVol > 0 && d.Volume < _icebergMinVol) return;

            string key = d.FromPrice.ToString("N0");
            MarkPriceDetector(key, 1);
            Dispatcher.InvokeAsync(() =>
            {
                _icebergCount++;
                AddAlert("I", d.FromPrice, $"{d.Direction} | {d.Broker} | vol:{d.Volume}");
            });
            ClearPriceDetectorAfter(key, 1);
        }

        private void HandleRenewable(RenewableEvent d)
        {
            if (_renewableMinVol > 0 && d.VolumePerCycle < _renewableMinVol) return;

            string key = d.Price.ToString("N0");
            MarkPriceDetector(key, 2);
            Dispatcher.InvokeAsync(() =>
            {
                _renewableCount++;
                AddAlert("R", d.Price, $"{d.Side} | {d.Broker} | {d.Renewals}x renovações");
            });
            ClearPriceDetectorAfter(key, 2);
        }

        private void HandleExhaustion(ExhaustionEvent d)
        {
            if (_exhaustionMinVol > 0 && d.NumTrades < _exhaustionMinVol) return;

            string key = d.PrecoInicial.ToString("N0");
            MarkPriceDetector(key, 3);
            Dispatcher.InvokeAsync(() =>
            {
                _exhaustionCount++;
                AddAlert("E", d.PrecoInicial, $"{d.LadoAgressor} | {d.Ticker} | {d.NumTrades} trades");
            });
            ClearPriceDetectorAfter(key, 3);
        }

        private void MarkPriceDetector(string priceKey, int bit)
        {
            lock (_detectorsByPrice)
            {
                if (!_detectorsByPrice.ContainsKey(priceKey)) _detectorsByPrice[priceKey] = 0;
                _detectorsByPrice[priceKey] |= (1 << bit);
            }
        }

        private void ClearPriceDetectorAfter(string priceKey, int bit)
        {
            _ = System.Threading.Tasks.Task.Delay(30000).ContinueWith(_ =>
            {
                lock (_detectorsByPrice)
                {
                    if (_detectorsByPrice.ContainsKey(priceKey))
                        _detectorsByPrice[priceKey] &= ~(1 << bit);
                }
            });
        }

        // ══════════════════════════════════════════════════════════════════════
        //  ALERTAS — agrupamento por tipo+segundo
        // ══════════════════════════════════════════════════════════════════════

        private void AddAlert(string tag, decimal price, string description = "")
        {
            string key = $"{tag}_{DateTime.Now:HHmmss}";

            if (_alertByKey.TryGetValue(key, out var existing))
            {
                existing.Count++;
                existing.CountVisibility = Visibility.Visible;
                return;
            }

            string tagColor    = tag switch { "S" => "#FF1744", "I" => "#2979FF", "R" => "#00C853", _ => "#FFD600" };
            string bgColor     = tag switch { "S" => "#1A0808", "I" => "#08081A", "R" => "#081A08",  _ => "#1A1A08" };
            string borderColor = tag switch { "S" => "#330A0A", "I" => "#0A0A33", "R" => "#0A330A",  _ => "#33330A" };
            string titleColor  = tagColor;

            string title = tag switch
            {
                "S" => $"Spoof — {price:N0}",
                "I" => $"Iceberg — {price:N0}",
                "R" => $"Renewable — {price:N0}",
                _   => $"Exhaustion — {price:N0}"
            };

            var vm = new AlertViewModel
            {
                Tag             = tag,
                Title           = title,
                Description     = description,
                Time            = DateTime.Now.ToString("HH:mm:ss"),
                TagColor        = tagColor,
                BgColor         = bgColor,
                BorderColor     = borderColor,
                TitleColor      = titleColor,
                Count           = 1,
                CountVisibility = Visibility.Collapsed
            };

            _alertByKey[key] = vm;

            AlertItems.Insert(0, vm);

            // Manter máximo de 10 alertas únicos
            while (AlertItems.Count > 10)
            {
                var last = AlertItems[AlertItems.Count - 1];
                var lastKey = _alertByKey.FirstOrDefault(kvp => kvp.Value == last).Key;
                if (lastKey != null) _alertByKey.Remove(lastKey);
                AlertItems.RemoveAt(AlertItems.Count - 1);
            }

            AlertsScrollViewer.ScrollToTop();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  EVENT HANDLERS UI
        // ══════════════════════════════════════════════════════════════════════

        private void BtnRecordingConfig_Click(object sender, RoutedEventArgs e)
        {
            var window = new RecordingConfigWindow { Owner = this };
            window.ShowDialog();
        }

        private void BtnPower_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Deseja encerrar o MarketCore?",
                "Encerrar",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _engine?.Dispose();
                Application.Current.Shutdown();
            }
        }

        private void BtnAddFilter_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxFilterVolMin.Text, out int volMin)) volMin = 0;
            if (!int.TryParse(TxFilterVolMax.Text, out int volMax)) volMax = 9999;
            string broker = (CbFilterBroker.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "(todas)";

            var filter = new BrokerFilter
            {
                Broker = broker,
                VolMin = volMin,
                VolMax = volMax,
                DisplayText = $"{broker}  [{volMin}–{volMax}]"
            };

            ActiveFilters.Add(filter);
            _activeFilters.Add(filter);
            TbFilterStatus.Text = $"{_activeFilters.Count} filtro(s) ativo(s)";
            if (_lastSnapshot != null) RenderBook(_lastSnapshot);
        }

        // ═══ MÉTODO QUE ESTAVA FALTANDO ═══
        private void BtnRemoveFilter_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is BrokerFilter f)
            {
                ActiveFilters.Remove(f);
                _activeFilters.Remove(f);
                if (_activeFilters.Count == 0)
                    TbFilterStatus.Text = "Nenhum filtro ativo — todas as ordens exibidas";
                else
                    TbFilterStatus.Text = $"{_activeFilters.Count} filtro(s) ativo(s)";
                if (_lastSnapshot != null) RenderBook(_lastSnapshot);
            }
        }

        private void BtnClearFilters_Click(object sender, RoutedEventArgs e)
        {
            ActiveFilters.Clear();
            _activeFilters.Clear();
            TbFilterStatus.Text = "Nenhum filtro ativo — todas as ordens exibidas";
            if (_lastSnapshot != null) RenderBook(_lastSnapshot);
        }

        private void CbLevels_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CbLevels.SelectedItem is ComboBoxItem item &&
                int.TryParse(item.Content?.ToString(), out int lvl))
                _levels = lvl;
        }

        private void CbGrouping_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CbGrouping.SelectedItem is ComboBoxItem item)
            {
                var txt = item.Content?.ToString() ?? "0";
                _groupingPts = int.TryParse(txt, out int g) ? g : 0;
            }
        }

        private void TxHighlightThreshold_LostFocus(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(TxHighlightThreshold.Text, out int t))
                _highlightThreshold = t;
        }

        private void CbWindowPeriod2_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CbWindowPeriod2.SelectedItem is ComboBoxItem item)
            {
                var txt = item.Content?.ToString() ?? "5 min";
                _windowMinutes2 = int.Parse(txt.Split(' ')[0]);
                lock (_aggressionWindow2) { _aggressionWindow2.Clear(); _windowBuy2 = 0; _windowSell2 = 0; }
            }
        }

        private void CbWindowPeriod_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CbWindowPeriod.SelectedItem is ComboBoxItem item)
            {
                var txt = item.Content?.ToString() ?? "1 min";
                _windowMinutes = int.Parse(txt.Split(' ')[0]);
                // Limpar fila ao mudar período
                lock (_aggressionWindow)
                {
                    _aggressionWindow.Clear();
                    _windowBuy  = 0;
                    _windowSell = 0;
                }
            }
        }

        private void TxTapeVolMin_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (decimal.TryParse(TxTapeVolMin.Text, out var val))
                _tapeVolMin = val;
        }

        private void TxTapeMoveMin_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (decimal.TryParse(TxTapeMoveMin.Text, out var val))
                _tapeMoveMin = val;
        }

        private void TapeScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // Se o usuário rolou manualmente (não foi autoscroll)
            if (e.ExtentHeightChange == 0)
                _userScrolledTape = TapeScrollViewer.VerticalOffset < TapeScrollViewer.ScrollableHeight - 5;
            else if (!_userScrolledTape)
                TapeScrollViewer.ScrollToBottom();
        }

        private void BtnClearAlerts_Click(object sender, RoutedEventArgs e)
        {
            AlertItems.Clear();
            _alertByKey.Clear();
        }

        protected override void OnClosed(EventArgs e)
        {
            _ = _engine?.DisconnectAsync();
            _uiTimer?.Stop();
            _clockTimer?.Stop();
            _flowScoreTimer?.Stop();
            _brokerAccum?.Stop();
            base.OnClosed(e);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  TROCA DE ATIVO — pressionar Enter no campo do ticker
        // ══════════════════════════════════════════════════════════════════════

        private void TbTicker_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;

            var novoTicker = TbTicker.Text.Trim().ToUpper();
            if (string.IsNullOrEmpty(novoTicker)) return;

            // Desincreve ticker anterior
            _engine.Unsubscribe(TbTicker.Text.Trim().ToUpper());

            // Limpa dados da tela
            Dispatcher.Invoke(() =>
            {
                TbLastPrice.Text = "--";
                TbFooterBid.Text = "--";
                TbFooterAsk.Text = "--";
                TbSpread.Text    = "-- pts";
                _tapeRecords.Clear();
                BookRows.Clear();
                _delta          = 0;
                _buyAggression  = 0;
                _sellAggression = 0;
                _lastBid        = 0;
                _lastAsk        = 0;
                _lastSnapshot   = null;
                lock (_aggressionWindow)  { _aggressionWindow.Clear();  _windowBuy  = 0; _windowSell  = 0; }
                lock (_aggressionWindow2) { _aggressionWindow2.Clear(); _windowBuy2 = 0; _windowSell2 = 0; }
            });

            // Subscreve novo ticker
            _engine.Subscribe(novoTicker);

            // Tira o foco do campo
            TbTicker.MoveFocus(new System.Windows.Input.TraversalRequest(
                System.Windows.Input.FocusNavigationDirection.Next));
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  VIEW MODELS
    // ══════════════════════════════════════════════════════════════════════════

    public class BookRowViewModel : INotifyPropertyChanged
    {
        private string _bidBroker = "";
        private string _bidVolume = "";
        private string _bidPrice  = "";
        private string _askBroker = "";
        private string _askVolume = "";
        private string _askPrice  = "";
        private string _bidVolColor  = "#AAAAAA";
        private string _askVolColor  = "#AAAAAA";
        private FontWeight _bidVolWeight = FontWeights.Normal;
        private FontWeight _askVolWeight = FontWeights.Normal;
        private double _bidBarWidth  = 0;
        private double _askBarWidth  = 0;
        private string _bidBarColor  = "#00C853";
        private string _askBarColor  = "#FF1744";
        private double _bidBarOpacity = 0.5;
        private double _askBarOpacity = 0.5;
        private string _bidSpoofColor   = "#1A1A1A";
        private string _bidIcebergColor = "#1A1A1A";
        private string _askSpoofColor   = "#1A1A1A";
        private string _askIcebergColor = "#1A1A1A";
        private Brush  _rowBackground   = Brushes.Transparent;
        
        // ═══ NOVOS CAMPOS PARA INDICADORES DE TEXTO S/I/R ═══
        private string _bidDetector = "";
        private string _askDetector = "";
        private string _bidDetectorColor = "#FFFFFF";
        private string _askDetectorColor = "#FFFFFF";

        public string BidBroker     { get => _bidBroker;     set => Set(ref _bidBroker,     value); }
        public string BidVolume     { get => _bidVolume;     set => Set(ref _bidVolume,     value); }
        public string BidPrice      { get => _bidPrice;      set => Set(ref _bidPrice,      value); }
        public string AskBroker     { get => _askBroker;     set => Set(ref _askBroker,     value); }
        public string AskVolume     { get => _askVolume;     set => Set(ref _askVolume,     value); }
        public string AskPrice      { get => _askPrice;      set => Set(ref _askPrice,      value); }
        public string BidVolColor   { get => _bidVolColor;   set => Set(ref _bidVolColor,   value); }
        public string AskVolColor   { get => _askVolColor;   set => Set(ref _askVolColor,   value); }
        public FontWeight BidVolWeight { get => _bidVolWeight; set => Set(ref _bidVolWeight, value); }
        public FontWeight AskVolWeight { get => _askVolWeight; set => Set(ref _askVolWeight, value); }
        public double BidBarWidth   { get => _bidBarWidth;   set => Set(ref _bidBarWidth,   value); }
        public double AskBarWidth   { get => _askBarWidth;   set => Set(ref _askBarWidth,   value); }
        public string BidBarColor   { get => _bidBarColor;   set => Set(ref _bidBarColor,   value); }
        public string AskBarColor   { get => _askBarColor;   set => Set(ref _askBarColor,   value); }
        public double BidBarOpacity { get => _bidBarOpacity; set => Set(ref _bidBarOpacity, value); }
        public double AskBarOpacity { get => _askBarOpacity; set => Set(ref _askBarOpacity, value); }
        public string BidSpoofColor    { get => _bidSpoofColor;    set => Set(ref _bidSpoofColor,    value); }
        public string BidIcebergColor  { get => _bidIcebergColor;  set => Set(ref _bidIcebergColor,  value); }
        public string AskSpoofColor    { get => _askSpoofColor;    set => Set(ref _askSpoofColor,    value); }
        public string AskIcebergColor  { get => _askIcebergColor;  set => Set(ref _askIcebergColor,  value); }
        public Brush  RowBackground    { get => _rowBackground;    set => Set(ref _rowBackground,    value); }
        
        // ═══ PROPRIEDADES PÚBLICAS DOS NOVOS INDICADORES ═══
        public string BidDetector { get => _bidDetector; set => Set(ref _bidDetector, value); }
        public string AskDetector { get => _askDetector; set => Set(ref _askDetector, value); }
        public string BidDetectorColor { get => _bidDetectorColor; set => Set(ref _bidDetectorColor, value); }
        public string AskDetectorColor { get => _askDetectorColor; set => Set(ref _askDetectorColor, value); }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    public class AlertViewModel : INotifyPropertyChanged
    {
        private int _count;
        private Visibility _countVisibility = Visibility.Collapsed;

        public string Tag            { get; set; } = "";
        public string Title          { get; set; } = "";
        public string Description    { get; set; } = "";
        public string Time           { get; set; } = "";
        public string TagColor       { get; set; } = "#888888";
        public string BgColor        { get; set; } = "#1A1A1A";
        public string BorderColor    { get; set; } = "#2A2A2A";
        public string TitleColor     { get; set; } = "#E8E8E8";

        public int Count
        {
            get => _count;
            set { _count = value; OnPropChanged(); }
        }
        public Visibility CountVisibility
        {
            get => _countVisibility;
            set { _countVisibility = value; OnPropChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class SpoofNotificationViewModel
    {
        public string Time       { get; set; } = "";   // HH:mm:ss
        public string SideLetter { get; set; } = "";   // C ou V
        public string SideColor  { get; set; } = "";   // verde/vermelho
        public string Broker     { get; set; } = "";   // nome da corretora (max 6 chars)
        public string Vol        { get; set; } = "";   // volume
        public string Price      { get; set; } = "";   // preço formatado
        public string TypeLabel  { get; set; } = "";   // CLASS ou CICL
        public string TypeColor  { get; set; } = "";   // cor do tipo
        public string RowBg      { get; set; } = "Transparent";
    }

    public class BrokerFilter
    {
        public string Broker      { get; set; } = "";
        public int    VolMin      { get; set; }
        public int    VolMax      { get; set; }
        public string DisplayText { get; set; } = "";
    }
}


