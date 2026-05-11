using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MarketCore.Contracts;
using MarketCore.Models;
using MarketCore.HistoricalImporter;

namespace MarketCore.Providers.Nelogica
{
    public class ProfitDLLProvider : IMarketDataProvider
    {
        #region Constantes

        private const string DLL_PATH      = @"ProfitDLL64.dll";
        private const string EXCHANGE_BMF  = "F";
        private const string EXCHANGE_BVMF = "B";

        #endregion

        #region Structs e Delegates da Nelogica

        // TAssetIDRec no manual é packed record.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
        private struct TAssetID
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string Ticker;
            [MarshalAs(UnmanagedType.LPWStr)] public string Bolsa;
            public int nFeedType;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TConnectorAssetIdentifier
        {
            public byte Version;
            [MarshalAs(UnmanagedType.LPWStr)] public string Ticker;
            [MarshalAs(UnmanagedType.LPWStr)] public string Exchange;
            public byte FeedType;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TConnectorPriceGroup
        {
            public byte   Version;
            public double Price;
            public uint   Count;
            public long   Quantity;
            public uint   PriceGroupFlags;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // CORREÇÃO: Layout do array FullBook conforme manual (V1)
        // Preço(8) + Qtd(4) + Agente(4) + OfferID(8) + TamData(2) + Data(T) = 26+T bytes
        // ═══════════════════════════════════════════════════════════════════════
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct TOfferBookItem
        {
            public double Price;    // offset 8,  8 bytes
            public int    Qtd;      // offset 16, 4 bytes
            public int    Agent;    // offset 20, 4 bytes
            public long   OfferId;  // offset 24, 8 bytes
            // Após OfferId vem: short (tam data) + bytes da data → lido manualmente
        }

        private delegate void TStateCallback(int nResult, int result);

        private delegate void TTradeCallback(
            TAssetID assetId,
            [MarshalAs(UnmanagedType.LPWStr)] string date,
            uint tradeNumber, double price, double vol,
            int qtd, int buyAgent, int sellAgent, int tradeType, int bIsEdit);

        private delegate void TNewDailyCallback(
            TAssetID assetId,
            [MarshalAs(UnmanagedType.LPWStr)] string date,
            double sOpen, double sHigh, double sLow, double sClose,
            double sVol, double sAjuste, double sMaxLimit, double sMinLimit,
            double sVolBuyer, double sVolSeller,
            int nQtd, int nNegocios, int nContratosOpen,
            int nQtdBuyer, int nQtdSeller, int nNegBuyer, int nNegSeller);

        private delegate void TPriceBookCallback(
            TAssetID assetId, int nAction, int nPosition,
            int side, int nQtd, int nCount, double sPrice,
            IntPtr pArraySell, IntPtr pArrayBuy);

        private delegate void TOfferBookCallbackV2(
            TAssetID assetId, int nAction, int nPosition,
            int side, long nQtd, int nAgent, long nOfferID, double sPrice,
            ushort bHasPrice, ushort bHasQtd, ushort bHasDate, ushort bHasOfferID, ushort bHasAgent,
            [MarshalAs(UnmanagedType.LPWStr)] string date,
            IntPtr pArraySell, IntPtr pArrayBuy);

        private delegate void THistoryTradeCallback(
            TAssetID assetId,
            [MarshalAs(UnmanagedType.LPWStr)] string date,
            uint tradeNumber, double price, double vol,
            int qtd, int buyAgent, int sellAgent, int tradeType);

        private delegate void TProgressCallBack(TAssetID assetId, int nProgress);
        private delegate void TNewTinyBookCallBack(TAssetID assetId, double price, int qtd, int side);

        private delegate void TChangeCotation(
            TAssetID assetId,
            [MarshalAs(UnmanagedType.LPWStr)] string date,
            uint tradeNumber, double sPrice);

        private delegate void TConnectorPriceDepthCallback(
            TConnectorAssetIdentifier assetID, byte side, int position, byte updateType);

        #endregion

        #region DLL Imports

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int DLLInitializeMarketLogin(
            [MarshalAs(UnmanagedType.LPWStr)] string activationKey,
            [MarshalAs(UnmanagedType.LPWStr)] string user,
            [MarshalAs(UnmanagedType.LPWStr)] string password,
            TStateCallback       stateCallback,
            TTradeCallback       newTradeCallback,
            TNewDailyCallback    newDailyCallback,
            TPriceBookCallback   priceBookCallback,
            TOfferBookCallbackV2 offerBookCallback,
            THistoryTradeCallback newHistoryCallback,
            TProgressCallBack    progressCallBack,
            TNewTinyBookCallBack newTinyBookCallBack);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int SetChangeCotationCallback(TChangeCotation a_ChangeCotation);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int SetOfferBookCallback(TOfferBookCallbackV2 a_OfferBookCallback);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int SetOfferBookCallbackV2(TOfferBookCallbackV2 a_OfferBookCallback);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int SetPriceDepthCallback(TConnectorPriceDepthCallback a_Callback);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int SubscribeTicker(
            [MarshalAs(UnmanagedType.LPWStr)] string pwcTicker,
            [MarshalAs(UnmanagedType.LPWStr)] string pwcBolsa);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int UnsubscribeTicker(
            [MarshalAs(UnmanagedType.LPWStr)] string pwcTicker,
            [MarshalAs(UnmanagedType.LPWStr)] string pwcBolsa);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int SubscribeOfferBook(
            [MarshalAs(UnmanagedType.LPWStr)] string pwcTicker,
            [MarshalAs(UnmanagedType.LPWStr)] string pwcBolsa);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int UnsubscribeOfferBook(
            [MarshalAs(UnmanagedType.LPWStr)] string pwcTicker,
            [MarshalAs(UnmanagedType.LPWStr)] string pwcBolsa);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int SubscribePriceDepth(in TConnectorAssetIdentifier assetID);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int UnsubscribePriceDepth(in TConnectorAssetIdentifier assetID);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int GetPriceDepthSideCount(
            in TConnectorAssetIdentifier assetID, byte side);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int GetPriceGroup(
            in TConnectorAssetIdentifier assetID,
            byte side, int position, ref TConnectorPriceGroup priceGroup);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int GetAgentNameLength(int nAgentID, int nShortName);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int GetAgentName(
            int nCount, int nAgentID,
            [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwcAgent,
            int nShortName);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall, EntryPoint = "GetAgentName")]
        private static extern int GetAgentNamePtr(
            int nCount, int nAgentID,
            IntPtr pwcAgent,
            int nShortName);

        #endregion

        #region Tipos internos para filas

        private readonly struct RawTrade
        {
            public readonly string Ticker;
            public readonly double Price;
            public readonly int    Qtd;
            public readonly int    BuyAgent;
            public readonly int    SellAgent;
            public readonly int    TradeType;
            public RawTrade(string t, double p, int q, int b, int s, int tt)
            { Ticker=t; Price=p; Qtd=q; BuyAgent=b; SellAgent=s; TradeType=tt; }
        }

        private readonly struct RawBook
        {
            public readonly string Ticker;
            public readonly int    Action;
            public readonly int    Position;
            public readonly int    Side;
            public readonly double Price;
            public readonly int    Volume;
            public readonly int    Agent;
            public readonly long   OfferId;
            public readonly DateTime? ExchangeTime;
            public RawBook(string t, int ac, int pos, int si, double p, int v, int a, long o, DateTime? exch = null)
            { Ticker=t; Action=ac; Position=pos; Side=si; Price=p; Volume=v; Agent=a; OfferId=o; ExchangeTime=exch; }
        }

        /// <summary>Tenta interpretar <paramref name="date"/> do OfferBookCallback (vários layouts já vistos em BMF).</summary>
        private static bool TryParseOfferBookDate(string? date, out DateTime utc)
        {
            utc = default;
            if (string.IsNullOrWhiteSpace(date)) return false;
            string s = date.Trim();
            ReadOnlySpan<string> formats =
            [
                "dd/MM/yyyy HH:mm:ss.fff",
                "dd/MM/yyyy HH:mm:ss",
                "yyyy-MM-dd HH:mm:ss.fff",
                "yyyy-MM-dd HH:mm:ss",
                "HH:mm:ss.fff",
                "HH:mm:ss"
            ];
            DateTime dt;
            if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeLocal | System.Globalization.DateTimeStyles.AllowWhiteSpaces, out dt)
                || DateTime.TryParse(s, System.Globalization.CultureInfo.GetCultureInfo("pt-BR"),
                    System.Globalization.DateTimeStyles.AssumeLocal | System.Globalization.DateTimeStyles.AllowWhiteSpaces, out dt))
            {
                utc = DateTime.SpecifyKind(dt, DateTimeKind.Local).ToUniversalTime();
                return true;
            }

