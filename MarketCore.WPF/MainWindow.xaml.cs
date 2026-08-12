using System;
using System.IO;
using System.Collections.Generic;
using System.Globalization;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MarketCore.Engine;
using MarketCore.Engine.Detectors;
using MarketCore.Models;
using MarketCore.Providers.Simulator;
using MarketCore.Providers.Nelogica;
using MarketCore.Contracts;
using MarketCore.FlowSense;
using MarketCore.HistoricalImporter;
using MarketCore.WPF.LeituraFluxo;
using MarketCore.WPF.Data;
using MarketCore.WPF.AnaliseQuantitativa;
using MarketCore.WPF.Views.PregaoVivaVoz;

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
        /// <summary>Instância ativa da janela principal (para vincular Análise Quantitativa).</summary>
        public static MainWindow? ActiveInstance { get; private set; }
        // ── Engine ────────────────────────────────────────────────────────────
        private MarketEngine _engine = null!;
        private SimulatorProvider _simulator = null!;

        // ── Estado ────────────────────────────────────────────────────────────
        private long _tradeCount;   // apenas Interlocked
        private long _bookCount;
        private long _delta;      // apenas Interlocked
        private int _spoofCount;
        private int _icebergCount;
        private int _renewableCount;
        private int _exhaustionCount;
        private decimal _lastBid;
        private decimal _lastAsk;

        // ── Configurações ─────────────────────────────────────────────────────
        private int _levels = 30;
        private ProfitDLLProvider? _profitBookProvider;
        private int _groupingPts = 0;          // 0 = sem agrupamento
        private int _highlightThreshold = 300;
        private readonly TapeObservableCollection _tapeRecords = new();
        private decimal _tapeVolMin = 0;
        private decimal _tapeMoveMin = 0;
        private decimal _lastTradePrice = 0;   // Para calcular movimento de preço
        private bool _addingBrokerFilter;
        private bool _bookFilterButtonsWired;
        private bool _mainWindowInitialized;
        private string _bookOfferTicker = "";

        private static readonly Dictionary<string, string[]> BrokerFilterTerms = new(StringComparer.OrdinalIgnoreCase)
        {
            ["XP"] = ["XP", "XPI", "CLEAR"],
            ["BTG"] = ["BTG", "PACTUAL"],
            ["CSHG"] = ["CSHG", "CREDIT", "SUISSE"],
            ["MORGAN"] = ["MORGAN", "STANLEY"],
            ["GOLDMAN"] = ["GOLDMAN", "SACHS"],
            ["BRADESCO"] = ["BRADESCO"],
            ["ITAÚ"] = ["ITAU", "ITA"],
            ["ITAU"] = ["ITAU", "ITA"],
            ["RLP"] = ["RLP"],
        };

        // ── Detectores ativos por nível de preço ──────────────────────────────
        // Key: preço formatado, Value: bitfield (bit0=Spoof, bit1=Iceberg, bit2=Renewable)
        private readonly Dictionary<string, int> _detectorsByPrice = new();

        // ── Máximo volume no book (para calcular barras proporcionais) ─────────
        private double _maxBidVol = 1;
        private double _maxAskVol = 1;

        // ── Throttle da barra de pressão ──────────────────────────────────────
        private long _buyAggression;   // apenas Interlocked
        private long _sellAggression;  // apenas Interlocked

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

                // ── Timers UI ──────────────────────────────────────────────────────────
        /// <summary>Tape, Renko e redesenho do livro — lotes pequenos com intervalo curto (evita “picos” grosseiros).</summary>
        private readonly DispatcherTimer _uiTimer;
        /// <summary>Delta, pressão e barras — ~30 Hz para sensação contínua sem refazer livro/tape cada vez.</summary>
        private readonly DispatcherTimer _uiPulseTimer;
        private readonly DispatcherTimer _clockTimer;

        /// <summary>Últimos textos escritos nos TextBlocks mais quentes — evita atribuir igual e forçar layout.</summary>
        private string _lastDeltaText = "\0";
        private string _lastTradeCountText = "\0";
        private string _lastBookCountText = "\0";
        private long _tradesLastSec;
        private long _booksLastSec;
        private long _tradesThisSec;
        private long _booksThisSec;

        /// <summary>Snapshot por negócio p/ HUD de atraso (thread fanout grava; pulso UI lê).</summary>
        private readonly object _tradeLagSync = new();
        private string _tradeNegBolsaClock = "";
        private DateTime? _tradeExchangeUtcSnap;
        private DateTime? _tradeReceivedUtcSnap;

        // ── Tape scroll manual ────────────────────────────────────────────────
        private bool _userScrolledTape;

        // ── Tape: fila para UI sem bloquear fanout (~30 Hz + ~14 Hz); contador próprio para evitar ConcurrentQueue.Count (caro).
        private readonly System.Collections.Concurrent.ConcurrentQueue<TapeRecord> _pendingTape = new();
        /// <summary>Teto pendente até descartar entradas mais antigas — WIN em pico excede vários mil/s na UI.</summary>
        private const int PendingTapeMaxQueue = 24_000;
        private int _tapePendingDepth;

        // Trades pendentes para FlowCandle (Renko) - também drenados em lote.
        private readonly record struct FlowCandleTick(double Price, int Volume, bool IsBuy);
        private readonly System.Collections.Concurrent.ConcurrentQueue<FlowCandleTick> _pendingFlowCandle = new();
        private const int PendingFlowCandleMaxQueue = 8_000;
        private const int PendingFlowCandleFlushPerTick = 42;

        // Brushes pré-criados (frozen) para evitar 5 alocações por trade.
        private static readonly SolidColorBrush TapePriceBrush   = CreateFrozenBrush(255, 238, 238, 238);
        private static readonly SolidColorBrush TapeVolBigBrush  = CreateFrozenBrush(255, 0,   200, 83);
        private static readonly SolidColorBrush TapeVolSmallBrush= CreateFrozenBrush(255, 204, 204, 204);
        private static readonly SolidColorBrush TapeBuyBrush     = CreateFrozenBrush(255, 0,   200, 83);
        private static readonly SolidColorBrush TapeSellBrush    = CreateFrozenBrush(255, 255, 23,  68);
        private static readonly SolidColorBrush TapeNeutralBrush = CreateFrozenBrush(255, 170, 170, 170);
        private static readonly SolidColorBrush TapeRowBgBrush   = CreateFrozenBrush(255, 10,  10,  10);

        // ── Último snapshot para re-render ao mudar filtros ───────────────────
        private BookSnapshot? _lastSnapshot;
        private bool _bookSnapshotLifecycleLogged;
        private bool _bookVisualDirty;

        /// <summary>
        /// Postagem do livro: snapshots vêm na thread <c>MarketEngine-UiDispatch</c>. Acoplar ao Dispatcher aqui +
        /// fundir apenas o último snapshot evita inconsistência + fila gigante no WPF quando o DLL dispara forte.
        /// </summary>
        private readonly object _bookMailboxSync = new();
        private BookSnapshot? _bookMailboxSnapshot;
        private int _bookMailboxDrainPosted;
        private long _lastBookRenderMs;
        /// <summary>Motor já agrega por preço; aqui só limita FPS visual sem duplicar merge.</summary>
        private const long MinBookRenderIntervalMs = 48;
        /// <summary>Suaviza a escala máxima das barras do livro; evita “piscada” quando o max oscila frame a frame.</summary>
        private double _bookBarMaxVolSmoothed;

        private static readonly SolidColorBrush BookBidHighlightBg = CreateFrozenBrush(40, 0, 200, 83);
        private static readonly SolidColorBrush TbDeltaPositiveBrush = CreateFrozenBrush(255, 0,   200, 83);
        private static readonly SolidColorBrush TbDeltaNegativeBrush = CreateFrozenBrush(255, 255, 23,  68);

        // Texto das barras “janela móvel” — evita new SolidColorBrush a cada tick da UI
        private static readonly SolidColorBrush TbWinBuyPctStrongFg  = CreateFrozenBrush(255, 0,   230, 118);
        private static readonly SolidColorBrush TbWinBuyPctNeutralFg = CreateFrozenBrush(255, 0,   180, 80);
        private static readonly SolidColorBrush TbWinSellPctStrongFg  = CreateFrozenBrush(255, 255, 80,  80);
        private static readonly SolidColorBrush TbWinSellPctNeutralFg = CreateFrozenBrush(255, 200, 50,  50);

        private readonly List<double> _analyzerBidPrices = new(48);
        private readonly List<double> _analyzerBidQtys   = new(48);
        private readonly List<double> _analyzerAskPrices = new(48);
        private readonly List<double> _analyzerAskQtys   = new(48);

        /// <summary>FlowSense só usa topo do livro - não clonar milhares de níveis por snapshot.</summary>
        private const int BookAnalyzerLevels = 48;

        /// <remarks>Reuse para remover linhas do grid sem LINQ-ToList por frame.</remarks>
        private readonly List<long> _scratchKeysForBook = new(64);

        /// <summary>Buffer FIFO do enqueue de negócios antes do merge na tape (drenagem total por tick da UI).</summary>
        private readonly List<TapeRecord> _tapeIncomingBatch = new(4096);
        /// <summary>Lista final passada a <see cref="TapeObservableCollection.ResetContents"/> (evita N× <c>Insert(0)</c> em backlog).</summary>
        private readonly List<TapeRecord> _tapeMergeScratch = new(520);

        private int _uiTicks;
        private long _tapeLastScrollTicks;

        /// <summary>Evita <c>ObservableCollection.IndexOf</c> O(n²) ao reordenar o livro.</summary>
        private readonly Dictionary<BookSideRowViewModel, int> _bookSideVmToIndexScratch = new();

        private static readonly SolidColorBrush BookAskHighlightBg = CreateFrozenBrush(40, 255, 23, 68);

        private static SolidColorBrush CreateFrozenBrush(byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }

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

        // ── Agent Panel ───────────────────────────────────────────────────────
        // ── Leitura de Fluxo (substitui o antigo Agent Panel) ──────────────────
        private LeituraFluxoWindow? _leituraFluxoWindow;
        private readonly FlowReadingEngine _flowReadingEngine = new();
        private bool _todayBackfillStarted;

        // ── Análise Quantitativa ──────────────────────────────────────────────
        private AnaliseQuantitativaWindow? _analiseQuantWindow;

        // ── Pregão Viva Voz ───────────────────────────────────────────────────
        private PregaoVivaVozWindow? _pregaoVivaVozWindow;
        private readonly AnaliseQuantSinkAdapter _analiseSink = new();

        /// <summary>Motor de mercado (para Análise Quantitativa e integrações).</summary>
        public MarketEngine MarketEngine => _engine;

        // ── MarketDataManager (PostgreSQL) ────────────────────────────────────
        private MarketDataManager? _marketDataManager;

        // ── ViewModels ────────────────────────────────────────────────────────
        public ObservableCollection<BookSideRowViewModel> BidRows { get; } = new();
        public ObservableCollection<BookSideRowViewModel> AskRows { get; } = new();
        /// <summary>Chave = <see cref="BookLevel.OfferId"/> (ou chave sintética quando ausente) — uma linha por oferta individual, não por preço.</summary>
        private readonly Dictionary<long, BookSideRowViewModel> _bidRowsByKey = new();
        private readonly Dictionary<long, BookSideRowViewModel> _askRowsByKey = new();
        public ObservableCollection<AlertViewModel> AlertItems { get; } = new();
        public ObservableCollection<BrokerFilter> ActiveFilters { get; } = new();
        private readonly ObservableCollection<SpoofNotificationViewModel> _spoofNotifications = new();

        // ── Credenciais Profit ────────────────────────────────────────────────
        private readonly ProfitCredentials _profitCredentials;
        private readonly bool _isRealMarket;
        private volatile bool _profitDllConnected;
        private bool _openHistoryDownloadAfterConnect;
        private bool _leituraFluxoStarted;
        private bool _bookSubscriptionScheduled;
        private DispatcherTimer? _runtimeHeartbeat;

        // ─────────────────────────────────────────────────────────────────────
        public MainWindow(ProfitCredentials credentials, bool isRealMarket)
        {
            _profitCredentials = credentials;
            _isRealMarket      = isRealMarket;

            InitializeComponent();
            DataContext = this;
            ActiveInstance = this;

            _bookOfferTicker = ResolveBookOfferTicker(FlowsenseUiSettings.Load().HistoryDownloadTicker);
            TxPrimaryTicker.Text = _bookOfferTicker;

            TxPrimaryTicker.LostFocus += (_, _) =>
            {
                try { CommitPrimaryTickerIfChanged(); }
                catch (Exception ex) { App.AppendCrashLog(nameof(CommitPrimaryTickerIfChanged), ex); }
            };
            TxPrimaryTicker.KeyDown += TxPrimaryTicker_KeyDown;

            IcActiveFilters.ItemsSource = ActiveFilters;

            // Timers
            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
            _uiTimer.Tick += UiTimer_Tick;

            _uiPulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _uiPulseTimer.Tick += UiPulseTimer_Tick;

            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += ClockTimer_Tick;

            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;

            // Filtros do book: handlers no XAML (evitar duplicar com += aqui).
            CbWindowPeriod.SelectionChanged  += CbWindowPeriod_SelectionChanged;
            CbWindowPeriod2.SelectionChanged += CbWindowPeriod2_SelectionChanged;
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
                    Dispatcher.InvokeAsync(() => RenderBook(_lastSnapshot), DispatcherPriority.Background);
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
        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_mainWindowInitialized)
                return;

            _mainWindowInitialized = true;

            try
            {
                WindowState = WindowState.Normal;
                InitializeMarketRuntime();
                StartRuntimeHeartbeat();
                
                // ── Inicializar MarketDataManager (PostgreSQL) ──
                await InitializeMarketDataManagerAsync();
            }
            catch (Exception ex)
            {
                App.AppendCrashLog(nameof(MainWindow_Loaded), ex);
                MessageBox.Show(
                    this,
                    $"Não foi possível iniciar o mercado.\n\n{ex.Message}",
                    "MarketCore",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            App.AppendLifecycle($"MainWindow.Closing cancel={e.Cancel}");
            if (e.Cancel)
                return;

            // Safety net: se engine.Dispose() ou outro recurso travar, o processo sai em 5s.
            // Thread background — não impede saída normal, só dispara se o processo ficar preso.
            var guardThread = new System.Threading.Thread(() =>
            {
                System.Threading.Thread.Sleep(5000);
                App.AppendLifecycle("ForceExitTimeout — processo travado no Closing, forçando saída");
                Environment.Exit(0);
            });
            guardThread.IsBackground = true;
            guardThread.Start();

            _runtimeHeartbeat?.Stop();
            _uiTimer?.Stop();
            _uiPulseTimer?.Stop();
            _clockTimer?.Stop();
            _flowScoreTimer?.Stop();
            _brokerAccum?.Stop();
            _leituraFluxoWindow?.ForceClose();
            _analiseQuantWindow?.Close();
            _pregaoVivaVozWindow?.Close();   // garante Dispose das threads de áudio antes do Dispatcher fechar
            _engine?.SetAnaliseQuantSink(null);

            if (ActiveInstance == this)
                ActiveInstance = null;
            
            // Fechar conexão PostgreSQL
            _marketDataManager?.Dispose();

            var engine = _engine;
            _engine = null!;
            if (engine == null)
                return;

            try
            {
                engine.Dispose();
            }
            catch (Exception ex)
            {
                App.AppendCrashLog(nameof(MainWindow) + ".Closing.Dispose", ex);
            }
        }

        private async System.Threading.Tasks.Task InitializeMarketDataManagerAsync()
        {
            try
            {
                _marketDataManager = new MarketDataManager(
                    host: "localhost",
                    port: 5432,
                    database: "marketcore_historical",
                    username: "postgres",
                    password: "postgres"  // ← TROCAR pela senha real do PostgreSQL
                );

                bool conectado = await _marketDataManager.ConnectAsync();
                if (conectado)
                {
                    await _marketDataManager.TestConnectionAsync();
                    int count = await _marketDataManager.GetTodayTradeCountAsync();
                    Console.WriteLine($"✓ MarketDataManager: {count} trades em trades_intraday");
                    App.AppendLifecycle($"MarketDataManager conectado - {count} trades intraday");
                }
                else
                {
                    App.AppendLifecycle("MarketDataManager: Falha ao conectar");
                }
            }
            catch (Exception ex)
            {
                App.AppendCrashLog("InitializeMarketDataManager", ex);
                Console.WriteLine($"✗ MarketDataManager erro: {ex.Message}");
            }
        }

        private void InitializeMarketRuntime()
        {
            // ── Vincular ItemsSource da Tape ──
            IcTape.ItemsSource = _tapeRecords;
            IcSpoofNotifications.ItemsSource = _spoofNotifications;
            BtnPower.Click += BtnPower_Click;
            BtnPregaoVivaVoz.Click += BtnPregaoVivaVoz_Click;
            BtnRecordingConfig.Click += BtnRecordingConfig_Click;
            BtnAgentPanel.Click += BtnAgentPanel_Click;
            BtnAnaliseQuant.Click += BtnAnaliseQuant_Click;
            if (_isRealMarket)
            {
                BtnHistory.Visibility = Visibility.Visible;
                BtnHistory.Click += BtnHistory_Click;
            }

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

            _profitBookProvider = provider as ProfitDLLProvider;
            _engine = new MarketEngine(provider);

            var uiDetect = FlowsenseUiSettings.Load();
            bool microOn = !uiDetect.DisableBookMicrostructureDetectors;
            MarketEngine.EnableBookMicrostructureDetectors = microOn;
            ChkBookMicrostructureDetectors.IsChecked = microOn;
            ChkBookMicrostructureDetectors.Click += ChkBookMicrostructureDetectors_Click;

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
            _bookAnalyzer      = new BookAnalyzer(_flowScoreConfig);
            _detectorAggregator = new DetectorAggregator(_flowScoreConfig);
            _flowScoreEngine   = new FlowScoreEngine(_brokerAccum, _deltaEngine, _bookAnalyzer, _detectorAggregator, _flowScoreConfig);

            // Timer para recalcular FlowScore a cada 200 ms
            _flowScoreTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _flowScoreTimer.Tick += FlowScoreTimer_Tick;
            _flowScoreTimer.Start();

            // Inicializar painel FlowScore com as engines
            FlowScorePanelControl.Initialize(_flowScoreEngine, _brokerAccum, _deltaEngine, _bookAnalyzer, _detectorAggregator);
            FlowScorePanelControl.LeituraFluxoRequested += (_, _) => ToggleLeituraFluxoWindow();

            AnaliseQuantLiveHub.SetHost(this);

            // ── Ativar gravação automática ──────────────────────────────────
            try
            {
                var recordingConfig = RecordingConfig.Load();
                var recordingsPath  = recordingConfig.RecordingsPath;

                // Se o drive não existir (HD externo desconectado), usa padrão
                var root = System.IO.Path.GetPathRoot(recordingsPath);
                if (!string.IsNullOrEmpty(root) && !Directory.Exists(root))
                {
                    recordingsPath = RecordingConfig.GetDefaultPath();
                }

                _engine.HabilitarGravacao(recordingsPath, isSimulator: !_isRealMarket);
            }
            catch (Exception ex)
            {
                App.AppendCrashLog(nameof(MainWindow) + ".HabilitarGravacao", ex);
            }

            WireBookFilterButtons();

            if (CbLevels.SelectedItem is ComboBoxItem levelsItem
                && int.TryParse(levelsItem.Content?.ToString(), out int initialLevels))
            {
                _levels = initialLevels;
            }

            _uiTimer.Start();
            _uiPulseTimer.Start();
            _clockTimer.Start();

            if (_isRealMarket && FlowsenseUiSettings.Load().ShowHistoryDownloadOnStartup)
                _openHistoryDownloadAfterConnect = true;

            var connectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            connectTimer.Tick += (_, _) =>
            {
                connectTimer.Stop();
                _ = ConnectEngineSafelyAsync(providerCredentials);
            };
            connectTimer.Start();

            var agentTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            agentTimer.Tick += (_, _) =>
            {
                agentTimer.Stop();
                StartLeituraFluxoIfNeeded();
            };
            agentTimer.Start();
        }

        private void StartRuntimeHeartbeat()
        {
            _runtimeHeartbeat = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _runtimeHeartbeat.Tick += (_, _) =>
            {
                if (!IsLoaded)
                    return;

                App.AppendLifecycle("MainWindow.Heartbeat");
            };
            _runtimeHeartbeat.Start();
        }

        /// <summary>
        /// Cria a janela de Leitura de Fluxo (sem mostrar) para que já esteja pronta quando o usuário
        /// clicar no botão. O <see cref="FlowReadingEngine"/> em si já está recebendo trades desde o
        /// início (via <see cref="Engine_OnTrade"/>) independente desta janela existir ou não.
        /// </summary>
        private void StartLeituraFluxoIfNeeded()
        {
            if (_leituraFluxoStarted)
                return;

            _leituraFluxoStarted = true;

            try
            {
                _leituraFluxoWindow = new LeituraFluxoWindow(_flowReadingEngine);
            }
            catch (Exception ex)
            {
                _leituraFluxoStarted = false;
                App.AppendCrashLog(nameof(StartLeituraFluxoIfNeeded), ex);
            }
        }

        /// <summary>
        /// Baixa da ProfitDLL (GetHistoryTrades) os negócios de hoje desde a abertura do pregão até agora
        /// e alimenta o <see cref="FlowReadingEngine"/> + grava em <c>trades_intraday</c> (source="historical") —
        /// fecha a lacuna de quando o MarketCore não estava aberto. Roda uma vez por sessão do app, em segundo
        /// plano, disparado assim que o mercado conecta (mesmo gatilho usado para o Download Histórico manual).
        /// </summary>
        private async System.Threading.Tasks.Task StartTodayHistoryBackfillIfNeededAsync()
        {
            if (_todayBackfillStarted || !_isRealMarket)
                return;
            _todayBackfillStarted = true;

            try
            {
                DateTime today = DateTime.Today;
                // Abertura padrão do pregão B3 para WIN. Se ainda não passou desse horário, não há nada para baixar.
                DateTime marketOpen = today.AddHours(9);
                DateTime now = DateTime.Now;
                if (now <= marketOpen)
                    return;

                _flowReadingEngine.SetBackfillStatus("Carregando histórico de hoje…");

                var credsCfg = new ProfitCredentialsConfig
                {
                    ActivationKey = _profitCredentials.ActivationKey ?? "",
                    Username = _profitCredentials.Username ?? "",
                    Password = _profitCredentials.Password ?? ""
                };

                bool sessionOk = await ProfitMarketInit.TryEnsureMarketForHistoryAsync(
                    credsCfg,
                    TimeSpan.FromSeconds(45),
                    sessionAlreadyConnected: _profitDllConnected || ProfitMarketInit.IsDllInitializedInProcess)
                    .ConfigureAwait(false);

                if (!sessionOk)
                {
                    App.AppendLifecycle("Backfill histórico de hoje: sem sessão Profit disponível — abortado.");
                    return;
                }

                // Passa o resolvedor de corretora do mesmo provider ao vivo (código numérico → token curto,
                // ex. "XP"/"BTG") — sem isso o backfill grava tudo sob o código bruto do agente e nenhum
                // negócio bate com as 7 colunas fixas da janela.
                var sink = new FlowReadingHistorySink(_flowReadingEngine, _marketDataManager,
                    agentId => _profitBookProvider?.ResolveBrokerShortName(agentId) ?? agentId.ToString());
                // Ticker contínuo (recomendação Nelogica para GetHistoryTrades) — não o contrato específico
                // usado na assinatura do book ao vivo.
                const string historyTicker = "WINFUT";
                sink.SetCurrentContract(historyTicker);

                using var history = new ProfitHistoryService(sink);
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));

                await history.RequestHistoricalDataAsync(historyTicker, marketOpen, now, cts.Token)
                    .ConfigureAwait(false);

                App.AppendLifecycle(
                    $"Backfill histórico de hoje concluído: {sink.TotalAccepted:N0} negócios carregados " +
                    $"({sink.TotalRejected:N0} rejeitados).");
            }
            catch (Exception ex)
            {
                App.AppendCrashLog(nameof(StartTodayHistoryBackfillIfNeededAsync), ex);
            }
            finally
            {
                _flowReadingEngine.SetBackfillStatus("");
            }
        }

        private void ClockTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                TbClock.Text = DateTime.Now.ToString("HH:mm:ss");
                _tradesLastSec = _tradesThisSec;
                _booksLastSec  = _booksThisSec;
                _tradesThisSec = 0;
                _booksThisSec  = 0;
            }
            catch (Exception ex)
            {
                App.AppendCrashLog(nameof(ClockTimer_Tick), ex);
            }
        }

        private void FlowScoreTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                _flowScoreEngine.CalculateScore();
            }
            catch (Exception ex)
            {
                App.AppendCrashLog(nameof(FlowScoreTimer_Tick), ex);
            }
        }

        private void WireBookFilterButtons()
        {
            if (_bookFilterButtonsWired)
                return;

            _bookFilterButtonsWired = true;
            BtnAddFilter.Click += BtnAddFilter_Click;
            BtnClearFilters.Click += BtnClearFilters_Click;
        }

        private void ChkBookMicrostructureDetectors_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool enabled = ChkBookMicrostructureDetectors.IsChecked == true;
                MarketEngine.EnableBookMicrostructureDetectors = enabled;
                var u = FlowsenseUiSettings.Load();
                u.DisableBookMicrostructureDetectors = !enabled;
                u.Save();
            }
            catch (Exception ex)
            {
                App.AppendCrashLog(nameof(ChkBookMicrostructureDetectors_Click), ex);
            }
        }

        private async Task ConnectEngineSafelyAsync(ProviderCredentials credentials)
        {
            try
            {
                await _engine.ConnectAsync(credentials).ConfigureAwait(true);
                App.AppendLifecycle("MainWindow.MarketConnectCompleted");
            }
            catch (Exception ex)
            {
                App.AppendCrashLog(nameof(ConnectEngineSafelyAsync), ex);
                MessageBox.Show(
                    this,
                    $"Falha ao conectar ao mercado.\n\n{ex.Message}",
                    "MarketCore",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private static string ResolveBookOfferTicker(string? preferredTicker)
        {
            string ticker = (preferredTicker ?? string.Empty).Trim().ToUpperInvariant();
            // Fallback vazio: contrato corrente do WIN. M=jun, Q=ago (código padrão B3/CME) — WINQ26 vence 19/08/2026.
            // Depois disso, atualizar para o próximo vencimento (ver Engine/ContractManager.VencimentosWIN) ou ligar a rolagem automática.
            return ticker.Length > 0 ? ticker : "WINQ26";
        }

        private void TxPrimaryTicker_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;
            try
            {
                CommitPrimaryTickerIfChanged();
                TxPrimaryTicker.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            }
            catch (Exception ex)
            {
                App.AppendCrashLog(nameof(TxPrimaryTicker_KeyDown), ex);
            }
            e.Handled = true;
        }

        /// <summary>Muda o ativo monitorizado: livro, tape, Renko, FlowScore e gravação seguem este ticker.</summary>
        private void CommitPrimaryTickerIfChanged()
        {
            string normalized = ResolveBookOfferTicker(TxPrimaryTicker.Text);
            TxPrimaryTicker.Text = normalized;
            if (string.Equals(normalized, _bookOfferTicker, StringComparison.OrdinalIgnoreCase))
                return;

            string previous = _bookOfferTicker;
            ResetWorkspaceForInstrumentSwitch(normalized);
            _bookOfferTicker = normalized;

            var ui = FlowsenseUiSettings.Load();
            ui.HistoryDownloadTicker = normalized;
            ui.Save();

            var eng = _engine;
            bool connected = eng != null && eng.Status == ConnectionStatus.Connected;
            if (connected)
            {
                if (!string.IsNullOrEmpty(previous))
                {
                    try { eng.Unsubscribe(previous); }
                    catch (Exception ex) { App.AppendCrashLog(nameof(CommitPrimaryTickerIfChanged) + ".Unsubscribe", ex); }
                }
                try { eng.Subscribe(normalized); }
                catch (Exception ex) { App.AppendCrashLog(nameof(CommitPrimaryTickerIfChanged) + ".Subscribe", ex); }
            }
        }

        private void ResetWorkspaceForInstrumentSwitch(string newTicker)
        {
            while (_pendingTape.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _tapePendingDepth, 0);
            while (_pendingFlowCandle.TryDequeue(out _)) { }

            _tapeRecords.Clear();
            _bookBarMaxVolSmoothed = 0;
            Interlocked.Exchange(ref _tradeCount, 0);
            Interlocked.Exchange(ref _bookCount, 0);
            Interlocked.Exchange(ref _delta, 0);
            Interlocked.Exchange(ref _buyAggression, 0);
            Interlocked.Exchange(ref _sellAggression, 0);
            _lastDeltaText = "\0";
            _lastTradeCountText = "\0";
            _lastBookCountText = "\0";
            _lastTradePrice = 0;
            _spoofCount = 0;
            _icebergCount = 0;
            _renewableCount = 0;
            _exhaustionCount = 0;
            _spoofNotifications.Clear();

            lock (_aggressionWindow)
            {
                _aggressionWindow.Clear();
                _windowBuy = 0;
                _windowSell = 0;
            }
            lock (_aggressionWindow2)
            {
                _aggressionWindow2.Clear();
                _windowBuy2 = 0;
                _windowSell2 = 0;
            }

            _lastBid = 0;
            _lastAsk = 0;
            TbLastPrice.Text = "--";
            TbLastPrice.Foreground = new SolidColorBrush(Color.FromRgb(0, 200, 83));
            TbSpread.Text = "-- pts";
            TbDepth.Text = "-- níveis";
            TbFooterBid.Text = "--";
            TbFooterAsk.Text = "--";
            lock (_bookMailboxSync)
                _bookMailboxSnapshot = null;
            lock (_tradeLagSync)
            {
                _tradeNegBolsaClock = "";
                _tradeExchangeUtcSnap = null;
                _tradeReceivedUtcSnap = null;
            }

            TbTapeTotal.Text = "0 trades";

            BidRows.Clear();
            AskRows.Clear();
            _bidRowsByKey.Clear();
            _askRowsByKey.Clear();
            _lastSnapshot = new BookSnapshot(
                newTicker,
                Array.Empty<BookLevel>(),
                Array.Empty<BookLevel>(),
                DateTime.UtcNow,
                Array.Empty<BookLevel>(),
                Array.Empty<BookLevel>());
            _bookVisualDirty = true;
            RenderBook(_lastSnapshot);

            _deltaEngine?.ClearSessionState();
            _brokerAccum?.ClearAllBrokers();
            _bookAnalyzer?.ClearBookState();
            _detectorAggregator?.ResetForNewInstrument();

            flowCandleChart.ApplyPrimaryInstrument(newTicker);
        }

        private void EnsureBookSubscription()
        {
            string ticker = ResolveBookOfferTicker(TxPrimaryTicker.Text);
            _bookOfferTicker = ticker;
            TxPrimaryTicker.Text = ticker;
            _engine?.Subscribe(_bookOfferTicker);
        }

        private void ScheduleBookSubscription()
        {
            if (_bookSubscriptionScheduled)
                return;

            _bookSubscriptionScheduled = true;
            App.AppendLifecycle("MainWindow.BookSubscribeScheduled");

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    App.AppendLifecycle("MainWindow.BookSubscribeStart");
                    EnsureBookSubscription();
                    App.AppendLifecycle("MainWindow.BookSubscribeDone");
                }
                catch (Exception ex)
                {
                    App.AppendCrashLog(nameof(ScheduleBookSubscription), ex);
                }
            };
            timer.Start();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  CALLBACKS DA ENGINE
        // ══════════════════════════════════════════════════════════════════════

        private void Engine_OnConnectionChanged(ConnectionChangedEvent evt)
        {
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    bool connected = evt.Status == ConnectionStatus.Connected;
                    _profitDllConnected = connected && _isRealMarket;
                    EllipseConnection.Fill = connected
                        ? new SolidColorBrush(Color.FromRgb(0, 200, 83))
                        : new SolidColorBrush(Color.FromRgb(255, 23, 68));
                    TbConnectionStatus.Text = connected ? "CONECTADO" : evt.Status.ToString().ToUpper();

                    if (connected)
                    {
                        App.AppendLifecycle("MainWindow.MarketConnected");
                        ScheduleBookSubscription();
                    }

                    if (connected && _isRealMarket && _openHistoryDownloadAfterConnect)
                    {
                        _openHistoryDownloadAfterConnect = false;
                        var openHistoryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                        openHistoryTimer.Tick += (_, _) =>
                        {
                            openHistoryTimer.Stop();
                            OpenDownloadHistoryWindow();
                        };
                        openHistoryTimer.Start();
                    }

                    if (connected && _isRealMarket)
                        _ = StartTodayHistoryBackfillIfNeededAsync();
                }
                catch (Exception ex)
                {
                    App.AppendCrashLog(nameof(Engine_OnConnectionChanged), ex);
                }
            }, DispatcherPriority.Background);
        }

        private void Engine_OnTrade(TradeEvent trade)
        {
            Interlocked.Increment(ref _tradeCount);
            _tradesThisSec++;

            lock (_tradeLagSync)
            {
                _tradeNegBolsaClock = trade.Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
                _tradeExchangeUtcSnap = trade.ExchangeTimeUtc;
                _tradeReceivedUtcSnap = trade.ReceivedUtc;
            }

            // PostgreSQL: nunca bloquear a thread dos callbacks (antes: GetResult() síncrono por trade).
            _marketDataManager?.EnqueueRealtimeTrade(
                timestamp: trade.Time,
                symbol: "WIN",
                price: (int)trade.Price,
                quantity: (int)trade.Volume,
                side: trade.Aggressor == TradeAggressor.Buy ? "buy" : "sell",
                aggressor: trade.Aggressor == TradeAggressor.Buy ? 1 : -1,
                brokerCode: 0,
                brokerName: trade.Broker,
                source: "realtime"
            );

            // Leitura de Fluxo: motor em memória (não grava em disco — só análise ao vivo).
            _flowReadingEngine.OnTrade(
                trade.Broker,
                trade.Time,
                trade.Price,
                (int)trade.Volume,
                trade.Aggressor == TradeAggressor.Buy);

            // FlowCandle Renko: enfileirar para drain em lote no _uiTimer
            // (antes era um InvokeAsync por trade, que enchia o dispatcher).
            bool isBuyFlow = trade.Aggressor == TradeAggressor.Buy;
            if (trade.Aggressor == TradeAggressor.Buy || trade.Aggressor == TradeAggressor.Sell)
            {
                _pendingFlowCandle.Enqueue(new FlowCandleTick((double)trade.Price, (int)trade.Volume, isBuyFlow));
                while (_pendingFlowCandle.Count > PendingFlowCandleMaxQueue)
                    _pendingFlowCandle.TryDequeue(out _);
            }
            // ──── Acumuladores de Delta e Pressão ────
            // Somente trades com agressor explícito (B3 via DLL tipo 2/3). Cross/leilão etc. não contaminam barras nem delta tradicional.
            if (trade.Aggressor == TradeAggressor.Buy)
            {
                Interlocked.Add(ref _buyAggression, trade.Volume);
                Interlocked.Add(ref _delta, trade.Volume);
            }
            else if (trade.Aggressor == TradeAggressor.Sell)
            {
                Interlocked.Add(ref _sellAggression, trade.Volume);
                Interlocked.Add(ref _delta, -trade.Volume);
            }

            // ──── Janelas Móveis ────
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

            // ──── FlowSense - alimenta BrokerAccumulator e DeltaEngine ────
            if (trade.Aggressor == TradeAggressor.Buy || trade.Aggressor == TradeAggressor.Sell)
            {
                _brokerAccum.OnTrade(
                    trade.Broker,
                    (double)trade.Volume,
                    trade.Aggressor == TradeAggressor.Buy,
                    trade.Time);
            }

            _deltaEngine.OnTrade(
                (double)trade.Price,
                trade.Aggressor == TradeAggressor.Buy  ? (double)trade.Volume : 0,
                trade.Aggressor == TradeAggressor.Sell ? (double)trade.Volume : 0,
                trade.Time);

            // Análise Quantitativa — CoordPlayerMiner precisa de TODOS os trades (antes dos filtros de tape).
            AnaliseQuantLiveHub.PushTrade(trade);

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

            // ──── ENFILEIRAR PARA A TAPE (sem tocar no Dispatcher aqui) ────
            // Tape: coleção atualizada pelo pulso leve (~30 Hz) em lotes, sem Reload em massa na UI.
            bool isBuy = trade.Aggressor == TradeAggressor.Buy;
            bool isSell = trade.Aggressor == TradeAggressor.Sell;
            bool bigVol = trade.Volume >= 500;

            var rec = new TapeRecord
            {
                Time = trade.Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                Broker = trade.Broker.Length > 6 ? trade.Broker[..6] : trade.Broker,
                Price = trade.Price.ToString("N0"),
                Volume = trade.Volume.ToString(),
                Side = isBuy ? "Compra" : isSell ? "Venda" : "Outro",
                PriceColor = TapePriceBrush,
                VolColor = bigVol ? TapeVolBigBrush : TapeVolSmallBrush,
                SideColor = isBuy ? TapeBuyBrush : isSell ? TapeSellBrush : TapeNeutralBrush,
                RowBg = TapeRowBgBrush,
                VolWeight = bigVol ? "Bold" : "Normal"
            };

            _pendingTape.Enqueue(rec);
            Interlocked.Increment(ref _tapePendingDepth);

            // Cap: descarta negócios pendentes mais antigos (evita usar ConcurrentQueue.Count).
            while (Volatile.Read(ref _tapePendingDepth) > PendingTapeMaxQueue
                   && _pendingTape.TryDequeue(out _))
                Interlocked.Decrement(ref _tapePendingDepth);
        }

        /// <summary>Drena FlowCandle (Renko) em lote no timer pesado.</summary>
        private void FlushPendingFlowCandle()
        {
            if (_pendingFlowCandle.IsEmpty)
                return;

            int processed = 0;
            while (processed < PendingFlowCandleFlushPerTick && _pendingFlowCandle.TryDequeue(out var t))
            {
                try
                {
                    flowCandleChart.ProcessTrade(t.Price, t.Volume, t.IsBuy);
                }
                catch (Exception ex)
                {
                    App.AppendCrashLog(nameof(FlushPendingFlowCandle), ex);
                }
                processed++;
            }
        }

        /// <summary>
        /// Drena <b>toda</b> a fila pendente da tape a cada tick. Antes: fatia ~900 na <b>cabeça</b> da <c>ConcurrentQueue</c>
        /// deixava os negócios mais novos na <b>cauda</b> — atraso real de vários segundos na exibição mesmo com feed em dia.
        /// Se o backlog exceder o teto da tape (500 linhas), mantém só os últimos trades (mais frescos) na janela visual.
        /// </summary>
        private void FlushPendingTape()
        {
            if (_pendingTape.IsEmpty)
            {
                if (Volatile.Read(ref _tapePendingDepth) > 0)
                    Interlocked.Exchange(ref _tapePendingDepth, 0);
                return;
            }

            _tapeIncomingBatch.Clear();
            while (_pendingTape.TryDequeue(out var rec))
            {
                _tapeIncomingBatch.Add(rec);
                Interlocked.Decrement(ref _tapePendingDepth);
            }

            if (_tapeIncomingBatch.Count == 0)
                return;

            const int tapeCap = 500;
            if (_tapeIncomingBatch.Count > tapeCap)
                _tapeIncomingBatch.RemoveRange(0, _tapeIncomingBatch.Count - tapeCap);

            _tapeMergeScratch.Clear();
            for (int i = _tapeIncomingBatch.Count - 1; i >= 0; i--)
                _tapeMergeScratch.Add(_tapeIncomingBatch[i]);
            for (int i = 0; i < _tapeRecords.Count && _tapeMergeScratch.Count < tapeCap; i++)
                _tapeMergeScratch.Add(_tapeRecords[i]);

            _tapeRecords.ResetContents(_tapeMergeScratch);

            TbTapeTotal.Text = $"{Interlocked.Read(ref _tradeCount)} trades";

            TapeScrollIfNeeded();
        }

        /// <summary>
        /// Tape: autoscroll no topo onde estão os negócios mais recentes (merge via <see cref="TapeObservableCollection.ResetContents"/>).
        /// Autoscroll deve ir para <b>Topo</b> onde estão os negócios mais recentes —
        /// <c>ScrollToBottom</c> mostrava a cauda (= mais antigos) e parecia atraso de minutos vs. alertas/relogio.
        /// </summary>
        private void TapeScrollIfNeeded()
        {
            if (ChkAutoscroll.IsChecked != true || _userScrolledTape)
                return;

            long now = Environment.TickCount64;
            if (now - _tapeLastScrollTicks < 280)
                return;

            _tapeLastScrollTicks = now;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try { TapeScrollViewer.ScrollToTop(); }
                catch { /* ignore */ }
            }), DispatcherPriority.Background);
        }

        private void FeedBookAnalyzerFromTop(IReadOnlyList<BookLevel> bids, IReadOnlyList<BookLevel> asks)
        {
            _analyzerBidPrices.Clear();
            _analyzerBidQtys.Clear();
            _analyzerAskPrices.Clear();
            _analyzerAskQtys.Clear();

            int nB = Math.Min(BookAnalyzerLevels, bids.Count);
            int nA = Math.Min(BookAnalyzerLevels, asks.Count);

            for (int i = 0; i < nB; i++)
            {
                var b = bids[i];
                _analyzerBidPrices.Add((double)b.Price);
                _analyzerBidQtys.Add((double)b.Volume);
            }

            for (int i = 0; i < nA; i++)
            {
                var a = asks[i];
                _analyzerAskPrices.Add((double)a.Price);
                _analyzerAskQtys.Add((double)a.Volume);
            }

            _bookAnalyzer.OnBookSnapshot(_analyzerBidPrices, _analyzerBidQtys, _analyzerAskPrices, _analyzerAskQtys);
        }

        private void Engine_OnBookSnapshot(BookSnapshot snapshot)
        {
            Interlocked.Increment(ref _bookCount);
            _booksThisSec++;

            lock (_bookMailboxSync)
                _bookMailboxSnapshot = snapshot;

            // Uma só postagem pendent até o Dispatcher drenar; fotos intermediários são descartadas (último vence).
            if (Interlocked.CompareExchange(ref _bookMailboxDrainPosted, 1, 0) == 0)
            {
                Dispatcher.BeginInvoke(DrainBookMailboxOnDispatcher,
                    DispatcherPriority.Normal);
            }
        }

        /// <summary>Atualiza <c>_lastSnapshot</c> + FlowSense a partir da mailbox na thread UI.</summary>
        private void DrainBookMailboxOnDispatcher()
        {
            // Se _engine já foi nulificado no Closing, ignora callbacks pendentes no Dispatcher.
            if (_engine == null) return;

            try
            {
                for (;;)
                {
                    BookSnapshot? snap = null;
                    lock (_bookMailboxSync)
                    {
                        snap = _bookMailboxSnapshot;
                        _bookMailboxSnapshot = null;
                    }

                    if (snap == null)
                        break;

                    ApplyBookSnapshotOnUiThread(snap);
                }

                TryRenderBookThrottled();
            }
            finally
            {
                Interlocked.Exchange(ref _bookMailboxDrainPosted, 0);
                bool pending;
                lock (_bookMailboxSync)
                    pending = _bookMailboxSnapshot != null;

                if (pending && Interlocked.CompareExchange(ref _bookMailboxDrainPosted, 1, 0) == 0)
                    Dispatcher.BeginInvoke(DrainBookMailboxOnDispatcher, DispatcherPriority.Normal);
            }
        }

        private void ApplyBookSnapshotOnUiThread(BookSnapshot snapshot)
        {
            // O MarketEngine já agrega por preço antes de emitir snapshot.
            _lastSnapshot = snapshot;

            if (!_bookSnapshotLifecycleLogged && (snapshot.Bids.Count > 0 || snapshot.Asks.Count > 0))
            {
                _bookSnapshotLifecycleLogged = true;
                App.AppendLifecycle("MainWindow.FirstBookSnapshot");
            }

            if (snapshot.Bids.Count > 0 || snapshot.Asks.Count > 0)
            {
                // Cópia + peel: snapshot do motor já vem normalizado, mas evita topo cruzado no analisador e
                // não grava _lastBid/_lastAsk aqui (throttle de RenderBook desalinhava cabeçalho vs grid).
                var bidsNorm = snapshot.Bids.Count > 0
                    ? new List<BookLevel>(snapshot.Bids)
                    : new List<BookLevel>();
                var asksNorm = snapshot.Asks.Count > 0
                    ? new List<BookLevel>(snapshot.Asks)
                    : new List<BookLevel>();
                BookSnapshotAggregation.NormalizeEconomicalTop(bidsNorm, asksNorm);
                FeedBookAnalyzerFromTop(bidsNorm, asksNorm);
            }

            _bookVisualDirty = true;
        }

        private void TryRenderBookThrottled()
        {
            if (!_bookVisualDirty || _lastSnapshot == null)
                return;

            long nowMs = Environment.TickCount64;
            if (nowMs - _lastBookRenderMs < MinBookRenderIntervalMs)
                return;

            _bookVisualDirty = false;
            _lastBookRenderMs = nowMs;
            RenderBook(_lastSnapshot);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  RENDERIZAÇÃO DO BOOK BILATERAL
        // ══════════════════════════════════════════════════════════════════════

        private static string NormalizeBrokerKey(string? broker)
        {
            if (string.IsNullOrWhiteSpace(broker))
                return string.Empty;

            return broker.Trim().ToUpperInvariant();
        }

        private static IEnumerable<string> ExpandBrokerFilterTerms(string filterBroker)
        {
            string trimmed = (filterBroker ?? string.Empty).Trim();
            string key = NormalizeBrokerKey(trimmed);
            if (key.Length == 0 || key == "(TODAS)")
                return Array.Empty<string>();

            if (BrokerFilterTerms.TryGetValue(trimmed, out string[]? aliases) && aliases.Length > 0)
                return aliases.Select(NormalizeBrokerKey).Where(t => t.Length > 0);

            return new[] { key };
        }

        private static bool BrokerMatchesFilter(string levelBroker, string filterBroker)
        {
            string filter = NormalizeBrokerKey(filterBroker);
            if (filter.Length == 0 || filter == "(TODAS)")
                return true;

            string level = NormalizeBrokerKey(levelBroker);
            if (level.Length == 0)
                return false;

            foreach (string term in ExpandBrokerFilterTerms(filterBroker))
            {
                if (level.Equals(term, StringComparison.Ordinal)
                    || level.Contains(term, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool PassesVolumeFilter(int volume, int volMin, int volMax) =>
            volume >= volMin && volume <= volMax;

        private string DisplayBookBroker(BookLevel level) =>
            _profitBookProvider?.ResolveDisplayBroker(level) ?? level.Broker;

        // Mantém só ofertas que casam com algum filtro ativo no painel esquerdo.
        // Livro agregado por preço: linhas sem OfferId/corretora - filtro de corretora não aplica; volume aplica-se.
        private bool PassaFiltro(BookLevel level)
        {
            if (ActiveFilters.Count == 0)
                return true;

            string broker = DisplayBookBroker(level);
            bool hasBrokerForFilter = level.OfferId > 0 || !string.IsNullOrWhiteSpace(broker);

            return ActiveFilters.Any(f =>
                (!hasBrokerForFilter || BrokerMatchesFilter(broker, f.Broker))
                && PassesVolumeFilter(level.Volume, f.VolMin, f.VolMax));
        }

        private static List<BookLevel> SelectBookLevels(IReadOnlyList<BookLevel> levels, int maxLevels, Func<BookLevel, bool> predicate)
        {
            var selected = new List<BookLevel>(maxLevels);
            foreach (var level in levels)
            {
                if (!predicate(level))
                    continue;

                selected.Add(level);
                if (selected.Count >= maxLevels)
                    break;
            }

            return selected;
        }

        private string ReadFilterBrokerFromUi()
        {
            string broker = (CbFilterBroker.Text ?? string.Empty).Trim();
            if (broker.Length == 0
                && CbFilterBroker.SelectedItem is ComboBoxItem item)
            {
                broker = item.Content?.ToString()?.Trim() ?? string.Empty;
            }

            return broker.Length == 0 ? "(todas)" : broker;
        }

        private void UpdateFilterStatus(BookSnapshot snapshot, int visibleInPool)
        {
            if (ActiveFilters.Count == 0)
                return;

            int pool = snapshot.Bids.Count + snapshot.Asks.Count;
            int visible = visibleInPool;
            if (pool == 0)
            {
                TbFilterStatus.Text =
                    $"{ActiveFilters.Count} filtro(s) ativo(s) - aguardando livro da DLL (Books/s em 0 = sem ofertas ainda).";
                return;
            }

            if (visible == 0)
            {
                var sampleBrokers = snapshot.Bids
                    .Concat(snapshot.Asks)
                    .Select(l => l.Broker)
                    .Where(b => !string.IsNullOrWhiteSpace(b))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(5)
                    .ToList();

                string brokerHint = sampleBrokers.Count > 0
                    ? $" Corretoras no livro agora: {string.Join(", ", sampleBrokers)}."
                    : " As ofertas ainda não trazem corretora - aguarde alguns segundos ou teste com (todas).";

                TbFilterStatus.Text =
                    $"{ActiveFilters.Count} filtro(s) ativo(s) - nenhuma oferta casou.{brokerHint}";
                return;
            }

            TbFilterStatus.Text =
                $"{ActiveFilters.Count} filtro(s) ativo(s) - {visible} nível(is) visível(eis); filtro só por volume (livro por preço).";
        }

        private void RenderBook(BookSnapshot snapshot)
        {
            int levels = _levels;

            // Book de ofertas individual (por corretora, igual ProfitChart): usa as listas NÃO agregadas
            // (uma linha por oferta real, com Broker/OfferId) em vez de snapshot.Bids/Asks (agregado por preço).
            IReadOnlyList<BookLevel> bidsWork = snapshot.RawBids ?? snapshot.Bids;
            IReadOnlyList<BookLevel> asksWork = snapshot.RawAsks ?? snapshot.Asks;

            int visibleInPool = 0;
            foreach (var level in bidsWork)
            {
                if (PassaFiltro(level))
                    visibleInPool++;
            }

            foreach (var level in asksWork)
            {
                if (PassaFiltro(level))
                    visibleInPool++;
            }

            var bids = SelectBookLevels(bidsWork, levels, PassaFiltro);
            var asks = SelectBookLevels(asksWork, levels, PassaFiltro);
            // Filtros podem ocultar o melhor nível de um lado e “cruzar” só no subconjunto exibido.
            BookSnapshotAggregation.NormalizeEconomicalTop(bids, asks);

            var statusSnap = snapshot with { Bids = bidsWork, Asks = asksWork };
            UpdateFilterStatus(statusSnap, visibleInPool);

            // Sem manter topo antigo quando um lado fica vazio (filtro/DLL): evita “compra > venda” na tela.
            _lastBid = bids.Count > 0 ? bids[0].Price : 0;
            _lastAsk = asks.Count > 0 ? asks[0].Price : 0;

            if (_lastBid > 0)
                _bookAnalyzer.SetVWAPDistance((double)_lastBid, _deltaEngine.SessionVWAP);

            double maxBv = 1;
            foreach (var b in bids)
                maxBv = Math.Max(maxBv, (double)b.Volume);
            double maxAv = 1;
            foreach (var a in asks)
                maxAv = Math.Max(maxAv, (double)a.Volume);
            _maxBidVol = maxBv;
            _maxAskVol = maxAv;

            double rawMax = Math.Max(_maxBidVol, _maxAskVol);
            if (rawMax < 1) rawMax = 1;
            if (_bookBarMaxVolSmoothed < 1 || double.IsNaN(_bookBarMaxVolSmoothed))
                _bookBarMaxVolSmoothed = rawMax;
            else if (rawMax > _bookBarMaxVolSmoothed)
                _bookBarMaxVolSmoothed = rawMax;
            else
                _bookBarMaxVolSmoothed = _bookBarMaxVolSmoothed * 0.82 + rawMax * 0.18;

            double maxVol = Math.Max(_bookBarMaxVolSmoothed, 1);
            const double maxBarWidth = 80;

            RenderBookSide(bids, BidRows, _bidRowsByKey, isBidSide: true,  maxVol, maxBarWidth);
            RenderBookSide(asks, AskRows, _askRowsByKey, isBidSide: false, maxVol, maxBarWidth);

            if (_lastBid > 0 && _lastAsk > 0)
            {
                if (_lastAsk > _lastBid)
                {
                    decimal spread = (_lastAsk - _lastBid) / 5m;
                    TbSpread.Text = $"{spread:N0} pts";
                }
                else
                    TbSpread.Text = "-- pts"; // filtros/UI ainda inconsistente; motor já normaliza o snapshot
            }
            else
            {
                TbSpread.Text = "-- pts";
            }

            int pool = bidsWork.Count + asksWork.Count;
            int rows = Math.Max(BidRows.Count, AskRows.Count);
            string diag = _engine?.GetBookDiagnostics(_bookOfferTicker) ?? string.Empty;
            TbDepth.Text = ActiveFilters.Count == 0
                ? $"AGG {pool} níveis | {rows}/{levels} grid | {diag}"
                : $"AGG filt {visibleInPool}/{pool} | {rows}/{levels} | {diag}";

            if (_lastBid > 0)
            {
                TbLastPrice.Text = BookSnapshotAggregation.FormatBookPrice(_lastBid);
                TbLastPrice.Foreground = Interlocked.Read(ref _delta) >= 0 ? TbDeltaPositiveBrush : TbDeltaNegativeBrush;
            }
            else
            {
                TbLastPrice.Text = "--";
            }

            TbFooterBid.Text = _lastBid > 0 ? BookSnapshotAggregation.FormatBookPrice(_lastBid) : "--";
            TbFooterAsk.Text = _lastAsk > 0 ? BookSnapshotAggregation.FormatBookPrice(_lastAsk) : "--";
        }

        private void RebuildBookSideRowIndexMap(ObservableCollection<BookSideRowViewModel> rows)
        {
            _bookSideVmToIndexScratch.Clear();
            for (int j = 0; j < rows.Count; j++)
                _bookSideVmToIndexScratch[rows[j]] = j;
        }

        /// <summary>Chave estável por oferta individual (<see cref="BookLevel.OfferId"/>); ofertas sem ID (raras) recebem chave sintética pela posição.</summary>
        private static long BookRowKey(BookLevel lvl, int fallbackIndex) =>
            lvl.OfferId > 0 ? lvl.OfferId : -(1_000_000L + fallbackIndex);

        /// <summary>Uma linha por oferta (corretora + preço reais, igual ProfitChart): <see cref="BookRowKey"/> faz match estável ao snapshot não agregado.</summary>
        private void RenderBookSide(
            IReadOnlyList<BookLevel> visible,
            ObservableCollection<BookSideRowViewModel> rows,
            Dictionary<long, BookSideRowViewModel> rowsByKey,
            bool isBidSide,
            double maxVol,
            double maxBarWidth)
        {
            var targetKeys = new HashSet<long>(visible.Count);
            for (int i = 0; i < visible.Count; i++)
                targetKeys.Add(BookRowKey(visible[i], i));

            _scratchKeysForBook.Clear();
            foreach (long rk in rowsByKey.Keys)
            {
                if (!targetKeys.Contains(rk))
                    _scratchKeysForBook.Add(rk);
            }

            foreach (var extraKey in _scratchKeysForBook)
            {
                if (rowsByKey.Remove(extraKey, out var vmRm))
                    rows.Remove(vmRm);
            }

            // Índice reconstruído só aqui, uma vez. Book por oferta individual reordena bem mais que o
            // antigo book por preço — reconstruir tudo a cada Move/Insert (como antes) virava O(n²) e
            // era exatamente a causa do "lag": em vez disso, cada Move/Insert só corrige as posições que
            // realmente mudaram (proporcional à distância do deslocamento, não ao tamanho da lista).
            RebuildBookSideRowIndexMap(rows);
            for (int i = 0; i < visible.Count; i++)
            {
                var lvl = visible[i];
                long rowKey = BookRowKey(lvl, i);

                BookSideRowViewModel vm;

                if (rowsByKey.TryGetValue(rowKey, out var found))
                {
                    vm = found;
                    if (_bookSideVmToIndexScratch.TryGetValue(vm, out int currentIdx) && currentIdx != i)
                    {
                        // Invariante do laço: posições 0..i-1 já estão finalizadas, então currentIdx > i sempre
                        // que há Move a fazer — só o intervalo [i, currentIdx] muda de posição.
                        rows.Move(currentIdx, i);
                        for (int p = i; p <= currentIdx && p < rows.Count; p++)
                            _bookSideVmToIndexScratch[rows[p]] = p;
                    }
                }
                else if (i < rows.Count && rows[i].RowKey == rowKey)
                {
                    vm = rows[i];
                    rowsByKey[rowKey] = vm;
                }
                else
                {
                    vm = new BookSideRowViewModel { RowKey = rowKey };
                    if (i < rows.Count)
                    {
                        rows.Insert(i, vm);
                        for (int p = i; p < rows.Count; p++)
                            _bookSideVmToIndexScratch[rows[p]] = p;
                    }
                    else
                    {
                        rows.Add(vm);
                        _bookSideVmToIndexScratch[vm] = rows.Count - 1;
                    }

                    rowsByKey[rowKey] = vm;
                }

                UpdateBookSideVm(vm, lvl.Price, lvl, isBidSide, maxVol, maxBarWidth);
            }

            while (rows.Count > visible.Count)
            {
                int last = rows.Count - 1;
                var extra = rows[last];
                rowsByKey.Remove(extra.RowKey);
                rows.RemoveAt(last);
            }
        }

        private void UpdateBookSideVm(
            BookSideRowViewModel vm,
            decimal priceKeyDecimal,
            BookLevel lvl,
            bool isBidSide,
            double maxVol,
            double maxBarWidth)
        {
            string priceKey = BookSnapshotAggregation.FormatBookPrice(lvl.Price);
            bool isBig = lvl.Volume >= _highlightThreshold;
            bool spoof      = _detectorsByPrice.TryGetValue(priceKey, out int dflags) && (dflags & 1) != 0;
            bool iceberg    = (dflags & 2) != 0;
            bool renewable  = (dflags & 4) != 0;
            bool exhaustion = (dflags & 8) != 0;

            string broker = DisplayBookBroker(lvl);
            if (lvl.OfferId <= 0 && string.IsNullOrWhiteSpace(broker))
                broker = "Total";
            broker = broker.Length > 7 ? broker[..7] : broker;

            string volColor;
            string barColor;
            Brush rowBg;
            if (isBidSide)
            {
                volColor = isBig ? "#00E676" : "#CCCCCC";
                barColor = isBig ? "#00E676" : "#00C853";
                rowBg = isBig ? BookBidHighlightBg : Brushes.Transparent;
            }
            else
            {
                volColor = isBig ? "#FF4569" : "#CCCCCC";
                barColor = isBig ? "#FF4569" : "#FF1744";
                rowBg = isBig ? BookAskHighlightBg : Brushes.Transparent;
            }

            string det = string.Empty;
            string detColor = "#FFFFFF";
            if (spoof)           { det = "S"; detColor = "#FF1744"; }
            else if (iceberg)    { det = "I"; detColor = "#2979FF"; }
            else if (renewable)  { det = "R"; detColor = "#00C853"; }
            else if (exhaustion) { det = "E"; detColor = "#FFD600"; }

            vm.SyncFromSnapshot(
                priceKeyDecimal,
                broker,
                lvl.Volume.ToString(),
                priceKey,
                volColor,
                isBig ? FontWeights.Bold : FontWeights.Normal,
                maxVol > 0 ? (double)lvl.Volume / maxVol * maxBarWidth : 0,
                barColor,
                isBig ? 0.9 : 0.45,
                rowBg,
                det,
                detColor);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  TIMERS UI — tape + HUD leves (~30 Hz); livro/FlowCandle/heavy (~14 Hz)
        // ══════════════════════════════════════════════════════════════════════

        private void UiPulseTimer_Tick(object? sender, EventArgs e)
        {
            try { UiPulseTickCore(); }
            catch (Exception ex) { App.AppendCrashLog(nameof(UiPulseTimer_Tick), ex); }
        }

        /// <summary>
        /// Compara horário do último negócio com o relógio local e mostra atrasos nominais no pipeline.
        /// </summary>
        private void RefreshTradeLatencyHud()
        {
            string pcClock = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

            string neg;
            DateTime? ex;
            DateTime? rx;
            lock (_tradeLagSync)
            {
                neg = _tradeNegBolsaClock;
                ex = _tradeExchangeUtcSnap;
                rx = _tradeReceivedUtcSnap;
            }

            if (string.IsNullOrEmpty(neg))
            {
                TbDataLatency.Text = $"Aguardando negócio… | PC {pcClock}";
                return;
            }

            DateTime utcNow = DateTime.UtcNow;

            string line;
            if (ex.HasValue && rx.HasValue)
            {
                double dllToMotor = (rx.Value - ex.Value).TotalMilliseconds;
                double motorToHud = Math.Max(0, (utcNow - rx.Value).TotalMilliseconds);
                line = $"{neg} negócio | DLL→motor {dllToMotor:F0} ms · motor→HUD {motorToHud:F0} ms | PC {pcClock}";
            }
            else if (rx.HasValue)
            {
                double motorToHud = Math.Max(0, (utcNow - rx.Value).TotalMilliseconds);
                line = $"{neg} (DLL sem data/hora) · motor→HUD {motorToHud:F0} ms | PC {pcClock}";
            }
            else
            {
                line = $"{neg} | PC {pcClock}";
            }

            TbDataLatency.Text = line;
        }

        private void UiPulseTickCore()
        {
            FlushPendingTape();
            RefreshTradeLatencyHud();

            long delta = Interlocked.Read(ref _delta);
            string deltaStr = delta >= 0 ? $"+{delta}" : $"{delta}";
            if (_lastDeltaText != deltaStr)
            {
                _lastDeltaText = deltaStr;
                TbDelta.Text = deltaStr;
                TbFooterDelta.Text = deltaStr;
                var brush = delta >= 0 ? TbDeltaPositiveBrush : TbDeltaNegativeBrush;
                TbDelta.Foreground = brush;
                TbFooterDelta.Foreground = brush;
            }

            long buyA = Interlocked.Read(ref _buyAggression);
            long sellA = Interlocked.Read(ref _sellAggression);
            long total = buyA + sellA;
            if (total > 0)
            {
                double buyPct = (double)buyA / total;
                double sellPct = 1.0 - buyPct;
                TbBuyPct.Text  = $"Comp {buyPct:P0}";
                TbSellPct.Text = $"Vend {sellPct:P0}";
                var pressureContainer = BidPressureBar.Parent as Grid;
                double containerWidth = pressureContainer?.ActualWidth ?? 200;
                BidPressureBar.Width = Math.Max(0, containerWidth * buyPct);
                AskPressureBar.Width = Math.Max(0, containerWidth * sellPct);
            }

            TbSpoofCount.Text      = _spoofCount.ToString();
            TbIcebergCount.Text    = _icebergCount.ToString();
            TbRenewableCount.Text  = _renewableCount.ToString();
            TbExhaustionCount.Text = _exhaustionCount.ToString();

            long wb2, ws2;
            lock (_aggressionWindow2)
            {
                wb2 = _windowBuy2;
                ws2 = _windowSell2;
            }

            long w2Total = wb2 + ws2;
            if (w2Total > 0)
            {
                double w2BuyPct  = (double)wb2 / w2Total;
                double w2SellPct = (double)ws2 / w2Total;
                TbBuyPctWindow2.Text  = $"Comp {w2BuyPct:P0}";
                TbSellPctWindow2.Text = $"Vend {w2SellPct:P0}";
                var w2Container = BidWindow2Bar.Parent as Grid;
                double w2Width  = w2Container?.ActualWidth ?? 200;
                BidWindow2Bar.Width = w2Width * w2BuyPct;
                AskWindow2Bar.Width = w2Width * w2SellPct;
            }

            long wb, ws;
            lock (_aggressionWindow)
            {
                wb = _windowBuy;
                ws = _windowSell;
            }

            long wTotal = wb + ws;
            if (wTotal > 0)
            {
                double wBuyPct  = (double)wb / wTotal;
                double wSellPct = (double)ws / wTotal;
                TbBuyPctWindow.Text  = $"Comp {wBuyPct:P0}";
                TbSellPctWindow.Text = $"Vend {wSellPct:P0}";
                var wContainer = BidWindowBar.Parent as Grid;
                double wWidth  = wContainer?.ActualWidth ?? 200;
                BidWindowBar.Width = wWidth * wBuyPct;
                AskWindowBar.Width = wWidth * wSellPct;
                TbBuyPctWindow.Foreground  = wBuyPct > 0.6
                    ? TbWinBuyPctStrongFg
                    : TbWinBuyPctNeutralFg;
                TbSellPctWindow.Foreground = wSellPct > 0.6
                    ? TbWinSellPctStrongFg
                    : TbWinSellPctNeutralFg;
            }

            TbTradesPerSec.Text = $"{_tradesLastSec}/s";
            TbBooksPerSec.Text  = $"{_booksLastSec}/s";

            long tc = Interlocked.Read(ref _tradeCount);
            string tcStr = tc.ToString();
            if (_lastTradeCountText != tcStr)
            {
                _lastTradeCountText = tcStr;
                TbTradeCount.Text = tcStr;
            }

            long bc = Interlocked.Read(ref _bookCount);
            string bcStr = bc.ToString();
            if (_lastBookCountText != bcStr)
            {
                _lastBookCountText = bcStr;
                TbBookCount.Text = bcStr;
            }

            long d = Interlocked.Read(ref _delta);
            TbLastPrice.Foreground = d >= 0 ? TbDeltaPositiveBrush : TbDeltaNegativeBrush;

            // Repinte do livro também no pulso ~33Hz (além do mailbox) — evita esperar só o timer ~70ms quando o throttle segurou.
            TryRenderBookThrottled();
        }

        private void UiTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                UiTimer_TickCore();
            }
            catch (Exception ex)
            {
                App.AppendCrashLog(nameof(UiTimer_Tick), ex);
            }
        }

        private void UiTimer_TickCore()
        {
            _uiTicks++;

            FlushPendingFlowCandle();

            TryRenderBookThrottled();

            if ((_uiTicks & 31) == 0 || _uiTicks <= 2)
                TbHeapMb.Text = $"{GC.GetTotalMemory(false) / 1024.0 / 1024.0:N1} MB";
        }

        // ══════════════════════════════════════════════════════════════════════
        //  DETECTORES
        // ══════════════════════════════════════════════════════════════════════

        private void HandleSpoof(SpoofEvent d)
        {
            if (_spoofMinVol > 0 && d.VolumeBefore < _spoofMinVol) return;

            string key = BookSnapshotAggregation.FormatBookPrice(d.Price);

            // O spoof ocorre quando a ordem some - preço pode não estar mais no book.
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
                        MarkPriceDetector(BookSnapshotAggregation.FormatBookPrice(nearest.Price), 0);
                }
                else
                {
                    // Busca ask mais próximo do preço do spoof
                    var nearest = _lastSnapshot.Asks
                        .OrderBy(a => Math.Abs((double)(a.Price - d.Price)))
                        .FirstOrDefault();
                    if (nearest != null)
                        MarkPriceDetector(BookSnapshotAggregation.FormatBookPrice(nearest.Price), 0);
                }
            }

            Dispatcher.InvokeAsync(() =>
            {
                _spoofCount++;
                AddAlert("S", d.Price, $"{d.Side} | {d.Broker} | {d.VolumeBefore}→{d.VolumeAfter}");
                AddSpoofNotification(d.Side, d.Broker, d.VolumeBefore, d.Price, isCyclic: false);
                string tipo = d.Side.Contains("COMPRA", StringComparison.OrdinalIgnoreCase) ? "BUY" : "SELL";
                NotifyAnaliseFlowAlert("AggressiveFlow", tipo,
                    $"Spoof {d.Side} | {d.Broker} | {d.VolumeBefore}→{d.VolumeAfter}", d.Price, 0.68);
            }, DispatcherPriority.Background);
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
                Price      = BookSnapshotAggregation.FormatBookPrice(price),
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

            string key = BookSnapshotAggregation.FormatBookPrice(d.FromPrice);
            MarkPriceDetector(key, 1);
            Dispatcher.InvokeAsync(() =>
            {
                _icebergCount++;
                AddAlert("I", d.FromPrice, $"{d.Direction} | {d.Broker} | vol:{d.Volume}");
                string tipo = d.Direction.Contains("COMPRA", StringComparison.OrdinalIgnoreCase) ? "BUY" : "SELL";
                NotifyAnaliseFlowAlert("PassiveAbsorption", tipo,
                    $"Iceberg {d.Direction} | {d.Broker} | vol {d.Volume}", d.FromPrice, 0.78);
            }, DispatcherPriority.Background);
            ClearPriceDetectorAfter(key, 1);
        }

        private void HandleRenewable(RenewableEvent d)
        {
            if (_renewableMinVol > 0 && d.VolumePerCycle < _renewableMinVol) return;

            string key = BookSnapshotAggregation.FormatBookPrice(d.Price);
            MarkPriceDetector(key, 2);
            Dispatcher.InvokeAsync(() =>
            {
                _renewableCount++;
                AddAlert("R", d.Price, $"{d.Side} | {d.Broker} | {d.Renewals}x renovações");
                string tipo = d.Side.Contains("COMPRA", StringComparison.OrdinalIgnoreCase) ? "BUY" : "SELL";
                NotifyAnaliseFlowAlert("ReversalPattern", tipo,
                    $"Renovável {d.Side} | {d.Broker} | {d.Renewals}x", d.Price, 0.71);
            }, DispatcherPriority.Background);
            ClearPriceDetectorAfter(key, 2);
        }

        private void HandleExhaustion(ExhaustionEvent d)
        {
            if (_exhaustionMinVol > 0 && d.NumTrades < _exhaustionMinVol) return;

            string key = BookSnapshotAggregation.FormatBookPrice(d.PrecoInicial);
            MarkPriceDetector(key, 3);
            Dispatcher.InvokeAsync(() =>
            {
                _exhaustionCount++;
                AddAlert("E", d.PrecoInicial, $"{d.LadoAgressor} | {d.Ticker} | {d.NumTrades} trades");
                string tipo = d.LadoAgressor.Contains("COMPRA", StringComparison.OrdinalIgnoreCase) ? "BUY" : "SELL";
                NotifyAnaliseFlowAlert("AbsorptionReversal", tipo,
                    $"Exaustão {d.LadoAgressor} | {d.NumTrades} trades", d.PrecoInicial, 0.74);
            }, DispatcherPriority.Background);
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
        //  ALERTAS - agrupamento por tipo+segundo
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
                "S" => $"Spoof - {BookSnapshotAggregation.FormatBookPrice(price)}",
                "I" => $"Iceberg - {BookSnapshotAggregation.FormatBookPrice(price)}",
                "R" => $"Renewable - {BookSnapshotAggregation.FormatBookPrice(price)}",
                _   => $"Exhaustion - {BookSnapshotAggregation.FormatBookPrice(price)}"
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

        private void BtnHistory_Click(object sender, RoutedEventArgs e) => OpenDownloadHistoryWindow();

        private void OpenDownloadHistoryWindow()
        {
            try
            {
                var w = new DownloadHistoryWindow(
                    this,
                    () => _profitDllConnected || (_isRealMarket && ProfitMarketInit.IsDllInitializedInProcess),
                    _profitCredentials);
                w.Show();
            }
            catch (Exception ex)
            {
                App.AppendCrashLog(nameof(OpenDownloadHistoryWindow), ex);
                _ = MessageBox.Show(
                    $"Não foi possível abrir a janela de histórico.\n\n{ex.Message}",
                    "Histórico",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        private void BtnAgentPanel_Click(object sender, RoutedEventArgs e) => ToggleLeituraFluxoWindow();

        /// <summary>Mostra/oculta a janela de Leitura de Fluxo. Chamado tanto pelo botão do MainWindow
        /// quanto pelo botão robozinho embutido no FlowScorePanel.</summary>
        private void ToggleLeituraFluxoWindow()
        {
            StartLeituraFluxoIfNeeded();
            if (_leituraFluxoWindow == null) return;
            if (_leituraFluxoWindow.IsVisible)
                _leituraFluxoWindow.Hide();
            else
            {
                _leituraFluxoWindow.Show();
                _leituraFluxoWindow.Activate();
            }
        }

        /// <summary>Vincula análise quantitativa ao motor ao vivo (chamado pelo botão e ao abrir a janela).</summary>
        public void WireAnaliseQuantitativa(AnaliseQuantitativaWindow window)
        {
            _analiseQuantWindow = window;
            _analiseSink.Bind(window);
            _engine.SetAnaliseQuantSink(_analiseSink);

            window.AttachLiveFeed(
                _deltaEngine,
                getSessionDelta: () => Interlocked.Read(ref _delta),
                getAggression1Min: () =>
                {
                    lock (_aggressionWindow)
                        return (_windowBuy, _windowSell);
                },
                getTradesPerSec: () => _tradesLastSec,
                _marketDataManager,
                isMarketLive: () => _engine != null && _engine.Status == ConnectionStatus.Connected);

            AnaliseQuantLiveHub.Register(window);
        }

        private void NotifyAnaliseFlowAlert(string detector, string tipo, string mensagem, decimal preco, double probabilidade = 0.75)
        {
            if (_analiseQuantWindow == null || !_analiseQuantWindow.IsLoaded)
                return;

            _analiseSink.OnFlowAlert(detector, tipo, mensagem, preco, probabilidade);
        }

        private void BtnAnaliseQuant_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_analiseQuantWindow == null || !_analiseQuantWindow.IsLoaded)
                    _analiseQuantWindow = new AnaliseQuantitativaWindow(this);
                else
                    WireAnaliseQuantitativa(_analiseQuantWindow);

                if (_analiseQuantWindow.IsVisible)
                    _analiseQuantWindow.Activate();
                else
                    _analiseQuantWindow.Show();
            }
            catch (Exception ex)
            {
                App.AppendCrashLog(nameof(BtnAnaliseQuant_Click), ex);
            }
        }

        private void BtnPregaoVivaVoz_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_pregaoVivaVozWindow == null || !_pregaoVivaVozWindow.IsLoaded)
                    _pregaoVivaVozWindow = new PregaoVivaVozWindow(_isRealMarket);

                _pregaoVivaVozWindow.Owner = this;

                if (_pregaoVivaVozWindow.IsVisible)
                    _pregaoVivaVozWindow.Activate();
                else
                    _pregaoVivaVozWindow.Show();
            }
            catch (Exception ex)
            {
                App.AppendCrashLog(nameof(BtnPregaoVivaVoz_Click), ex);
            }
        }

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
                Close();
        }

        private void BtnAddFilter_Click(object sender, RoutedEventArgs e)
        {
            if (_addingBrokerFilter)
                return;

            _addingBrokerFilter = true;
            try
            {
                if (!int.TryParse(TxFilterVolMin.Text, out int volMin)) volMin = 0;
                if (!int.TryParse(TxFilterVolMax.Text, out int volMax)) volMax = 9999;
                if (volMin > volMax)
                    (volMin, volMax) = (volMax, volMin);

                string broker = ReadFilterBrokerFromUi();

                if (ActiveFilters.Any(f =>
                        string.Equals(f.Broker, broker, StringComparison.OrdinalIgnoreCase)
                        && f.VolMin == volMin
                        && f.VolMax == volMax))
                {
                    return;
                }

                var filter = new BrokerFilter
                {
                    Broker = broker,
                    VolMin = volMin,
                    VolMax = volMax,
                    DisplayText = $"{broker}  [{volMin}–{volMax}]"
                };

                ActiveFilters.Add(filter);
                if (_lastSnapshot != null)
                    RenderBook(_lastSnapshot);
                else
                    TbFilterStatus.Text = $"{ActiveFilters.Count} filtro(s) ativo(s) - aguardando livro da DLL.";
            }
            finally
            {
                _addingBrokerFilter = false;
            }
        }

        // ═══ MÉTODO QUE ESTAVA FALTANDO ═══
        private void BtnRemoveFilter_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is BrokerFilter f)
            {
                ActiveFilters.Remove(f);
                if (ActiveFilters.Count == 0)
                    TbFilterStatus.Text = "Nenhum filtro ativo - todas as ordens exibidas";
                else
                    TbFilterStatus.Text = $"{ActiveFilters.Count} filtro(s) ativo(s)";
                if (_lastSnapshot != null) RenderBook(_lastSnapshot);
            }
        }

        private void BtnClearFilters_Click(object sender, RoutedEventArgs e)
        {
            ActiveFilters.Clear();
            TbFilterStatus.Text = "Nenhum filtro ativo - todas as ordens exibidas";
            if (_lastSnapshot != null) RenderBook(_lastSnapshot);
        }

        private void CbLevels_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CbLevels.SelectedItem is ComboBoxItem item &&
                int.TryParse(item.Content?.ToString(), out int lvl))
            {
                _levels = lvl;
                if (_lastSnapshot != null)
                    RenderBook(_lastSnapshot);
            }
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
            // Novos em cima (offset ~0); se o usuário desce para ver histórico, pára o autoscroll.
            const double topEpsilonPx = 3.0;
            if (e.ExtentHeightChange == 0)
                _userScrolledTape = TapeScrollViewer.VerticalOffset > topEpsilonPx;
            else if (!_userScrolledTape)
                TapeScrollViewer.ScrollToTop();
        }

        private void BtnClearAlerts_Click(object sender, RoutedEventArgs e)
        {
            AlertItems.Clear();
            _alertByKey.Clear();
        }

        protected override void OnClosed(EventArgs e)
        {
            App.AppendLifecycle("MainWindow.OnClosed");
            base.OnClosed(e);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  VIEW MODELS
    // ══════════════════════════════════════════════════════════════════════════

    public class BookSideRowViewModel : INotifyPropertyChanged
    {
        /// <summary>Preço desta oferta (exibição/formatação) — várias linhas podem repetir o mesmo valor quando há mais de uma corretora no nível.</summary>
        public decimal PriceKey { get; set; }

        /// <summary>Chave de identidade estável da linha (OfferId, ou sintética quando ausente) — usada só para reconciliar o grid, não exibida.</summary>
        public long RowKey { get; set; }

        private string _broker = "";
        private string _volume = "";
        private string _price  = "";
        private string _volColor = "#CCCCCC";
        private FontWeight _volWeight = FontWeights.Normal;
        private double _barWidth = 0;
        private string _barColor = "#00C853";
        private double _barOpacity = 0.45;
        private Brush _rowBackground = Brushes.Transparent;
        private string _detector = "";
        private string _detectorColor = "#FFFFFF";

        public string Broker     { get => _broker;     set => Set(ref _broker,     value); }
        public string Volume     { get => _volume;     set => Set(ref _volume,     value); }
        public string Price      { get => _price;      set => Set(ref _price,      value); }
        public string VolColor   { get => _volColor;   set => Set(ref _volColor,   value); }
        public FontWeight VolWeight { get => _volWeight; set => Set(ref _volWeight, value); }
        public double BarWidth   { get => _barWidth;   set => Set(ref _barWidth,   value); }
        public string BarColor   { get => _barColor;   set => Set(ref _barColor,   value); }
        public double BarOpacity { get => _barOpacity; set => Set(ref _barOpacity, value); }
        public Brush  RowBackground { get => _rowBackground; set => Set(ref _rowBackground, value); }
        public string Detector { get => _detector; set => Set(ref _detector, value); }
        public string DetectorColor { get => _detectorColor; set => Set(ref _detectorColor, value); }

        /// <summary>Um único <see cref="INotifyPropertyChanged"/> por atualização de linha.</summary>
        public void SyncFromSnapshot(
            decimal priceKeyDecimal,
            string broker,
            string volume,
            string price,
            string volColor,
            FontWeight volWeight,
            double barWidth,
            string barColor,
            double barOpacity,
            Brush rowBackground,
            string detector,
            string detectorColor)
        {
            bool dirty = PriceKey != priceKeyDecimal
                         || _broker != broker
                         || _volume != volume
                         || _price != price
                         || _volColor != volColor
                         || _volWeight != volWeight
                         || !AreClose(_barWidth, barWidth)
                         || _barColor != barColor
                         || !AreClose(_barOpacity, barOpacity)
                         || !Equals(_rowBackground, rowBackground)
                         || _detector != detector
                         || _detectorColor != detectorColor;

            if (!dirty)
                return;

            PriceKey = priceKeyDecimal;
            _broker = broker;
            _volume = volume;
            _price = price;
            _volColor = volColor;
            _volWeight = volWeight;
            _barWidth = barWidth;
            _barColor = barColor;
            _barOpacity = barOpacity;
            _rowBackground = rowBackground;
            _detector = detector;
            _detectorColor = detectorColor;

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }

        private static bool AreClose(double a, double b)
            => Math.Abs(a - b) < 0.0005;

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