using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MarketCore.Contracts;
using MarketCore.Models;

namespace MarketCore.Providers.Nelogica
{
    public class ProfitDLLProvider : IMarketDataProvider
    {
        #region DLL Path

        private const string DLL_PATH     = @"ProfitDLL64.dll";
        private const string EXCHANGE_BMF  = "F";  // gc_bvBMF = 70 = 'F'
        private const string EXCHANGE_BVMF = "B";  // gc_bvBovespa = 66 = 'B'

        #endregion

        #region Structs / Delegates da Nelogica

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TAssetID
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string Ticker;
            [MarshalAs(UnmanagedType.LPWStr)] public string Bolsa;
            public int nFeedType;
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
        private static extern int SubscribeTicker(
            [MarshalAs(UnmanagedType.LPWStr)] string pwcTicker,
            [MarshalAs(UnmanagedType.LPWStr)] string pwcBolsa);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int UnsubscribeTicker(
            [MarshalAs(UnmanagedType.LPWStr)] string pwcTicker,
            [MarshalAs(UnmanagedType.LPWStr)] string pwcBolsa);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int SubscribePriceBook(
            [MarshalAs(UnmanagedType.LPWStr)] string pwcTicker,
            [MarshalAs(UnmanagedType.LPWStr)] string pwcBolsa);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int UnsubscribePriceBook(
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
        private static extern int GetAgentNameLength(int nAgentID, int nShortName);

        [DllImport(DLL_PATH, CallingConvention = CallingConvention.StdCall)]
        private static extern int GetAgentName(
            int nCount, int nAgentID,
            [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwcAgent,
            int nShortName);

        #endregion

        #region Tipos internos para a fila

        // Dados brutos enfileirados pelo callback (sem processar nomes)
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
            public RawBook(string t, int si, double p, int v, int a)
            { Ticker=t; Side=si; Price=p; Volume=v; Agent=a; }
        }

        #endregion

        #region Campos Privados

        private readonly ConnectionLogger _logger;
        private readonly object _lock = new object();
        private readonly List<string> _subscribedTickers = new();

        // Filas lock-free para desacoplar callbacks da DLL do processamento
        private readonly ConcurrentQueue<RawTrade> _tradeQueue = new();
        private readonly ConcurrentQueue<RawBook>  _bookQueue  = new();

        // Cache de corretoras — preenchido na thread de processamento, nunca nos callbacks
        private readonly Dictionary<int, string> _brokerCache = new();

        // Thread de processamento
        private Thread? _processingThread;
        private volatile bool _processingRunning = false;

        private bool _disposed       = false;
        private bool _initialized    = false;
        private ProviderCredentials? _lastCredentials = null;

        // GC protection dos delegates
        private TStateCallback?        _stateCallback;
        private TTradeCallback?        _tradeCallback;
        private TNewDailyCallback?     _dailyCallback;
        private TPriceBookCallback?    _priceBookCallback;
        private TOfferBookCallback?    _offerBookCallback;
        private THistoryTradeCallback? _historyCallback;
        private TProgressCallBack?     _progressCallback;
        private TNewTinyBookCallBack?  _tinyBookCallback;
        private TChangeCotation?       _cotationCallback;

        private volatile bool _readyToSubscribe = false;

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
                        string broker = GetBrokerNameSafe(agentId);

                        OnTrade?.Invoke(new TradeEvent(
                            Ticker:    raw.Ticker,
                            Price:     (decimal)raw.Price,
                            Volume:    raw.Qtd,
                            Broker:    broker,
                            Aggressor: aggressor,
                            Time:      DateTime.Now
                        ));
                    }
                    catch (Exception ex) { _logger.Log($"Erro ProcessTrade: {ex.Message}"); }
                }

                // Processa book
                while (_bookQueue.TryDequeue(out var raw))
                {
                    hadWork = true;
                    try
                    {
                        string broker = raw.Agent > 0 ? GetBrokerNameSafe(raw.Agent) : string.Empty;

                        OnBook?.Invoke(new BookLevel(
                            Ticker: raw.Ticker,
                            Side:   raw.Side == 0 ? BookSide.Bid : BookSide.Ask,
                            Price:  (decimal)raw.Price,
                            Volume: raw.Volume,
                            Broker: broker,
                            Time:   DateTime.Now
                        ));
                    }
                    catch (Exception ex) { _logger.Log($"Erro ProcessBook: {ex.Message}"); }
                }