            foreach (var f in formats)
            {
                if (DateTime.TryParseExact(s, f, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeLocal, out dt))
                {
                    utc = DateTime.SpecifyKind(dt, DateTimeKind.Local).ToUniversalTime();
                    return true;
                }
            }

            return false;
        }

        /// <summary>Fila única: delta incremental ou substituição completa (ordem preservada).</summary>
        private readonly struct BookWorkItem
        {
            public readonly bool IsFullRefresh;
            public readonly RawBook Delta;
            public readonly string? FullTicker;
            public readonly BookLevel[]? FullBids;
            public readonly BookLevel[]? FullAsks;

            private BookWorkItem(bool full, RawBook d, string? ft, BookLevel[]? b, BookLevel[]? a)
            {
                IsFullRefresh = full;
                Delta = d;
                FullTicker = ft;
                FullBids = b;
                FullAsks = a;
            }

            public static BookWorkItem FromDelta(RawBook d) => new(false, d, null, null, null);

            public static BookWorkItem FromFull(string ticker, BookLevel[] bids, BookLevel[] asks)
                => new(true, default, ticker, bids, asks);
        }

        private readonly struct RawDepth
        {
            public readonly byte Side;
            public readonly byte UpdateType;
            public RawDepth(byte s, byte u) { Side=s; UpdateType=u; }
        }

        #endregion

        #region Campos Privados

        private readonly ConnectionLogger _logger;
        private readonly object _lock = new object();
        private readonly List<string> _subscribedTickers = new();

        // Filas lock-free
        private readonly ConcurrentQueue<RawTrade> _tradeQueue = new();
        private readonly ConcurrentQueue<BookWorkItem> _bookQueue  = new();
        private readonly ConcurrentQueue<RawDepth> _depthQueue = new();

