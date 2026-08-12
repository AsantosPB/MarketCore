using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
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

        // FullBook arrays (OfferBookCallbackV2): cada linha = 53 bytes após cabeçalho 8
        // (Manual + exemplo MarshalOfferBuffer: Preço+Int64 qty+Agent+OfferID+tamdata+payload).

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void TStateCallback(int nResult, int result);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate void TTradeCallback(
            TAssetID assetId,
            [MarshalAs(UnmanagedType.LPWStr)] string date,
            uint tradeNumber, double price, double vol,
            int qtd, int buyAgent, int sellAgent, int tradeType, int bIsEdit);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate void TNewDailyCallback(
            TAssetID assetId,
            [MarshalAs(UnmanagedType.LPWStr)] string date,
            double sOpen, double sHigh, double sLow, double sClose,
            double sVol, double sAjuste, double sMaxLimit, double sMinLimit,
            double sVolBuyer, double sVolSeller,
            int nQtd, int nNegocios, int nContratosOpen,
            int nQtdBuyer, int nQtdSeller, int nNegBuyer, int nNegSeller);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate void TPriceBookCallback(
            TAssetID assetId, int nAction, int nPosition,
            int side, int nQtd, int nCount, double sPrice,
            IntPtr pArraySell, IntPtr pArrayBuy);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate void TOfferBookCallbackV2(
            TAssetID assetId, int nAction, int nPosition,
            int side, long nQtd, int nAgent, long nOfferID, double sPrice,
            ushort bHasPrice, ushort bHasQtd, ushort bHasDate, ushort bHasOfferID, ushort bHasAgent,
            [MarshalAs(UnmanagedType.LPWStr)] string date,
            IntPtr pArraySell, IntPtr pArrayBuy);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate void THistoryTradeCallback(
            TAssetID assetId,
            [MarshalAs(UnmanagedType.LPWStr)] string date,
            uint tradeNumber, double price, double vol,
            int qtd, int buyAgent, int sellAgent, int tradeType);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate void TProgressCallBack(TAssetID assetId, int nProgress);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate void TNewTinyBookCallBack(TAssetID assetId, double price, int qtd, int side);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate void TChangeCotation(
            TAssetID assetId,
            [MarshalAs(UnmanagedType.LPWStr)] string date,
            uint tradeNumber, double sPrice);

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
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
            /// <summary>Instante UTC do passe (string <c>date</c> do NewTrade).</summary>
            public readonly DateTime? ExchangeUtc;
            public RawTrade(string t, double p, int q, int b, int s, int tt, DateTime? exchangeUtc = null)
            {
                Ticker = t;
                Price = p;
                Qtd = q;
                BuyAgent = b;
                SellAgent = s;
                TradeType = tt;
                ExchangeUtc = exchangeUtc;
            }
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
            public readonly bool   HasQuantityUpdate;
            public RawBook(string t, int ac, int pos, int si, double p, int v, int a, long o, DateTime? exch = null, bool hasQuantityUpdate = false)
            {
                Ticker = t;
                Action = ac;
                Position = pos;
                Side = si;
                Price = p;
                Volume = v;
                Agent = a;
                OfferId = o;
                ExchangeTime = exch;
                HasQuantityUpdate = hasQuantityUpdate;
            }
        }

        /// <summary>Tenta interpretar <paramref name="date"/> do OfferBookCallback (vários layouts já vistos em BMF).</summary>
        private static bool TryParseOfferBookDate(string? date, out DateTime utc)
        {
            utc = default;
            if (string.IsNullOrWhiteSpace(date)) return false;
            string s = date.Trim();

            // IMPORTANTE: a ordem aqui importa. A DLL da Nelogica manda datas em
            // formato brasileiro (DMY: "11/08/2026" = 11 de agosto). Se caírmos em
            // DateTime.TryParse(InvariantCulture) PRIMEIRO, ele interpreta como MDY
            // (padrão US) e "11/08/2026" vira 8 de novembro — 89 dias no futuro.
            // Bug real observado no dll_latency.log: tradeAge/bookAge apareciam
            // negativos em ~89 dias porque a data era parseada com o mês/dia trocados.
            // Solução: tentar formatos EXATOS primeiro (todos DMY), depois pt-BR,
            // e só como último recurso a InvariantCulture (que raramente veremos).
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

            foreach (var f in formats)
            {
                if (DateTime.TryParseExact(s, f, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeLocal, out dt))
                {
                    utc = DateTime.SpecifyKind(dt, DateTimeKind.Local).ToUniversalTime();
                    return true;
                }
            }

            // Fallbacks livres — tenta pt-BR PRIMEIRO (DMY), invariant só se pt-BR falhar.
            if (DateTime.TryParse(s, System.Globalization.CultureInfo.GetCultureInfo("pt-BR"),
                    System.Globalization.DateTimeStyles.AssumeLocal | System.Globalization.DateTimeStyles.AllowWhiteSpaces, out dt)
                || DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeLocal | System.Globalization.DateTimeStyles.AllowWhiteSpaces, out dt))
            {
                utc = DateTime.SpecifyKind(dt, DateTimeKind.Local).ToUniversalTime();
                return true;
            }

            return false;
        }

        private static bool TryParseOfferBookDateBytes(byte[] data, int start, int length, out DateTime utc)
        {
            utc = default;
            if (length <= 0 || start < 0 || start + length > data.Length)
                return false;

            var text = new System.Text.StringBuilder(length);
            for (int i = start; i < start + length; i++)
                text.Append((char)data[i]);

            return TryParseOfferBookDate(text.ToString(), out utc);
        }

        /// <summary>Fila única: delta incremental ou substituição completa (ordem preservada).</summary>
        private readonly struct BookWorkItem
        {
            public readonly bool IsFullRefresh;
            public readonly RawBook Delta;
            public readonly string? FullTicker;
            public readonly BookLevel[]? FullBids;
            public readonly BookLevel[]? FullAsks;
            public readonly byte[]? FullRawSell;
            public readonly byte[]? FullRawBuy;

            private BookWorkItem(
                bool full,
                RawBook d,
                string? ft,
                BookLevel[]? b,
                BookLevel[]? a,
                byte[]? rawSell,
                byte[]? rawBuy)
            {
                IsFullRefresh = full;
                Delta = d;
                FullTicker = ft;
                FullBids = b;
                FullAsks = a;
                FullRawSell = rawSell;
                FullRawBuy = rawBuy;
            }

            public static BookWorkItem FromDelta(RawBook d) => new(false, d, null, null, null, null, null);

            public static BookWorkItem FromFull(string ticker, BookLevel[] bids, BookLevel[] asks)
                => new(true, default, ticker, bids, asks, null, null);

            public static BookWorkItem FromFullRaw(string ticker, byte[]? rawSell, byte[]? rawBuy)
                => new(true, default, ticker, null, null, rawSell, rawBuy);
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
        private volatile string? _primaryBookTicker;

        // Filas lock-free (sem Count máximo imposto — crescem com o ritmo da DLL até a memória aguentar).
        private readonly ConcurrentQueue<RawTrade> _tradeQueue = new();
        private readonly ConcurrentQueue<BookWorkItem> _bookQueue  = new();
        private readonly ConcurrentQueue<RawDepth> _depthQueue = new();

        // Cache de corretoras (concurrent: threads separadas de livro e negócios).
        private readonly ConcurrentDictionary<int, string> _brokerCache = new();
        private readonly ConcurrentDictionary<long, string> _offerBrokerCache = new();
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
        /// <summary>Slot V1 do <c>DLLInitializeMarketLogin</c> recebe um no-op: o real fica em <c>SetOfferBookCallbackV2</c> para evitar callback duplicado.</summary>
        private TOfferBookCallbackV2?         _offerBookCallbackV1Stub;
        private THistoryTradeCallback?        _historyCallback;
        private TProgressCallBack?            _progressCallback;
        private TNewTinyBookCallBack?         _tinyBookCallback;
        private TChangeCotation?              _cotationCallback;
        private TConnectorPriceDepthCallback? _priceDepthCb;

        private volatile bool _readyToSubscribe = false;
        private int _offerBookSubscribeSeq;
        private int _bookEventsProcessed;
        private TConnectorAssetIdentifier _currentAssetID;

        // Log RAW do OfferBook
        private System.IO.StreamWriter? _rawBookLog;
        private readonly object _rawLogLock = new object();
        private int _rawLogCount = 0;
        private const int RAW_LOG_MAX = 5000;

        /// <summary>Diagnóstico: liga se existir o arquivo gatilho <c>%AppData%\MarketCore\offerbook_raw.on</c> ou se <c>MARKETCORE_OFFERBOOK_RAW=1</c>.</summary>
        private static readonly bool EnableRawOfferBookLog = IsRawOfferBookLogRequested();

        private static bool IsRawOfferBookLogRequested()
        {
            try
            {
                if (string.Equals(Environment.GetEnvironmentVariable("MARKETCORE_OFFERBOOK_RAW"), "1", StringComparison.Ordinal))
                    return true;

                string trigger = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MarketCore",
                    "offerbook_raw.on");
                return System.IO.File.Exists(trigger);
            }
            catch
            {
                return false;
            }
        }

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

            string raizMarketCore = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MarketCore");
            string triggerPath = System.IO.Path.Combine(raizMarketCore, "offerbook_raw.on");
            string logPath = System.IO.Path.Combine(raizMarketCore, "offerbook_raw.txt");
            _logger.Log($"[RAW LOG] Trigger={triggerPath} | exists={System.IO.File.Exists(triggerPath)} | EnableRawOfferBookLog={EnableRawOfferBookLog}");

            if (!EnableRawOfferBookLog)
                return;

            try
            {
                System.IO.Directory.CreateDirectory(raizMarketCore);
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

            // Monitor de latência: amostra a cada 1s idade dos callbacks + tamanho das filas.
            // Grava em %LocalAppData%\MarketCore\dll_latency.log — independente do PVV.
            DllLatencyMonitor.Start(() => (_tradeQueue.Count, _bookQueue.Count, _depthQueue.Count));
        }

        private void StopProcessingThread()
        {
            DllLatencyMonitor.Stop();
            _processingRunning = false;
            _bookProcessingThread?.Join(TimeSpan.FromSeconds(3));
            _tradeProcessingThread?.Join(TimeSpan.FromSeconds(3));
        }

        private void BookProcessingLoop()
        {
            // Drena até esvaziar por volta da fila (yield periódico) — evita backlog de deltas vs trades em pico.
            while (_processingRunning)
            {
                int bookSlice = 0;
                while (_bookQueue.TryDequeue(out var work))
                {
                    bookSlice++;
                    if ((bookSlice & 4095) == 0)
                        Thread.Sleep(0);

                    // Diagnostic de latência: contabiliza processed + idade do último processado.
                    Interlocked.Increment(ref DllLatencyMonitor.BooksProcessedTotal);
                    if (!work.IsFullRefresh && work.Delta.ExchangeTime.HasValue)
                        Interlocked.Exchange(ref DllLatencyMonitor.LastBookProcessedExchangeTicks, work.Delta.ExchangeTime.Value.ToLocalTime().Ticks);

                    try
                    {
                        if (Interlocked.Increment(ref _bookEventsProcessed) == 1)
                            _logger.Log($"[OfferBook] Primeiro evento processado: {work.FullTicker ?? work.Delta.Ticker}");

                        if (work.IsFullRefresh)
                        {
                            BookLevel[]? bids = null;
                            BookLevel[]? asks = null;
                            if (work.FullRawSell != null || work.FullRawBuy != null)
                            {
                                // ═══════════════════════════════════════════════════════════════
                                // ATENÇÃO: Na DLL Nelogica, os nomes são da perspectiva do TRADER:
                                //   pArrayBuy  (FullRawBuy)  = "onde posso COMPRAR" = ofertas de VENDA (ASK/side=1)
                                //   pArraySell (FullRawSell)  = "onde posso VENDER"  = ofertas de COMPRA (BID/side=0)
                                // ═══════════════════════════════════════════════════════════════
                                if (work.FullRawSell is { Length: >= 8 } rawSell)
                                {
                                    int q = BitConverter.ToInt32(rawSell, 0);
                                    var parsed = ParseOfferBookSnapshotToLevels(work.FullTicker!, rawSell, side: 0);
                                    if (q == 0 || parsed.Length > 0)
                                        bids = parsed;
                                }

                                if (work.FullRawBuy is { Length: >= 8 } rawBuy)
                                {
                                    int q = BitConverter.ToInt32(rawBuy, 0);
                                    var parsed = ParseOfferBookSnapshotToLevels(work.FullTicker!, rawBuy, side: 1);
                                    if (q == 0 || parsed.Length > 0)
                                        asks = parsed;
                                }
                            }
                            else if (work.FullBids != null || work.FullAsks != null)
                            {
                                bids = work.FullBids;
                                asks = work.FullAsks;
                            }
                            else
                            {
                                continue;
                            }

                            if (bids == null && asks == null)
                            {
                                if (_fullBookRouteLogCount < 10)
                                {
                                    _fullBookRouteLogCount++;
                                    _logger.Log($"[FullBook DIAG] SKIP: both null for {work.FullTicker}");
                                }
                                continue;
                            }

                            if (_fullBookRouteLogCount < 10)
                            {
                                _fullBookRouteLogCount++;
                                _logger.Log($"[FullBook DIAG] EMIT: {work.FullTicker} bids={bids?.Length ?? -1} asks={asks?.Length ?? -1}");
                            }

                            OnBookFullRefresh?.Invoke(new BookFullRefresh(
                                work.FullTicker!,
                                bids,
                                asks));
                            continue;
                        }

                        RawBook rawBook = work.Delta;

                        if (rawBook.Ticker == "__CLEAR__")
                            continue;

                        decimal price = 0;
                        if (rawBook.Action == 1)
                        {
                            if (rawBook.Price > 0 && !TryPriceToDecimal(rawBook.Price, out price))
                                continue;
                        }
                        else if (rawBook.Action == 2 || rawBook.Action == 3)
                        {
                            // delete: preço opcional conforme DLL; sem preço válido pode ser 0
                            if (rawBook.Price != 0 && !TryPriceToDecimal(rawBook.Price, out price))
                                continue;
                        }
                        else if (!TryPriceToDecimal(rawBook.Price, out price))
                        {
                            continue;
                        }

                        string broker = ResolveBookBrokerFromCache(rawBook.Agent, rawBook.OfferId, rawBook.Agent > 0);

                        OnBook?.Invoke(new BookLevel(
                            Ticker:   rawBook.Ticker,
                            Side:     rawBook.Side == 0 ? BookSide.Bid : BookSide.Ask,
                            Price:    price,
                            Volume:   rawBook.Volume,
                            Broker:   broker,
                            Time:     DateTime.UtcNow,
                            OfferId:  rawBook.OfferId,
                            Action:   rawBook.Action,
                            Position: rawBook.Position,
                            ExchangeTime: rawBook.ExchangeTime,
                            VolumeIsDelta: rawBook.Action == 1 && rawBook.HasQuantityUpdate,
                            AgentId: rawBook.Agent
                        ));
                        // Bridge do Pregão Viva Voz para BOOK é chamado no MarketEngine.HandleBook —
                        // lá temos acesso ao depth atual do book para computar o nível correto
                        // (a Nelogica manda nPosition contado do final da fila, então precisamos
                        // do count para inverter: nivel = depth - position).
                    }
                    catch (Exception ex) { _logger.Log($"Erro ProcessBook: {ex.Message}"); }
                }

                if (bookSlice > 0)
                {
                    DrainBrokerResolveQueue(32);
                    Thread.Sleep(0);
                }
                else
                {
                    DrainBrokerResolveQueue(128);
                    Thread.Sleep(1);
                }
            }
        }

        private void TradeProcessingLoop()
        {
            while (_processingRunning)
            {
                bool hadWork = false;

                int tradeSlice = 0;
                while (_tradeQueue.TryDequeue(out var raw))
                {
                    tradeSlice++;
                    hadWork = true;
                    if ((tradeSlice & 2047) == 0)
                        Thread.Sleep(0);

                    // Diagnostic de latência: contabiliza processed + idade do último processado.
                    Interlocked.Increment(ref DllLatencyMonitor.TradesProcessedTotal);
                    if (raw.ExchangeUtc.HasValue)
                        Interlocked.Exchange(ref DllLatencyMonitor.LastTradeProcessedExchangeTicks, raw.ExchangeUtc.Value.ToLocalTime().Ticks);

                    try
                    {
                        // Manual DLL (TNewTradeCallback): 1=cross 2=compra agressão 3=venda agressão … 32=desconhecido
                        var aggressor = raw.TradeType == 2 ? TradeAggressor.Buy
                                      : raw.TradeType == 3 ? TradeAggressor.Sell
                                      : TradeAggressor.Unknown;

                        int agentId = aggressor == TradeAggressor.Buy ? raw.BuyAgent : raw.SellAgent;

                        DateTime receivedUtc = DateTime.UtcNow;
                        DateTime timeLocal =
                            raw.ExchangeUtc.HasValue ? raw.ExchangeUtc.Value.ToLocalTime() : DateTime.Now;

                        OnTrade?.Invoke(new TradeEvent(
                            Ticker:    raw.Ticker ?? string.Empty,
                            Price:     (decimal)raw.Price,
                            Volume:    raw.Qtd,
                            Broker:    FormatBookBroker(agentId),
                            Aggressor: aggressor,
                            Time:      timeLocal,
                            ExchangeTimeUtc: raw.ExchangeUtc,
                            ReceivedUtc: receivedUtc));

                        // Pregão Viva Voz: encaminha o trade real para a Bridge (se motor ativo).
                        //
                        // Convenção oficial da Nelogica ProfitDLL, confirmada no exemplo Python
                        // publicado em https://ajuda.nelogica.com.br (artigo "Do tick ao dashboard"):
                        //   if trade_type == 2:  # agressao de compra   → usa BUY_AGENT
                        //   elif trade_type == 3:  # agressao de venda  → usa SELL_AGENT
                        //
                        // Portanto:
                        //   Nelogica raw.TradeType == 2  →  agressor COMPROU (tomou o ask)
                        //   Nelogica raw.TradeType == 3  →  agressor VENDEU (bateu no bid)
                        //
                        // Bridge espera: 1 = agressor comprou; 2 = agressor vendeu.
                        var pvvHook = PregaoVivaVozHook.OnTradeReceived;
                        if (pvvHook != null)
                        {
                            int pvvTradeType = raw.TradeType == 2 ? 1   // Nelogica 2 = compra-agr → PVV 1
                                             : raw.TradeType == 3 ? 2   // Nelogica 3 = venda-agr  → PVV 2
                                             : 0;
                            if (pvvTradeType != 0)
                            {
                                string buyName  = FormatBookBroker(raw.BuyAgent);
                                string sellName = FormatBookBroker(raw.SellAgent);
                                // callbackInfo já formatada, viaja pareada com o evento até o log.
                                // Se a DLL não enviou horário da bolsa (bHasDate=false), mostra "--:--:--.---".
                                string bolsa = raw.ExchangeUtc.HasValue
                                    ? raw.ExchangeUtc.Value.ToLocalTime().ToString("HH:mm:ss.fff")
                                    : "--:--:--.---";
                                string callbackInfo =
                                    $"TRADE bolsa={bolsa} ticker={raw.Ticker} buy={buyName} sell={sellName} qtd={raw.Qtd} tradeType={raw.TradeType}";
                                pvvHook(raw.Ticker ?? string.Empty, buyName, sellName, raw.Qtd, pvvTradeType, callbackInfo);
                            }
                        }
                    }
                    catch (Exception ex) { _logger.Log($"Erro ProcessTrade: {ex.Message}"); }
                }

                while (_depthQueue.TryDequeue(out _)) { }

                if (!hadWork)
                {
                    DrainBrokerResolveQueue(128);
                    if (_brokerResolveQueue.IsEmpty)
                        Thread.Sleep(1);
                }
                else
                    Thread.Sleep(0);
            }
        }

        public string ResolveDisplayBroker(BookLevel level)
        {
            if (!string.IsNullOrWhiteSpace(level.Broker) && !int.TryParse(level.Broker, out _))
                return level.Broker;

            if (level.AgentId > 0 && _brokerCache.TryGetValue(level.AgentId, out var cached))
            {
                string label = ShortBrokerLabel(cached);
                if (label.Length > 0)
                    return label;
            }

            if (level.OfferId > 0 && _offerBrokerCache.TryGetValue(level.OfferId, out var byOffer)
                && !string.IsNullOrWhiteSpace(byOffer))
            {
                return byOffer;
            }

            return level.Broker ?? string.Empty;
        }

        private void DrainBrokerResolveQueue(int maxAgents)
        {
            for (int i = 0; i < maxAgents && _brokerResolveQueue.TryDequeue(out int agentId); i++)
            {
                if (!_brokerCache.ContainsKey(agentId))
                    GetBrokerNameSafe(agentId);
            }
        }

        /// <summary>Corretoras cujo nome legal não se reduz corretamente ao token esperado pelo corte
        /// genérico de <see cref="ShortBrokerLabel"/> (primeiro espaço/traço/barra/ponto). Dois casos já
        /// confirmados: (1) iniciais com ponto logo no começo, ex. "J.P. MORGAN..." — o corte pega só a
        /// primeira letra ("J"); (2) o token esperado é só o PREFIXO da primeira palavra do nome legal, sem
        /// nenhum delimitador no meio, ex. "CITIGROUP..." ou "CITIBANK..." — o corte pega a palavra inteira
        /// ("CITIGROUP"), nunca "CITI". Os padrões abaixo são checados ANTES do corte genérico. Se outra
        /// corretora aparecer zerada pelo mesmo motivo, um padrão novo entra aqui (o log "[BrokerResolve]"
        /// em <see cref="GetBrokerNameSafe"/> mostra o nome legal exato pra calibrar o padrão certo).</summary>
        private static readonly (Regex Pattern, string Token)[] _brokerAliasPatterns =
        {
            // Sem "^" no início: cobre tanto "J.P. MORGAN S.A." quanto variações com prefixo
            // (ex.: "BANCO J.P. MORGAN S.A."), sem depender de saber o texto exato registrado na B3.
            (new Regex(@"\bJ\.?\s*P\.?\s*MORGAN", RegexOptions.Compiled), "JPM"),
            // "CITI" é só o prefixo de "CITIGROUP"/"CITIBANK" — sem delimitador no meio da palavra,
            // o corte genérico nunca isola "CITI" sozinho.
            (new Regex(@"\bCITI", RegexOptions.Compiled), "CITI"),
        };

        private static string ShortBrokerLabel(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;

            string trimmed = name.Trim();
            if (trimmed.Length == 0)
                return string.Empty;

            if (int.TryParse(trimmed, out _))
                return trimmed;

            string upper = trimmed.ToUpperInvariant();
            foreach (var (pattern, token) in _brokerAliasPatterns)
            {
                if (pattern.IsMatch(upper))
                    return token;
            }

            int splitAt = trimmed.IndexOfAny([' ', '-', '/', '.']);
            string token2 = splitAt > 0 ? trimmed[..splitAt] : trimmed;
            return token2.Trim().ToUpperInvariant();
        }

        private static bool TryPriceToDecimal(double price, out decimal value)
        {
            value = 0;
            if (price <= 0 || price > 10_000_000 || double.IsNaN(price) || double.IsInfinity(price))
                return false;

            try
            {
                decimal raw = (decimal)price;
                value = decimal.Round(raw, 10, MidpointRounding.ToEven);
                return true;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        private string FormatBookBroker(int agentId)
        {
            if (agentId <= 0)
                return string.Empty;

            if (_brokerCache.TryGetValue(agentId, out var cached))
            {
                string label = ShortBrokerLabel(cached);
                if (label.Length > 0)
                    return label;
            }

            _brokerResolveQueue.Enqueue(agentId);
            return agentId.ToString();
        }

        private string ResolveBookBrokerFromCache(int agentId, long offerId, bool hasAgent)
        {
            if (hasAgent && agentId > 0)
            {
                if (_brokerCache.TryGetValue(agentId, out var cached))
                {
                    string label = ShortBrokerLabel(cached);
                    if (offerId > 0 && label.Length > 0)
                        _offerBrokerCache[offerId] = label;
                    return label;
                }

                _brokerResolveQueue.Enqueue(agentId);
                return agentId.ToString();
            }

            if (offerId > 0 && _offerBrokerCache.TryGetValue(offerId, out var cachedByOffer))
                return cachedByOffer ?? string.Empty;

            return string.Empty;
        }

        /// <summary>Resolve o token curto de uma corretora (ex.: "XP", "BTG") a partir do código numérico
        /// do agente, consultando a ProfitDLL de forma síncrona se ainda não estiver em cache. Diferente de
        /// <see cref="FormatBookBroker"/> (que enfileira e devolve o código na primeira chamada, pensado para
        /// o refresh contínuo do book), este método bloqueia até resolver — usado pelo backfill histórico da
        /// Leitura de Fluxo, onde poucas dezenas de corretoras distintas se repetem em milhares de negócios,
        /// então o custo da chamada à DLL só é pago uma vez por corretora.</summary>
        public string ResolveBrokerShortName(int agentId)
        {
            if (agentId <= 0) return string.Empty;
            return ShortBrokerLabel(GetBrokerNameSafe(agentId));
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
                    // Log único por corretora nova (não por negócio) — permite conferir no log qual nome
                    // legal completo a ProfitDLL devolveu para cada agentId e qual token curto o
                    // ShortBrokerLabel gerou a partir dele. Essencial para diagnosticar corretoras cujo nome
                    // legal não se reduz do jeito esperado (ex.: "J.P. MORGAN..." pode virar só "J" se o nome
                    // tiver um ponto logo após a primeira letra, nunca batendo com o token "JPM" da Leitura de Fluxo).
                    _logger.Log($"[BrokerResolve] agentId={agentId} rawName=\"{name}\" shortToken=\"{ShortBrokerLabel(name)}\"");
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
            await ProfitMarketInit.DllBootstrapGate.WaitAsync().ConfigureAwait(false);
            try
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

                _lastCredentials = credentials;

                if (ProfitMarketInit.IsDllInitializedInProcess && !_initialized)
                {
                    await ReattachToExistingDllSessionAsync().ConfigureAwait(false);
                    return;
                }

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
                    _offerBookCallbackV1Stub = (TAssetID _, int _, int _, int _, long _, int _, long _, double _,
                                                ushort _, ushort _, ushort _, ushort _, ushort _,
                                                string _, IntPtr _, IntPtr _) => { };
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
                        _offerBookCallbackV1Stub,
                        _historyCallback,
                        _progressCallback,
                        _tinyBookCallback);

                    _logger.Log($"DLLInitializeMarketLogin retornou: {result}");

                    if (result != 0)
                    {
                        SetStatus(ConnectionStatus.Error, $"Erro ao inicializar: código {result}");
                        return;
                    }

                    ProfitMarketInit.MarkDllInitializedFromProvider();

                    SetChangeCotationCallback(_cotationCallback);
                    SetPriceDepthCallback(_priceDepthCb);

                    int r2 = SetOfferBookCallbackV2(_offerBookCallback);
                    _logger.Log($"SetOfferBookCallbackV2 retornou: {r2}");

                    int waited = 0;
                    while (!_readyToSubscribe && waited < 20000)
                    {
                        await Task.Delay(100).ConfigureAwait(false);
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
            finally
            {
                ProfitMarketInit.DllBootstrapGate.Release();
            }
        }

        private async Task ReattachToExistingDllSessionAsync()
        {
            _logger.Log("ProfitDLL já ativa no processo — reanexando callbacks sem novo login");

            try
            {
                _stateCallback     = OnStateCallback;
                _tradeCallback     = OnTradeCallback;
                _dailyCallback     = OnDailyCallback;
                _priceBookCallback = OnPriceBookCallback;
                _offerBookCallback = OnOfferBookCallback;
                _offerBookCallbackV1Stub ??= (TAssetID _, int _, int _, int _, long _, int _, long _, double _,
                                              ushort _, ushort _, ushort _, ushort _, ushort _,
                                              string _, IntPtr _, IntPtr _) => { };
                _historyCallback   = OnHistoryCallback;
                _progressCallback  = OnProgressCallback;
                _tinyBookCallback  = OnTinyBookCallback;
                _cotationCallback  = OnCotationCallback;
                _priceDepthCb      = OnPriceDepthCallback;

                SetChangeCotationCallback(_cotationCallback);
                SetOfferBookCallbackV2(_offerBookCallback);
            }
            catch (Exception ex)
            {
                _logger.Log($"Reattach callbacks: {ex.Message}");
            }

            if (!_readyToSubscribe)
            {
                int waited = 0;
                while (!_readyToSubscribe && waited < 5000)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                    waited += 100;
                }
            }

            if (!_readyToSubscribe)
                _readyToSubscribe = true;

            StartProcessingThread();
            _initialized = true;
            SetStatus(ConnectionStatus.Connected, "Conectado");

            List<string> pending;
            lock (_lock)
                pending = new List<string>(_subscribedTickers);

            foreach (string ticker in pending)
                InternalSubscribe(ticker);
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
            DateTime? exUtc = null;
            if (!string.IsNullOrWhiteSpace(date) && TryParseOfferBookDate(date, out DateTime parsedEx))
                exUtc = parsedEx;
            _tradeQueue.Enqueue(new RawTrade(
                assetId.Ticker ?? string.Empty,
                price, qtd, buyAgent, sellAgent, tradeType, exUtc));

            // Diagnostic de latência: idade do último trade recebido (bolsa vs now).
            Interlocked.Increment(ref DllLatencyMonitor.TradesReceivedTotal);
            if (exUtc.HasValue)
                Interlocked.Exchange(ref DllLatencyMonitor.LastTradeExchangeTicks, exUtc.Value.ToLocalTime().Ticks);
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
            string? ticker = assetId.Ticker;
            if (string.IsNullOrWhiteSpace(ticker))
                ticker = Volatile.Read(ref _primaryBookTicker);
            if (string.IsNullOrWhiteSpace(ticker))
                return;

            if (EnableRawOfferBookLog && _rawLogCount < RAW_LOG_MAX)
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


            // nAction=4 (atFullBook): cópia mínima dos arrays no callback; parse na thread de livro.
            if (nAction == 4)
            {
                _bookQueue.Enqueue(BookWorkItem.FromFullRaw(
                    ticker,
                    SnapshotOfferBookArray(pArraySell),
                    SnapshotOfferBookArray(pArrayBuy)));
                return;
            }

            // nAction 2–3 — espelhar manual/exemplo Nelogica (remoções position-based).
            if (nAction == 2 || nAction == 3)
            {
                if (nPosition < 0)
                    return;

                int agentDelete = bHasAgent != 0 ? nAgent : 0;
                long offerDelete = bHasOfferID != 0 ? nOfferID : 0;
                DateTime? exchangeDelete = null;
                if (bHasDate != 0 && TryParseOfferBookDate(date, out DateTime parsedExDel))
                    exchangeDelete = parsedExDel;

                double priceDelete = 0;
                if (bHasPrice != 0
                    && (sPrice <= 0 || sPrice > 10_000_000 ||
                        double.IsNaN(sPrice) || double.IsInfinity(sPrice)))
                {
                    return;
                }

                if (bHasPrice != 0)
                    priceDelete = sPrice;

                _bookQueue.Enqueue(BookWorkItem.FromDelta(
                    new RawBook(
                        ticker,
                        nAction,
                        nPosition,
                        side,
                        priceDelete,
                        0,
                        agentDelete,
                        offerDelete,
                        exchangeDelete,
                        hasQuantityUpdate: false)));

                Interlocked.Increment(ref DllLatencyMonitor.BooksReceivedTotal);
                if (exchangeDelete.HasValue)
                    Interlocked.Exchange(ref DllLatencyMonitor.LastBookExchangeTicks, exchangeDelete.Value.ToLocalTime().Ticks);
                return;
            }

            // nAction=0 (atAdd), 1 (atEdit)
            int volume = 0;
            if (nAction == 0)
            {
                if (bHasQtd == 0 || nQtd <= 0)
                    return;
                volume = nQtd > int.MaxValue ? int.MaxValue : (int)nQtd;
            }
            else if (nAction == 1 && bHasQtd != 0)
            {
                if (nQtd == 0)
                    return;
                volume = nQtd > int.MaxValue ? int.MaxValue : (int)nQtd;
            }

            if (nAction == 0)
            {
                if (bHasPrice == 0 || sPrice <= 0 || sPrice > 10_000_000 ||
                    double.IsNaN(sPrice) || double.IsInfinity(sPrice)) return;
            }
            else if (nAction == 1)
            {
                if (bHasPrice == 0 && bHasQtd == 0)
                    return;
                if (bHasPrice != 0 && (sPrice <= 0 || sPrice > 10_000_000 ||
                    double.IsNaN(sPrice) || double.IsInfinity(sPrice)))
                    return;
            }

            // IMPORTANTE: nPosition é contado do FINAL da lista (manual Nelogica)
            // índice_real = size - nPosition - 1
            // O BookState lida com isso na lógica de inserção/deleção
            int agent = bHasAgent != 0 ? nAgent : 0;
            long offerId = bHasOfferID != 0 ? nOfferID : 0;
            DateTime? exchangeTime = null;
            if (bHasDate != 0 && TryParseOfferBookDate(date, out DateTime parsedExchange))
                exchangeTime = parsedExchange;

            _bookQueue.Enqueue(BookWorkItem.FromDelta(
                new RawBook(
                    ticker,
                    nAction,
                    nPosition,
                    side,
                    sPrice,
                    volume,
                    agent,
                    offerId,
                    exchangeTime,
                    hasQuantityUpdate: nAction == 1 && bHasQtd != 0)));

            Interlocked.Increment(ref DllLatencyMonitor.BooksReceivedTotal);
            if (exchangeTime.HasValue)
                Interlocked.Exchange(ref DllLatencyMonitor.LastBookExchangeTicks, exchangeTime.Value.ToLocalTime().Ticks);
        }

        /// <summary>Máximo de linhas lidas de um snapshot <c>atFullBook</c> (array da DLL), não é limite da <c>_bookQueue</c>.</summary>
        private const int OfferBookMaxEntries = 10_000;
        private const int OfferBookMaxDateBytes = 256;
        private const int OfferBookMaxSnapshotBytes = 4 * 1024 * 1024;
        /// <summary>Largura fixa de cada entrada no snapshot <see cref="TOfferBookCallbackV2"/> (<c>atFullBook</c>) — igual ao exemplo <c>MarshalOfferBuffer</c>.</summary>
        private const int OfferBookFullRowStrideV2 = 53;

        /// <summary>Copia o array TOfferBook no callback nativo; o parse pesado roda na thread de livro.</summary>
        private static byte[]? SnapshotOfferBookArray(IntPtr arrayPtr)
        {
            if (arrayPtr == IntPtr.Zero)
                return null;

            try
            {
                int Q = Marshal.ReadInt32(arrayPtr, 0);
                if (Q <= 0)
                {
                    var emptyHeader = new byte[8];
                    BitConverter.TryWriteBytes(emptyHeader.AsSpan(0, 4), 0);
                    return emptyHeader;
                }

                if (Q > OfferBookMaxEntries)
                    Q = OfferBookMaxEntries;

                int offset = 8;
                int parsed = 0;
                for (int i = 0; i < Q; i++)
                {
                    int next = offset + OfferBookFullRowStrideV2;
                    if (next > OfferBookMaxSnapshotBytes)
                        break;

                    offset = next;
                    parsed++;
                }

                if (parsed <= 0 || offset <= 8)
                    return null;

                var buffer = new byte[offset];
                Marshal.Copy(arrayPtr, buffer, 0, offset);
                if (parsed < Q)
                    BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), parsed);

                return buffer;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Converte um lado do snapshot <c>atFullBook</c> (<see cref="TOfferBookCallbackV2"/> stride 53 bytes/linha).</summary>
        private BookLevel[] ParseOfferBookSnapshotToLevels(string ticker, byte[]? snapshot, int side)
        {
            if (snapshot == null || snapshot.Length < 8)
                return Array.Empty<BookLevel>();

            try
            {
                int Q = BitConverter.ToInt32(snapshot, 0);

                if (EnableRawOfferBookLog)
                {
                    lock (_rawLogLock)
                    {
                        if (_rawLogCount < RAW_LOG_MAX)
                            _rawBookLog?.WriteLine($"[FullBook] ticker={ticker} side={side} Q={Q}");
                    }
                }

                if (Q <= 0 || Q > 10_000)
                    return Array.Empty<BookLevel>();

                int headerCount = Math.Min(Q, 10_000);
                var list = new List<BookLevel>(headerCount);
                int offset = 8;
                BookSide bookSide = side == 0 ? BookSide.Bid : BookSide.Ask;

                for (int i = 0; i < headerCount; i++)
                {
                    try
                    {
                        if (offset + OfferBookFullRowStrideV2 > snapshot.Length)
                            break;

                        int rowBase = offset;
                        double price = BitConverter.ToDouble(snapshot, rowBase);
                        long qtd64 = BitConverter.ToInt64(snapshot, rowBase + 8);
                        int agent = BitConverter.ToInt32(snapshot, rowBase + 16);
                        long offerId = BitConverter.ToInt64(snapshot, rowBase + 20);
                        ushort tamData = BitConverter.ToUInt16(snapshot, rowBase + 28);
                        int tailStart = rowBase + 30;
                        int tailMax = OfferBookFullRowStrideV2 - 30;
                        int parseLen = tamData > 0
                            ? Math.Min(Math.Min(OfferBookMaxDateBytes, (int)tamData), tailMax)
                            : tailMax;

                        DateTime? exchangeTime = null;
                        if (parseLen > 0
                            && tailStart + parseLen <= snapshot.Length
                            && TryParseOfferBookDateBytes(snapshot, tailStart, parseLen, out DateTime parsedExchange))
                        {
                            exchangeTime = parsedExchange;
                        }

                        offset += OfferBookFullRowStrideV2;

                        if (!TryPriceToDecimal(price, out decimal priceDecimal))
                            continue;
                        int qtd = qtd64 <= 0 ? 0
                            : qtd64 > int.MaxValue ? int.MaxValue
                            : (int)qtd64;
                        if (qtd <= 0) continue;

                        string broker = FormatBookBroker(agent);
                        if (offerId > 0 && broker.Length > 0)
                            _offerBrokerCache[offerId] = broker;

                        list.Add(new BookLevel(
                            Ticker: ticker,
                            Side: bookSide,
                            Price: priceDecimal,
                            Volume: qtd,
                            Broker: broker,
                            Time: DateTime.Now,
                            OfferId: offerId,
                            Action: 5,
                            Position: i,
                            ExchangeTime: exchangeTime,
                            AgentId: agent));
                    }
                    catch { break; }
                }

                if (_fullBookParseLogCount < 20)
                {
                    _fullBookParseLogCount++;
                    _logger.Log($"[FullBook DIAG] side={side} Q={Q} parsed={list.Count} snapshotLen={snapshot.Length}" +
                        (list.Count > 0 ? $" firstPrice={list[0].Price} lastPrice={list[list.Count-1].Price}" : ""));
                }

                return list.ToArray();
            }
            catch (Exception ex)
            {
                _logger.Log($"[FullBook DIAG] EXCEPTION side={side}: {ex.Message}");
                return Array.Empty<BookLevel>();
            }
        }

        private int _fullBookParseLogCount;
        private int _fullBookRouteLogCount;

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
                string tkr = assetId.Ticker ?? string.Empty;
                string dt = date ?? string.Empty;

                // Tratamentos independentes: um subscritor com erro não pode "engolir"
                // a notificação dos restantes (já vimos isso causar perda de negócios
                // quando o handler legacy lançava antes do Relay ser chamado).
                try
                {
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
                }
                catch { /* legacy event — best effort */ }

                try
                {
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
                catch { /* relay — best effort */ }
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
                _primaryBookTicker = ticker;
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
                if (string.Equals(_primaryBookTicker, ticker, StringComparison.OrdinalIgnoreCase))
                    _primaryBookTicker = _subscribedTickers.Count > 0 ? _subscribedTickers[0] : null;
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

                _currentAssetID = new TConnectorAssetIdentifier
                {
                    Version  = 0,
                    Ticker   = ticker,
                    Exchange = EXCHANGE_BMF,
                    FeedType = 0
                };

                int subscribeSeq = Interlocked.Increment(ref _offerBookSubscribeSeq);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                        if (!_processingRunning || !_initialized || _disposed)
                            return;
                        if (subscribeSeq != Volatile.Read(ref _offerBookSubscribeSeq))
                            return;

                        bool stillSubscribed;
                        lock (_lock)
                            stillSubscribed = _subscribedTickers.Contains(ticker);
                        if (!stillSubscribed)
                            return;

                        _logger.Log($"SubscribeOfferBook a iniciar: {ticker}/{EXCHANGE_BMF}");
                        int r2 = SubscribeOfferBook(ticker, EXCHANGE_BMF);
                        _logger.Log($"SubscribeOfferBook {ticker}/{EXCHANGE_BMF}: {r2}");
                        if (r2 != 0)
                        {
                            r2 = SubscribeOfferBook(ticker, EXCHANGE_BVMF);
                            _logger.Log($"SubscribeOfferBook {ticker}/{EXCHANGE_BVMF} (fallback): {r2}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Log($"✗ InternalSubscribe offer book {ticker}: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.Log($"✗ InternalSubscribe {ticker}: {ex.Message}");
            }
        }

        private void InternalUnsubscribe(string ticker)
        {
            Interlocked.Increment(ref _offerBookSubscribeSeq);
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