                // Se não teve trabalho, espera um pouco
                if (!hadWork)
                    Thread.Sleep(1);
            }
        }

        // Chamado FORA dos callbacks da DLL — seguro para chamar GetAgentName
        private string GetBrokerNameSafe(int agentId)
        {
            if (agentId <= 0) return string.Empty;

            if (_brokerCache.TryGetValue(agentId, out var cached))
                return cached;

            try
            {
                // Usa buffer fixo grande — evita problemas de tamanho Unicode
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

                // Aguarda Market conectado (nConnStateType=2, result=4)
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

                // Inicia thread de processamento
                StartProcessingThread();

                _initialized = true;
                SetStatus(ConnectionStatus.Connected, "Conectado");
                _logger.Log("✓ CONEXÃO ESTABELECIDA!");

                // Subscreve tickers pendentes
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

        #region Callbacks da DLL — apenas enfileiram, nunca processam

        private void OnStateCallback(int nConnStateType, int result)
        {
            // ⚠️ NUNCA chamar funções da DLL aqui dentro!
            _logger.Log($"[StateCallback] nConnStateType={nConnStateType} result={result}");

            switch (nConnStateType)
            {
                case 0: // Login
                    switch (result)
                    {
                        case 0: _logger.Log("[Login] Conectado"); break;
                        case 1: SetStatus(ConnectionStatus.Error, "Login inválido"); break;
                        case 2: SetStatus(ConnectionStatus.Error, "Senha inválida"); break;
                        case 3: SetStatus(ConnectionStatus.Error, "Senha bloqueada"); break;
                        case 4: SetStatus(ConnectionStatus.Error, "Senha expirada"); break;
                    }
                    break;

                case 1: // Broker
                    _logger.Log($"[Broker] result={result}");
                    break;

                case 2: // Market
                    switch (result)
                    {
                        case 0:
                            _logger.Log("[Market] Desconectado — aguardando reconexão automática");
                            _readyToSubscribe = false;
                            if (_initialized)
                            {
                                _initialized = false;
                                // Não marca como ERROR — DLL reconecta automaticamente
                                SetStatus(ConnectionStatus.Connecting, "Reconectando...");
                            }
                            break;
                        case 4:
                            _logger.Log("[Market] CONECTADO — pronto para subscrições!");
                            _readyToSubscribe = true;
                            // Se já estava inicializado antes, reinscreve automaticamente
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

                case 3: // Licença
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
            // ⚠️ Apenas enfileira — NÃO processa aqui!
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
            // Ignorado — usamos apenas OfferBook (por oferta individual com corretora)
        }

        private void OnOfferBookCallback(
            TAssetID assetId, int nAction, int nPosition,
            int side, int nQtd, int nAgent, long nOfferID, double sPrice,
            int bHasPrice, int bHasQtd, int bHasDate, int bHasOfferID, int bHasAgent,
            string date, IntPtr pArraySell, IntPtr pArrayBuy)
        {
            // ⚠️ Apenas enfileira — NÃO processa aqui!
            if (sPrice <= 0 || sPrice > 10_000_000 ||
                double.IsNaN(sPrice) || double.IsInfinity(sPrice)) return;

            int volume = nQtd < 0 ? 0 : nQtd;

            _bookQueue.Enqueue(new RawBook(
                assetId.Ticker ?? string.Empty,
                side, sPrice, volume, nAgent));
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
                    Volume: 0,
                    Time:   DateTime.Now
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
                    Volume: nQtd,
                    Time:   DateTime.Now
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
            if (price <= 0 || price > 10_000_000 ||
                double.IsNaN(price) || double.IsInfinity(price)) return;

            int volume = qtd < 0 ? 0 : qtd;

            _bookQueue.Enqueue(new RawBook(
                assetId.Ticker ?? string.Empty,
                side, price, volume, 0));
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

            if (_initialized)
                InternalSubscribe(ticker);
        }

        public void Unsubscribe(string ticker)
        {
            lock (_lock)
            {
                if (!_subscribedTickers.Contains(ticker)) return;
                _subscribedTickers.Remove(ticker);
            }

            if (_initialized)
                InternalUnsubscribe(ticker);
        }

        private void InternalSubscribe(string ticker)
        {
            try
            {
                // Apenas Ticker e OfferBook — PriceBook ignorado (sem corretora por oferta)
                int r1 = SubscribeTicker(ticker, EXCHANGE_BMF);
                int r3 = SubscribeOfferBook(ticker, EXCHANGE_BMF);
                _logger.Log($"Subscribe {ticker}/{EXCHANGE_BMF} — Ticker:{r1} OfferBook:{r3}");

                if (r1 != 0)
                {
                    r1 = SubscribeTicker(ticker, EXCHANGE_BVMF);
                    r3 = SubscribeOfferBook(ticker, EXCHANGE_BVMF);
                    _logger.Log($"Subscribe {ticker}/{EXCHANGE_BVMF} — Ticker:{r1} OfferBook:{r3}");
                }
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
                UnsubscribeOfferBook(ticker, EXCHANGE_BMF);
                UnsubscribeTicker(ticker, EXCHANGE_BVMF);
                UnsubscribeOfferBook(ticker, EXCHANGE_BVMF);
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