        // Cache de corretoras (concurrent: threads separadas de livro e negócios).
        private readonly ConcurrentDictionary<int, string> _brokerCache = new();
        private readonly ConcurrentQueue<int> _brokerResolveQueue = new();

        // Threads de processamento: livro e negócios separados para trades não atrasarem ofertas.
        private Thread? _bookProcessingThread;
        private Thread? _tradeProcessingThread;
        private volatile bool _processingRunning = false;

        private bool _disposed       = false;
        private bool _initialized    = false;
        private ProviderCredentials? _lastCredentials = null;

        // GC protection dos delegates
        private TStateCallback?               _stateCallback;
        private TTradeCallback?               _tradeCallback;
        private TNewDailyCallback?            _dailyCallback;
        private TPriceBookCallback?           _priceBookCallback;
        private TOfferBookCallbackV2?         _offerBookCallback;
        private THistoryTradeCallback?        _historyCallback;
        private TProgressCallBack?            _progressCallback;
        private TNewTinyBookCallBack?         _tinyBookCallback;
        private TChangeCotation?              _cotationCallback;
        private TConnectorPriceDepthCallback? _priceDepthCb;

        private volatile bool _readyToSubscribe = false;
        private TConnectorAssetIdentifier _currentAssetID;

        // Log RAW do OfferBook
        private System.IO.StreamWriter? _rawBookLog;
        private readonly object _rawLogLock = new object();
        private int _rawLogCount = 0;
        private const int RAW_LOG_MAX = 500;

        /// <summary>Desligado por padrão: log síncrono + lock no callback da DLL atrasam ofertas.</summary>
        private static readonly bool EnableRawOfferBookLog = false;

        #endregion

        #region IMarketDataProvider — Eventos

        public event Action<TradeEvent>?             OnTrade;
        public event Action<BookLevel>?              OnBook;
        public event Action<BookFullRefresh>?        OnBookFullRefresh;
        public event Action<QuoteEvent>?             OnQuote;
        public event Action<ConnectionChangedEvent>? OnConnectionChanged;
        /// <summary>
        /// Encaminha negócios históricos vindos do callback nativo da DLL.
        /// Útil para módulos externos (ex.: importador histórico) sem acoplar ao provider.
        /// </summary>
        public static event Action<string, string, uint, double, double, int, int, int, int>? OnNativeHistoryTrade;

        #endregion

        #region IMarketDataProvider — Propriedades

        public string ProviderName => "Nelogica ProfitDLL";
        public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;
        public IReadOnlyList<string> SubscribedTickers
        {
            get { lock (_lock) return _subscribedTickers.AsReadOnly(); }
        }

        #endregion

        #region Construtor

