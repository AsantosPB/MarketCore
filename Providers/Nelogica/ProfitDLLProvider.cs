using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MarketCore.Contracts;
using MarketCore.Models;

namespace MarketCore.Providers.Nelogica
{
    public class ProfitDLLProvider : IMarketDataProvider
    {
        #region Constantes

        private const string DLL_PATH     = @"ProfitDLL64.dll";
        private const string EXCHANGE_BMF  = "F";
        private const string EXCHANGE_BVMF = "B";

        #endregion

        #region Structs e Delegates da Nelogica

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
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

        private delegate void TStateCallback(int nResult, int result);
        private delegate void TTradeCallback(
            TAssetID assetId, [MarshalAs(UnmanagedType.LPWStr)] string date,
            uint tradeNumber, double price, double vol,
            int qtd, int buyAgent, int sellAgent, int tradeType, int bIsEdit);
        private delegate void TNewDailyCallback(
            TAssetID assetId, [MarshalAs(UnmanagedType.LPWStr)] string date,
            double sOpen, double sHigh, double sLow, double sClose,
            double sVol, double sAjuste, double sMaxLimit, double sMinLimit,
            double sVolBuyer, double sVolSeller,
            int nQtd, int nNegocios, int nContratosOpen,
            int nQtdBuyer, int nQtdSeller, int nNegBuyer, int nNegSeller);
        private delegate void TPriceBookCallback(
            TAssetID assetId, int nAction, int nPosition,
            int side, int nQtd, int nCount, double sPrice,
            IntPtr pArraySell, IntPtr pArrayBuy);
        private delegate void TOfferBookCallback(
            TAssetID assetId, int nAction, int nPosition,
            int side, int nQtd, int nAgent, long nOfferID, double sPrice,
            int bHasPrice, int bHasQtd, int bHasDate, int bHasOfferID, int bHasAgent,
            [MarshalAs(UnmanagedType.LPWStr)] string date,
            IntPtr pArraySell, IntPtr pArrayBuy);
        private delegate void THistoryTradeCallback(
            TAssetID assetId, [MarshalAs(UnmanagedType.LPWStr)] string date,
            uint tradeNumber, double price, double vol,
            int qtd, int buyAgent, int sellAgent, int tradeType);
        private delegate void TProgressCallBack(TAssetID assetId, int nProgress);
        private delegate void TNewTinyBookCallBack(TAssetID assetId, double price, int qtd, int side);
        private delegate void TChangeCotation(
            TAssetID assetId, [MarshalAs(UnmanagedType.LPWStr)] string date,
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
            TStateCallback stateCallback,
            TTradeCallback newTradeCallback,
            TNewDailyCallback newDailyCallback,
            TPriceBookCallback priceBookCallback,
            TOfferBookCallback offerBookCallback,
            THistoryTradeCallback newHistoryCallback,
            TProgressCallBack progressCallBack,
            TNewTinyBookCallBack newTinyBookCallBack);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int SetChangeCotationCallback(TChangeCotation a_ChangeCotation);

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
            public readonly int    Side;
            public readonly double Price;
            public readonly int    Volume;
            public readonly int    Agent;
            public readonly long   OfferId;
            public RawBook(string t, int si, double p, int v, int a, long o)
            { Ticker=t; Side=si; Price=p; Volume=v; Agent=a; OfferId=o; }
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
        private readonly ConcurrentQueue<RawBook>  _bookQueue  = new();
        private readonly ConcurrentQueue<RawDepth> _depthQueue = new();

        // Cache de corretoras
        private readonly Dictionary<int, string> _brokerCache = new();

        // Thread de processamento
        private Thread? _processingThread;
        private volatile bool _processingRunning = false;

        private bool _disposed       = false;
        private bool _initialized    = false;
        private ProviderCredentials? _lastCredentials = null;

