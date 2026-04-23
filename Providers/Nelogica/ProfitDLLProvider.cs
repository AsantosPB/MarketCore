using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using MarketCore.Contracts;
using MarketCore.Models;

namespace MarketCore.Providers.Nelogica;

/// <summary>
/// Provider concreto para a ProfitDLL64.dll da Nelogica.
/// É a ÚNICA classe do sistema que conhece a DLL.
/// Tudo o que sai daqui são modelos comuns (TradeEvent, BookLevel, etc).
/// </summary>
public sealed class ProfitDLLProvider : IMarketDataProvider
{
    public string ProviderName => "Nelogica ProfitDLL";
    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;
    public IReadOnlyList<string> SubscribedTickers => _tickers.AsReadOnly();

    public event Action<TradeEvent>?             OnTrade;
    public event Action<BookLevel>?              OnBook;
    public event Action<QuoteEvent>?             OnQuote;
    public event Action<ConnectionChangedEvent>? OnConnectionChanged;

    // Fila de alta performance — callbacks chegam em thread da DLL,
    // processamos em thread separada para não bloquear a DLL
    private readonly ConcurrentQueue<Action> _eventQueue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _tickers = new();
    private Thread? _processorThread;

    // Mantemos referências dos delegates para o GC não coletar
    // enquanto a DLL ainda está usando — bug clássico com P/Invoke
    private ProfitDLL.TNewTradeCallback?       _tradeCb;
    private ProfitDLL.TNewBookCallback?        _bookCb;
    private ProfitDLL.TChangeCotationCallback? _quoteCb;
    private ProfitDLL.TStateCallback?          _stateCb;

    // ----------------------------------------------------------------
    // CONEXÃO
    // ----------------------------------------------------------------

    public async Task ConnectAsync(ProviderCredentials credentials)
    {
        SetStatus(ConnectionStatus.Connecting, "Inicializando DLL...");

        // Registra callbacks ANTES de inicializar — ordem importa
        RegisterCallbacks();

        // Inicia thread de processamento da fila
        StartProcessor();

        int result = credentials.RoutingEnabled
            ? ProfitDLL.DLLInitializeLogin(
                credentials.ActivationCode,
                credentials.Username,
                credentials.Password,
                _stateCb!, null!, null!, null!, null!, null!, null!)
            : ProfitDLL.DLLInitializeMarketLogin(
                credentials.ActivationCode,
                credentials.Username,
                credentials.Password,
                _stateCb!, null!, null!, null!);

        if (result != 0)
        {
            SetStatus(ConnectionStatus.Error, $"Erro ao inicializar DLL: código {result}");
            throw new InvalidOperationException($"ProfitDLL retornou erro {result}");
        }

        // Aguarda callback de estado confirmar conexão (timeout 30s)
        await WaitForConnectionAsync(TimeSpan.FromSeconds(30));
    }

    public async Task DisconnectAsync()
    {
        ProfitDLL.DLLFinalize();
        _cts.Cancel();
        SetStatus(ConnectionStatus.Disconnected, "Desconectado");
        await Task.CompletedTask;
    }

    // ----------------------------------------------------------------
    // ASSINATURAS
    // ----------------------------------------------------------------

    public void Subscribe(string ticker)
    {
        if (_tickers.Contains(ticker)) return;
        _tickers.Add(ticker);
        ProfitDLL.SubscribeTicker(ticker);
    }

    public void Unsubscribe(string ticker)
    {
        _tickers.Remove(ticker);
        ProfitDLL.UnsubscribeTicker(ticker);
    }

    // ----------------------------------------------------------------
    // CALLBACKS DA DLL → converte para modelos comuns → enfileira
    // ----------------------------------------------------------------

    private void RegisterCallbacks()
    {
        // Trade a trade
        _tradeCb = (ticker, price, volume, buyBroker, sellBroker, aggressor, time) =>
        {
            var evt = new TradeEvent(
                Ticker:     ticker,
                Price:      (decimal)price,
                Volume:     volume,
                Broker:     aggressor == 0 ? buyBroker : sellBroker,
                Aggressor:  aggressor == 0 ? TradeAggressor.Buy : TradeAggressor.Sell,
                Time:       DateTime.Now
            );
            _eventQueue.Enqueue(() => OnTrade?.Invoke(evt));
        };

        // Book de ofertas
        _bookCb = (ticker, side, position, price, volume, broker, time) =>
        {
            var evt = new BookLevel(
                Ticker:  ticker,
                Side:    side == 0 ? BookSide.Bid : BookSide.Ask,
                Price:   (decimal)price,
                Volume:  volume,
                Broker:  broker,
                Time:    DateTime.Now
            );
            _eventQueue.Enqueue(() => OnBook?.Invoke(evt));
        };

        // Cotação
        _quoteCb = (ticker, last, bid, ask, open, high, low, volume, time) =>
        {
            var evt = new QuoteEvent(
                Ticker: ticker,
                Last:   (decimal)last,
                Bid:    (decimal)bid,
                Ask:    (decimal)ask,
                Open:   (decimal)open,
                High:   (decimal)high,
                Low:    (decimal)low,
                Volume: volume,
                Time:   DateTime.Now
            );
            _eventQueue.Enqueue(() => OnQuote?.Invoke(evt));
        };

        // Estado da conexão
        _stateCb = (state, message) =>
        {
            _eventQueue.Enqueue(() =>
            {
                var status = state switch
                {
                    2 => ConnectionStatus.Connected,
                    3 => ConnectionStatus.Error,
                    _ => ConnectionStatus.Connecting
                };
                SetStatus(status, message ?? string.Empty);
            });
        };

        // Registra callbacks que precisam ser definidos manualmente
        ProfitDLL.SetChangeCotationCallback(_quoteCb);
    }

    // ----------------------------------------------------------------
    // PROCESSADOR DA FILA — thread separada, não bloqueia a DLL
    // ----------------------------------------------------------------

    private void StartProcessor()
    {
        _processorThread = new Thread(() =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                while (_eventQueue.TryDequeue(out var action))
                {
                    try { action(); }
                    catch (Exception ex)
                    {
                        // Log sem travar o processamento
                        Console.WriteLine($"[ProfitDLLProvider] Erro ao processar evento: {ex.Message}");
                    }
                }
                Thread.Sleep(1); // evita busy-wait excessivo
            }
        })
        {
            IsBackground = true,
            Name = "ProfitDLL-EventProcessor",
            Priority = ThreadPriority.AboveNormal
        };
        _processorThread.Start();
    }

    private async Task WaitForConnectionAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Status == ConnectionStatus.Connected) return;
            if (Status == ConnectionStatus.Error) throw new InvalidOperationException("Conexão falhou");
            await Task.Delay(100);
        }
        throw new TimeoutException("Timeout aguardando conexão com a Nelogica");
    }

    private void SetStatus(ConnectionStatus status, string message)
    {
        Status = status;
        OnConnectionChanged?.Invoke(new ConnectionChangedEvent(status, message));
    }

    public void Dispose()
    {
        DisconnectAsync().GetAwaiter().GetResult();
        _cts.Dispose();
    }
}