        public ProfitDLLProvider()
        {
            _logger = new ConnectionLogger();
            _logger.Log($"{ProviderName} inicializado");

            if (EnableRawOfferBookLog)
            {
                try
                {
                    string logPath = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "MarketCore", "offerbook_raw.txt");
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(logPath)!);
                    _rawBookLog = new System.IO.StreamWriter(logPath, append: false, System.Text.Encoding.UTF8)
                    {
                        AutoFlush = true
                    };
                    _rawBookLog.WriteLine($"=== OfferBook RAW Log — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                    _rawBookLog.WriteLine("Colunas: TIME | TICKER | ACTION | POS | SIDE | QTD | AGENT | OFFER_ID | PRICE | bHasPrice | bHasQtd | bHasOfferID | bHasAgent");
                    _logger.Log($"[RAW LOG] OfferBook log em: {logPath}");
                }
                catch (Exception ex)
                {
                    _logger.Log($"[RAW LOG] Falha ao criar log: {ex.Message}");
                }
            }
        }

        #endregion

        #region Thread de Processamento

        private void StartProcessingThread()
        {
            _processingRunning = true;
            _bookProcessingThread = new Thread(BookProcessingLoop)
            {
                IsBackground = true,
                Name = "ProfitDLL-Book",
                Priority = ThreadPriority.AboveNormal
            };
            _tradeProcessingThread = new Thread(TradeProcessingLoop)
            {
                IsBackground = true,
                Name = "ProfitDLL-Trades",
                Priority = ThreadPriority.AboveNormal
            };
            _bookProcessingThread.Start();
            _tradeProcessingThread.Start();
        }

        private void StopProcessingThread()
        {
            _processingRunning = false;
            _bookProcessingThread?.Join(TimeSpan.FromSeconds(3));
            _tradeProcessingThread?.Join(TimeSpan.FromSeconds(3));
        }

        private void BookProcessingLoop()
        {
            const int maxBooksPerSlice = 16384;

            while (_processingRunning)
            {
                int bookSlice = 0;
                while (bookSlice < maxBooksPerSlice && _bookQueue.TryDequeue(out var work))
                {
                    bookSlice++;
                    try
                    {
                        if (work.IsFullRefresh)
                        {
                            OnBookFullRefresh?.Invoke(new BookFullRefresh(
                                work.FullTicker!,
                                work.FullBids ?? Array.Empty<BookLevel>(),
                                work.FullAsks ?? Array.Empty<BookLevel>()));
                            continue;
                        }

                        RawBook rawBook = work.Delta;

                        if (rawBook.Ticker == "__CLEAR__")
                        {
                            OnBook?.Invoke(new BookLevel(
                                Ticker:  _subscribedTickers.Count > 0 ? _subscribedTickers[0] : "?",
                                Side:    rawBook.Side == 0 ? BookSide.Bid : BookSide.Ask,
                                Price:   0,
                                Volume:  -1,
                                Broker:  string.Empty,
                                Time:    DateTime.Now,
                                OfferId: 0
                            ));
                            continue;
                        }

                        if (rawBook.Agent > 0)
                            GetBrokerNameSafe(rawBook.Agent);
                        string broker = rawBook.Agent > 0
                            ? (_brokerCache.TryGetValue(rawBook.Agent, out var bn) ? (bn ?? string.Empty) : rawBook.Agent.ToString())
                            : string.Empty;

                        OnBook?.Invoke(new BookLevel(
                            Ticker:   rawBook.Ticker,
                            Side:     rawBook.Side == 0 ? BookSide.Bid : BookSide.Ask,
                            Price:    (decimal)rawBook.Price,
                            Volume:   rawBook.Volume,
                            Broker:   broker,
                            Time:     DateTime.UtcNow,
                            OfferId:  rawBook.OfferId,
                            Action:   rawBook.Action,
                            Position: rawBook.Position,
                            ExchangeTime: rawBook.ExchangeTime
                        ));
                    }
                    catch (Exception ex) { _logger.Log($"Erro ProcessBook: {ex.Message}"); }
                }

                if (bookSlice > 0)
                    Thread.Sleep(0);
                else
                    Thread.Sleep(1);
            }
        }

        private void TradeProcessingLoop()
        {
            const int maxTradesPerSlice = 2048;

            while (_processingRunning)
            {
                bool hadWork = false;

                int tradeSlice = 0;
                while (tradeSlice < maxTradesPerSlice && _tradeQueue.TryDequeue(out var raw))
                {
                    tradeSlice++;
                    hadWork = true;
                    try
                    {
                        var aggressor = raw.TradeType == 1 ? TradeAggressor.Buy
                                      : raw.TradeType == 2 ? TradeAggressor.Sell
                                      : TradeAggressor.Unknown;

                        int agentId = aggressor == TradeAggressor.Buy ? raw.BuyAgent : raw.SellAgent;

                        OnTrade?.Invoke(new TradeEvent(
                            Ticker:    raw.Ticker ?? string.Empty,
                            Price:     (decimal)raw.Price,
                            Volume:    raw.Qtd,
                            Broker:    _brokerCache.TryGetValue(agentId, out var bn) ? (bn ?? string.Empty) : agentId.ToString(),
                            Aggressor: aggressor,
                            Time:      DateTime.Now
                        ));
                    }
                    catch (Exception ex) { _logger.Log($"Erro ProcessTrade: {ex.Message}"); }
                }

                while (_depthQueue.TryDequeue(out _)) { }

                if (!hadWork)
                {
                    if (_brokerResolveQueue.TryDequeue(out int agentId) && !_brokerCache.ContainsKey(agentId))
                        GetBrokerNameSafe(agentId);
                    else
                        Thread.Sleep(1);
                }
                else
                    Thread.Sleep(0);
            }
        }

        private string GetBrokerNameSafe(int agentId)
        {
            if (agentId <= 0) return string.Empty;
            if (_brokerCache.TryGetValue(agentId, out var cached)) return cached ?? agentId.ToString();

            try
            {
                int bufSize = 256;
                IntPtr buf = Marshal.AllocHGlobal(bufSize * 2);
                try
                {
                    int result = GetAgentNamePtr(bufSize, agentId, buf, 0);
                    string name;
                    if (result == 0)
                    {
                        name = Marshal.PtrToStringUni(buf)?.Trim() ?? agentId.ToString();
                        if (string.IsNullOrWhiteSpace(name)) name = agentId.ToString();
                    }
                    else
                    {
                        name = agentId.ToString();
                    }
                    _brokerCache[agentId] = name;
                    return name;
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
            catch
            {
                _brokerCache[agentId] = agentId.ToString();
                return agentId.ToString();
            }
        }

        #endregion

        #region IMarketDataProvider — Conexão

        public async Task ConnectAsync(ProviderCredentials credentials)
        {
            if (string.IsNullOrEmpty(credentials.Username) ||
                string.IsNullOrEmpty(credentials.Password))
            {
                _logger.Log("✗ Username ou Password vazios");
                SetStatus(ConnectionStatus.Error, "Credenciais inválidas");
                return;
            }

            if (Status == ConnectionStatus.Connected)
            {
                _logger.Log("Já conectado - reutilizando sessão");
                return;
            }

            _lastCredentials  = credentials;
            _readyToSubscribe = false;

            SetStatus(ConnectionStatus.Connecting, "Conectando...");
            _logger.Log($"Iniciando DLLInitializeMarketLogin — usuário: {credentials.Username}");

            try
            {
                _stateCallback     = OnStateCallback;
                _tradeCallback     = OnTradeCallback;
                _dailyCallback     = OnDailyCallback;
                _priceBookCallback = OnPriceBookCallback;
                _offerBookCallback = OnOfferBookCallback;
                _historyCallback   = OnHistoryCallback;
                _progressCallback  = OnProgressCallback;
                _tinyBookCallback  = OnTinyBookCallback;
                _cotationCallback  = OnCotationCallback;
                _priceDepthCb      = OnPriceDepthCallback;

                int result = DLLInitializeMarketLogin(
                    credentials.ActivationCode ?? string.Empty,
                    credentials.Username,
                    credentials.Password,
                    _stateCallback,
                    _tradeCallback,
                    _dailyCallback,
                    _priceBookCallback,
                    _offerBookCallback,
                    _historyCallback,
                    _progressCallback,
                    _tinyBookCallback);

                _logger.Log($"DLLInitializeMarketLogin retornou: {result}");

                if (result != 0)
                {
                    SetStatus(ConnectionStatus.Error, $"Erro ao inicializar: código {result}");
                    return;
                }

                SetChangeCotationCallback(_cotationCallback);
                SetPriceDepthCallback(_priceDepthCb);

                int r2 = SetOfferBookCallbackV2(_offerBookCallback);
                _logger.Log($"SetOfferBookCallbackV2 retornou: {r2}");

                int waited = 0;
                while (!_readyToSubscribe && waited < 20000)
                {
                    await Task.Delay(100);
                    waited += 100;
                }

                if (!_readyToSubscribe)
                {
                    SetStatus(ConnectionStatus.Error, "Timeout aguardando DLL ficar pronta");
                    return;
                }

                StartProcessingThread();

                _initialized = true;
                SetStatus(ConnectionStatus.Connected, "Conectado");
                _logger.Log("✓ CONEXÃO ESTABELECIDA!");
            }
            catch (Exception ex)
            {
                _logger.Log($"✗ Exceção ConnectAsync: {ex.Message}");
                SetStatus(ConnectionStatus.Error, ex.Message);
            }
        }

        public async Task DisconnectAsync()
        {
            await Task.Run(() =>
            {
                StopProcessingThread();
                lock (_lock)
                {
                    _logger.Log("Desconectando...");
                    foreach (var ticker in _subscribedTickers.ToArray())
                        InternalUnsubscribe(ticker);
                    _initialized      = false;
                    _readyToSubscribe = false;
                    SetStatus(ConnectionStatus.Disconnected, "Desconectado");
                    _logger.Log("✓ Desconectado");
                }
            });

            lock (_rawLogLock)
            {
                _rawBookLog?.WriteLine($"=== FIM DO LOG — {DateTime.Now:HH:mm:ss} — {_rawLogCount} eventos ===");
                _rawBookLog?.Close();
                _rawBookLog = null;
            }
        }

        #endregion

        #region Callbacks da DLL

        /// <summary>
        /// Excepções que atravessam código gerido chamado pela DLL fazem muitos runtimes darem teardown imediato.
        /// Jamais propagar erro para unmanaged.
        /// </summary>
        private void SafeDllCallback(string callbackName, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                try
                {
                    _logger.Log($"✗ CALLBACK {callbackName}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                }
                catch { /* evita segunda excepção durante logging */ }
            }
        }

        private void OnStateCallback(int nConnStateType, int result)
            => SafeDllCallback(nameof(OnStateCallback), () => OnStateCallbackCore(nConnStateType, result));

        private void OnStateCallbackCore(int nConnStateType, int result)
        {
            _logger.Log($"[StateCallback] nConnStateType={nConnStateType} result={result}");

            switch (nConnStateType)
            {
                case 0:
                    switch (result)
                    {
                        case 0: _logger.Log("[Login] Conectado"); break;
                        case 1: SetStatus(ConnectionStatus.Error, "Login inválido"); break;
                        case 2: SetStatus(ConnectionStatus.Error, "Senha inválida"); break;
                        case 3: SetStatus(ConnectionStatus.Error, "Senha bloqueada"); break;
                        case 4: SetStatus(ConnectionStatus.Error, "Senha expirada"); break;
                    }
                    break;

                case 1:
                    _logger.Log($"[Broker] result={result}");
                    break;

                case 2:
                    switch (result)
                    {
                        case 0:
                            _logger.Log("[Market] Desconectado — aguardando reconexão automática");
                            _readyToSubscribe = false;
                            if (_initialized)
                            {
                                _initialized = false;
                                SetStatus(ConnectionStatus.Connecting, "Reconectando...");
                            }
                            break;
                        case 4:
                            _logger.Log("[Market] CONECTADO — pronto para subscrições!");
                            _readyToSubscribe = true;
                            if (!_initialized && Status != ConnectionStatus.Disconnected)
                            {
                                _initialized = true;
                                SetStatus(ConnectionStatus.Connected, "Conectado");
                                List<string> pendentes;
                                lock (_lock) pendentes = new List<string>(_subscribedTickers);
                                foreach (var ticker in pendentes)
                                    InternalSubscribe(ticker);
                            }
                            break;
                        default:
                            _logger.Log($"[Market] result={result}");
                            break;
                    }
                    break;

                case 3:
                    if (result != 0)
                        SetStatus(ConnectionStatus.Error, "Licença inválida");
                    else
                        _logger.Log("[Atividade] Válida ✓");
                    break;
            }
        }

        private void OnTradeCallback(
            TAssetID assetId, string date, uint tradeNumber,
            double price, double vol, int qtd,
            int buyAgent, int sellAgent, int tradeType, int bIsEdit)
            => SafeDllCallback(nameof(OnTradeCallback), () =>
                OnTradeCallbackCore(assetId, date, tradeNumber, price, vol, qtd, buyAgent, sellAgent, tradeType, bIsEdit));

        private void OnTradeCallbackCore(
            TAssetID assetId, string date, uint tradeNumber,
            double price, double vol, int qtd,
            int buyAgent, int sellAgent, int tradeType, int bIsEdit)
        {
            // Histórico/replay pode vir só por NewTrade com qtd=0 e quantidade em vol (callback de histórico por vezes nem dispara).
            ProfitHistoryRelay.TryMirrorNewTradeDuringHistoricalDownload(
                assetId.Ticker, date, tradeNumber, price, vol, qtd, buyAgent, sellAgent, tradeType);

            if (price <= 0 || price > 10_000_000 || double.IsNaN(price)) return;
            if (qtd <= 0) return;
            _tradeQueue.Enqueue(new RawTrade(
                assetId.Ticker ?? string.Empty,
                price, qtd, buyAgent, sellAgent, tradeType));
        }

        private void OnPriceBookCallback(
            TAssetID assetId, int nAction, int nPosition,
            int side, int nQtd, int nCount, double sPrice,
            IntPtr pArraySell, IntPtr pArrayBuy)
        {
            // Ignorado — usamos OfferBook
        }

        private void OnOfferBookCallback(
            TAssetID assetId, int nAction, int nPosition,
            int side, long nQtd, int nAgent, long nOfferID, double sPrice,
            ushort bHasPrice, ushort bHasQtd, ushort bHasDate, ushort bHasOfferID, ushort bHasAgent,
            string date,
            IntPtr pArraySell, IntPtr pArrayBuy)
            => SafeDllCallback(nameof(OnOfferBookCallback), () =>
                OnOfferBookCallbackCore(assetId, nAction, nPosition,
                    side, nQtd, nAgent, nOfferID, sPrice,
                    bHasPrice, bHasQtd, bHasDate, bHasOfferID, bHasAgent,
                    date,
                    pArraySell, pArrayBuy));

        private void OnOfferBookCallbackCore(
            TAssetID assetId, int nAction, int nPosition,
            int side, long nQtd, int nAgent, long nOfferID, double sPrice,
            ushort bHasPrice, ushort bHasQtd, ushort bHasDate, ushort bHasOfferID, ushort bHasAgent,
            string date,
            IntPtr pArraySell, IntPtr pArrayBuy)
        {
            string ticker = assetId.Ticker ?? (_subscribedTickers.Count > 0 ? _subscribedTickers[0] : "?");

            // Log RAW
            if (_rawLogCount < RAW_LOG_MAX)
            {
                lock (_rawLogLock)
                {
                    if (_rawLogCount < RAW_LOG_MAX)
                    {
                        _rawBookLog?.WriteLine(
                            $"{DateTime.Now:HH:mm:ss.fff} | {ticker} | act={nAction} | pos={nPosition} | side={side} | qtd={nQtd} | agent={nAgent} | offerID={nOfferID} | price={sPrice:F2} | hasP={bHasPrice} | hasQ={bHasQtd} | hasOID={bHasOfferID} | hasA={bHasAgent}");
                        _rawLogCount++;
                        if (_rawLogCount == RAW_LOG_MAX)
                            _rawBookLog?.WriteLine("=== LIMITE DE LOG ATINGIDO ===");
                    }
                }
            }

            // nAction=3 (atDeleteFrom): remove todas as ofertas a partir de nPosition
            if (nAction == 3)
            {
                // Não limpar o lado inteiro: encaminha ação incremental para o BookState
                // remover a partir da posição informada pela DLL.
                _bookQueue.Enqueue(BookWorkItem.FromDelta(new RawBook(ticker, 3, nPosition, side, 0, 0, 0, 0)));
                return;
            }

            // nAction=4 (atFullBook): reconcilia com arrays oficiais — um único item na fila (menos piscar / deriva).
            if (nAction == 4)
            {
                // ═══════════════════════════════════════════════════════════════════════
                // ATENÇÃO: Na DLL Nelogica, os nomes são da perspectiva do TRADER:
                //   pArrayBuy  = "onde posso COMPRAR" = ofertas de VENDA (ASK/side=1)
                //   pArraySell = "onde posso VENDER"  = ofertas de COMPRA (BID/side=0)
                // ═══════════════════════════════════════════════════════════════════════
                BookLevel[] bids = ParseOfferBookArrayToLevels(ticker, pArraySell, side: 0);
                BookLevel[] asks = ParseOfferBookArrayToLevels(ticker, pArrayBuy,  side: 1);
                _bookQueue.Enqueue(BookWorkItem.FromFull(ticker, bids, asks));
                return;
            }

            // nAction=0 (atAdd), 1 (atEdit), 2 (atDelete)
            int volume = 0;
            if (nAction != 2 && nQtd > 0)
                volume = nQtd > int.MaxValue ? int.MaxValue : (int)nQtd;

            if (nAction != 2)
            {
                if (bHasPrice == 0 || sPrice <= 0 || sPrice > 10_000_000 ||
                    double.IsNaN(sPrice) || double.IsInfinity(sPrice)) return;
            }

            DateTime? exchUtc = null;
            if (bHasDate != 0 && TryParseOfferBookDate(date, out DateTime utcParsed))
                exchUtc = utcParsed;

            // IMPORTANTE: nPosition é contado do FINAL da lista (manual Nelogica)
            // índice_real = size - nPosition - 1
            // O BookState lida com isso na lógica de inserção/deleção
            _bookQueue.Enqueue(BookWorkItem.FromDelta(
                new RawBook(ticker, nAction, nPosition, side, sPrice, volume, nAgent, nOfferID, exchUtc)));
        }

        /// <summary>Converte um lado do array TOfferBook (manual Nelogica V1) em níveis para snapshot atômico.</summary>
        private BookLevel[] ParseOfferBookArrayToLevels(string ticker, IntPtr arrayPtr, int side)
        {
            if (arrayPtr == IntPtr.Zero) return Array.Empty<BookLevel>();

            try
            {
                int Q = Marshal.ReadInt32(arrayPtr, 0);

                if (EnableRawOfferBookLog)
                {
                    lock (_rawLogLock)
                    {
                        if (_rawLogCount < RAW_LOG_MAX)
                            _rawBookLog?.WriteLine($"[FullBook] ticker={ticker} side={side} Q={Q}");
                    }
                }

                if (Q <= 0 || Q > 10000) return Array.Empty<BookLevel>();

                // Ler até Q (teto 10k do manual): truncar aqui deixava filas profundas no mesmo preço incompletas
                // após atFullBook até os deltas “preenchem” — no WIN isso vira dezenas de ofertas faltando na fila.
                int headerCount = Math.Min(Q, 10_000);
                var list = new List<BookLevel>(headerCount);
                int offset = 8;
                BookSide bookSide = side == 0 ? BookSide.Bid : BookSide.Ask;

                // Só lê os primeiros níveis (topo do book). Antes: loop até Q (milhares) no callback nativo → fila gigante.
                for (int i = 0; i < headerCount; i++)
                {
                    try
                    {
                        double price   = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(arrayPtr, offset));
                        int    qtd     = Marshal.ReadInt32(arrayPtr, offset + 8);
                        int    agent   = Marshal.ReadInt32(arrayPtr, offset + 12);
                        long   offerId = Marshal.ReadInt64(arrayPtr, offset + 16);
                        short  tamData = Marshal.ReadInt16(arrayPtr, offset + 24);

                        int entrySize = 26 + (tamData > 0 ? tamData : 0);
                        offset += entrySize;

                        if (price <= 0 || price > 10_000_000 || double.IsNaN(price) || double.IsInfinity(price))
                            continue;
                        if (qtd <= 0) continue;

                        if (agent > 0)
                            GetBrokerNameSafe(agent);
                        string broker = agent > 0
                            ? (_brokerCache.TryGetValue(agent, out var bn) ? (bn ?? string.Empty) : agent.ToString())
                            : string.Empty;

                        list.Add(new BookLevel(
                            Ticker:   ticker,
                            Side:     bookSide,
                            Price:    (decimal)price,
                            Volume:   qtd,
                            Broker:   broker,
                            Time:     DateTime.Now,
                            OfferId:  offerId,
                            Action:   5,
                            Position: i));
                    }
                    catch { break; }
                }

                return list.ToArray();
            }
            catch
            {
                return Array.Empty<BookLevel>();
            }
        }

        private void OnPriceDepthCallback(
            TConnectorAssetIdentifier assetID, byte side, int position, byte updateType)
            => SafeDllCallback(nameof(OnPriceDepthCallback), () => { });

        private void OnCotationCallback(
            TAssetID assetId, string date, uint tradeNumber, double sPrice)
            => SafeDllCallback(nameof(OnCotationCallback), () => OnCotationCallbackCore(assetId, date, tradeNumber, sPrice));

        private void OnCotationCallbackCore(
            TAssetID assetId, string date, uint tradeNumber, double sPrice)
        {
            if (sPrice <= 0 || sPrice > 10_000_000) return;
            try
            {
                OnQuote?.Invoke(new QuoteEvent(
                    Ticker: assetId.Ticker ?? string.Empty,
                    Last:   (decimal)sPrice,
                    Bid: 0, Ask: 0, Open: 0, High: 0, Low: 0,
                    Volume: 0, Time: DateTime.Now
                ));
            }
            catch { }
        }

        private void OnDailyCallback(
            TAssetID assetId, string date,
            double sOpen, double sHigh, double sLow, double sClose,
            double sVol, double sAjuste, double sMaxLimit, double sMinLimit,
            double sVolBuyer, double sVolSeller,
            int nQtd, int nNegocios, int nContratosOpen,
            int nQtdBuyer, int nQtdSeller, int nNegBuyer, int nNegSeller)
            => SafeDllCallback(nameof(OnDailyCallback), () => OnDailyCallbackCore(
                assetId, date, sOpen, sHigh, sLow, sClose, sVol, sAjuste, sMaxLimit, sMinLimit,
                sVolBuyer, sVolSeller,
                nQtd, nNegocios, nContratosOpen, nQtdBuyer, nQtdSeller, nNegBuyer, nNegSeller));

        private void OnDailyCallbackCore(
            TAssetID assetId, string date,
            double sOpen, double sHigh, double sLow, double sClose,
            double sVol, double sAjuste, double sMaxLimit, double sMinLimit,
            double sVolBuyer, double sVolSeller,
            int nQtd, int nNegocios, int nContratosOpen,
            int nQtdBuyer, int nQtdSeller, int nNegBuyer, int nNegSeller)
        {
            try
            {
                OnQuote?.Invoke(new QuoteEvent(
                    Ticker: assetId.Ticker ?? string.Empty,
                    Last:   (decimal)sClose,
                    Bid: 0, Ask: 0,
                    Open:   (decimal)sOpen,
                    High:   (decimal)sHigh,
                    Low:    (decimal)sLow,
                    Volume: nQtd, Time: DateTime.Now
                ));
            }
            catch { }
        }

        private void OnHistoryCallback(
            TAssetID assetId, string date, uint tradeNumber,
            double price, double vol, int qtd,
            int buyAgent, int sellAgent, int tradeType)
            => SafeDllCallback(nameof(OnHistoryCallback), () =>
            {
                try
                {
                    string tkr = assetId.Ticker ?? string.Empty;
                    string dt = date ?? string.Empty;
                    OnNativeHistoryTrade?.Invoke(
                        tkr,
                        dt,
                        tradeNumber,
                        price,
                        vol,
                        qtd,
                        buyAgent,
                        sellAgent,
                        tradeType);
                    ProfitHistoryRelay.Raise(
                        tkr,
                        dt,
                        tradeNumber,
                        price,
                        vol,
                        qtd,
                        buyAgent,
                        sellAgent,
                        tradeType);
                }
                catch { /* best effort */ }
            });

        private void OnProgressCallback(TAssetID assetId, int nProgress)
            => SafeDllCallback(nameof(OnProgressCallback), () =>
            {
                try
                {
                    ProfitHistoryRelay.RaiseHistoryProgress(assetId.Ticker, nProgress);
                }
                catch { /* best effort */ }
            });

        private void OnTinyBookCallback(TAssetID assetId, double price, int qtd, int side)
            => SafeDllCallback(nameof(OnTinyBookCallback), () => { });

        #endregion

        #region IMarketDataProvider — Subscrições

        public void Subscribe(string ticker)
        {
            lock (_lock)
            {
                if (_subscribedTickers.Contains(ticker)) return;
                _subscribedTickers.Add(ticker);
                _logger.Log($"Subscribe agendado: {ticker}");
            }
            if (_initialized) InternalSubscribe(ticker);
        }

        public void Unsubscribe(string ticker)
        {
            lock (_lock)
            {
                if (!_subscribedTickers.Contains(ticker)) return;
                _subscribedTickers.Remove(ticker);
            }
            if (_initialized) InternalUnsubscribe(ticker);
        }

        private void InternalSubscribe(string ticker)
        {
            try
            {
                int r1 = SubscribeTicker(ticker, EXCHANGE_BMF);
                _logger.Log($"SubscribeTicker {ticker}/{EXCHANGE_BMF}: {r1}");
                if (r1 != 0)
                {
                    r1 = SubscribeTicker(ticker, EXCHANGE_BVMF);
                    _logger.Log($"SubscribeTicker {ticker}/{EXCHANGE_BVMF}: {r1}");
                }

                int r2 = SubscribeOfferBook(ticker, EXCHANGE_BMF);
                _logger.Log($"SubscribeOfferBook {ticker}/{EXCHANGE_BMF}: {r2}");
                if (r2 != 0)
                {
                    r2 = SubscribeOfferBook(ticker, EXCHANGE_BVMF);
                    _logger.Log($"SubscribeOfferBook {ticker}/{EXCHANGE_BVMF} (fallback): {r2}");
                }

                _currentAssetID = new TConnectorAssetIdentifier
                {
                    Version  = 0,
                    Ticker   = ticker,
                    Exchange = EXCHANGE_BMF,
                    FeedType = 0
                };
            }
            catch (Exception ex)
            {
                _logger.Log($"✗ InternalSubscribe {ticker}: {ex.Message}");
            }
        }

        private void InternalUnsubscribe(string ticker)
        {
            try
            {
                UnsubscribeTicker(ticker, EXCHANGE_BMF);
                UnsubscribeTicker(ticker, EXCHANGE_BVMF);
                UnsubscribeOfferBook(ticker, EXCHANGE_BMF);
                UnsubscribeOfferBook(ticker, EXCHANGE_BVMF);
                UnsubscribePriceDepth(_currentAssetID);
                _logger.Log($"Unsubscribe {ticker} OK");
            }
            catch (Exception ex)
            {
                _logger.Log($"✗ InternalUnsubscribe {ticker}: {ex.Message}");
            }
        }

        #endregion

        #region Utilitários

        private void SetStatus(ConnectionStatus status, string message)
        {
            Status = status;
            _logger.Log($"[Status] {status}: {message}");
            try
            {
                OnConnectionChanged?.Invoke(new ConnectionChangedEvent(status, message));
            }
            catch (Exception ex)
            {
                try { _logger.Log($"✗ OnConnectionChanged subscriber: {ex.Message}"); } catch { /* ignore */ }
            }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopProcessingThread();
            lock (_lock)
            {
                foreach (var ticker in _subscribedTickers.ToArray())
                    InternalUnsubscribe(ticker);
                _initialized      = false;
                _readyToSubscribe = false;
            }

            lock (_rawLogLock)
            {
                _rawBookLog?.WriteLine($"=== DISPOSE — {DateTime.Now:HH:mm:ss} — {_rawLogCount} eventos ===");
                _rawBookLog?.Close();
                _rawBookLog = null;
            }

            _logger?.Dispose();
            GC.SuppressFinalize(this);
        }

        ~ProfitDLLProvider() => Dispose();

        #endregion
    }
}