        // GC protection dos delegates
        private TStateCallback?               _stateCallback;
        private TTradeCallback?               _tradeCallback;
        private TNewDailyCallback?            _dailyCallback;
        private TPriceBookCallback?           _priceBookCallback;
        private TOfferBookCallback?           _offerBookCallback;
        private THistoryTradeCallback?        _historyCallback;
        private TProgressCallBack?            _progressCallback;
        private TNewTinyBookCallBack?         _tinyBookCallback;
        private TChangeCotation?              _cotationCallback;
        private TConnectorPriceDepthCallback? _priceDepthCb;

        private volatile bool _readyToSubscribe = false;
        private TConnectorAssetIdentifier _currentAssetID;

        #endregion

        #region IMarketDataProvider — Eventos

        public event Action<TradeEvent>?             OnTrade;
        public event Action<BookLevel>?              OnBook;
        public event Action<QuoteEvent>?             OnQuote;
        public event Action<ConnectionChangedEvent>? OnConnectionChanged;

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
        }

        #endregion

        #region Thread de Processamento

        private void StartProcessingThread()
        {
            _processingRunning = true;
            _processingThread = new Thread(ProcessingLoop)
            {
                IsBackground = true,
                Name = "ProfitDLL-Processing",
                Priority = ThreadPriority.AboveNormal
            };
            _processingThread.Start();
        }

        private void StopProcessingThread()
        {
            _processingRunning = false;
        }

        private void ProcessingLoop()
        {
            while (_processingRunning)
            {
                bool hadWork = false;

                // Processa trades
                while (_tradeQueue.TryDequeue(out var raw))
                {
                    hadWork = true;
                    try
                    {
                        var aggressor = raw.TradeType == 1 ? TradeAggressor.Buy
                                      : raw.TradeType == 2 ? TradeAggressor.Sell
                                      : TradeAggressor.Unknown;

                        int agentId = aggressor == TradeAggressor.Buy ? raw.BuyAgent : raw.SellAgent;

                        OnTrade?.Invoke(new TradeEvent(
                            Ticker:    raw.Ticker,
                            Price:     (decimal)raw.Price,
                            Volume:    raw.Qtd,
                            Broker:    GetBrokerNameSafe(agentId),
                            Aggressor: aggressor,
                            Time:      DateTime.Now
                        ));
                    }
                    catch (Exception ex) { _logger.Log($"Erro ProcessTrade: {ex.Message}"); }
                }

                // Processa book (OfferBook — para SpoofDetector e detectores)
                while (_bookQueue.TryDequeue(out var raw))
                {
                    hadWork = true;
                    try
                    {
                        string broker = raw.Agent > 0 ? GetBrokerNameSafe(raw.Agent) : string.Empty;

                        OnBook?.Invoke(new BookLevel(
                            Ticker:  raw.Ticker,
                            Side:    raw.Side == 0 ? BookSide.Bid : BookSide.Ask,
                            Price:   (decimal)raw.Price,
                            Volume:  raw.Volume,
                            Broker:  broker,
                            Time:    DateTime.Now,
                            OfferId: raw.OfferId
                        ));
                    }
                    catch (Exception ex) { _logger.Log($"Erro ProcessBook: {ex.Message}"); }
                }

                // Processa PriceDepth (book visual com níveis agregados por preço)
                while (_depthQueue.TryDequeue(out var depth))
                {
                    hadWork = true;
                    try { ProcessPriceDepth(depth.Side, depth.UpdateType); }
                    catch (Exception ex) { _logger.Log($"Erro ProcessDepth: {ex.Message}"); }
                }

                if (!hadWork) Thread.Sleep(1);
            }
        }

        private void ProcessPriceDepth(byte side, byte updateType)
        {
            const byte BUY         = 0;
            const byte SELL        = 1;
            const byte BOTH        = 254;
            const uint PG_THEORIC  = 1;

            byte[] sides = side == BOTH ? new[] { BUY, SELL } : new[] { side };

            foreach (var s in sides)
            {
                int count = GetPriceDepthSideCount(_currentAssetID, s);
                if (count <= 0) continue;

                for (int pos = 0; pos < count; pos++)
                {
                    var pg = new TConnectorPriceGroup { Version = 0 };
                    if (GetPriceGroup(_currentAssetID, s, pos, ref pg) != 0) continue;
                    if ((pg.PriceGroupFlags & PG_THEORIC) != 0) continue;
                    if (pg.Price <= 0 || pg.Price > 10_000_000) continue;

                    OnBook?.Invoke(new BookLevel(
                        Ticker:  _currentAssetID.Ticker ?? string.Empty,
                        Side:    s == BUY ? BookSide.Bid : BookSide.Ask,
                        Price:   (decimal)pg.Price,
                        Volume:  (int)pg.Quantity,
                        Broker:  string.Empty,
                        Time:    DateTime.Now,
                        OfferId: 0
                    ));
                }
            }
        }

        private string GetBrokerNameSafe(int agentId)
        {
            if (agentId <= 0) return string.Empty;
            if (_brokerCache.TryGetValue(agentId, out var cached)) return cached;

            try
            {
                var sb = new StringBuilder(128);
                int result = GetAgentName(128, agentId, sb, 1);
                string name = result == 0 && sb.Length > 0
                    ? sb.ToString().Trim()
                    : agentId.ToString();
                _brokerCache[agentId] = name;
                return name;
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

                List<string> pendentes;
                lock (_lock) pendentes = new List<string>(_subscribedTickers);
                foreach (var ticker in pendentes)
                    InternalSubscribe(ticker);
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
        }

        #endregion

        #region Callbacks da DLL — apenas enfileiram

        private void OnStateCallback(int nConnStateType, int result)
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
        {
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
            // Ignorado — usamos PriceDepth para o book visual
        }

        private void OnOfferBookCallback(
            TAssetID assetId, int nAction, int nPosition,
            int side, int nQtd, int nAgent, long nOfferID, double sPrice,
            int bHasPrice, int bHasQtd, int bHasDate, int bHasOfferID, int bHasAgent,
            string date, IntPtr pArraySell, IntPtr pArrayBuy)
        {
            if (sPrice <= 0 || sPrice > 10_000_000 ||
                double.IsNaN(sPrice) || double.IsInfinity(sPrice)) return;

            int volume = nQtd < 0 ? 0 : nQtd;
            _bookQueue.Enqueue(new RawBook(
                assetId.Ticker ?? string.Empty,
                side, sPrice, volume, nAgent, nOfferID));
        }

        private void OnPriceDepthCallback(
            TConnectorAssetIdentifier assetID, byte side, int position, byte updateType)
        {
            // Não usado — book visual via OnOfferBookCallback com OfferId
        }

        private void OnCotationCallback(
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
            int buyAgent, int sellAgent, int tradeType) { }

        private void OnProgressCallback(TAssetID assetId, int nProgress) { }

        private void OnTinyBookCallback(TAssetID assetId, double price, int qtd, int side)
        {
            // Ignorado — usamos PriceDepth
        }

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
                // Trades
                int r1 = SubscribeTicker(ticker, EXCHANGE_BMF);
                _logger.Log($"SubscribeTicker {ticker}/{EXCHANGE_BMF}: {r1}");
                if (r1 != 0)
                {
                    r1 = SubscribeTicker(ticker, EXCHANGE_BVMF);
                    _logger.Log($"SubscribeTicker {ticker}/{EXCHANGE_BVMF}: {r1}");
                }

                // OfferBook (para book visual com corretora por oferta individual)
                string exchange = r1 == 0 ? EXCHANGE_BMF : EXCHANGE_BVMF;
                int r2 = SubscribeOfferBook(ticker, exchange);
                _logger.Log($"SubscribeOfferBook {ticker}/{exchange}: {r2}");

                // Salva AssetID para PriceDepth (caso necessário futuramente)
                _currentAssetID = new TConnectorAssetIdentifier
                {
                    Version  = 0,
                    Ticker   = ticker,
                    Exchange = exchange,
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
            OnConnectionChanged?.Invoke(new ConnectionChangedEvent(status, message));
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
            _logger?.Dispose();
            GC.SuppressFinalize(this);
        }

        ~ProfitDLLProvider() => Dispose();

        #endregion
    }
}
