using System.Collections.Concurrent;
using MarketCore.Contracts;
using MarketCore.Models;

namespace MarketCore.Providers.Simulator;

public sealed class SimulatorProvider : IMarketDataProvider
{
    public string ProviderName => "Simulator";
    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Disconnected;
    public IReadOnlyList<string> SubscribedTickers => _tickers.AsReadOnly();

    public event Action<TradeEvent>?             OnTrade;
    public event Action<BookLevel>?              OnBook;
    public event Action<QuoteEvent>?             OnQuote;
    public event Action<ConnectionChangedEvent>? OnConnectionChanged;

    private readonly List<string> _tickers = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Random _rng = new();
    private Task? _simTask;

    private readonly ConcurrentDictionary<string, TickerState> _states = new();

    // Fases de pressão
    private enum FasePressao { Equilibrado, DominanciaCompra, DominanciaVenda }
    private FasePressao _faseAtual = FasePressao.Equilibrado;
    private int _faseCiclosRestantes = 0;
    private readonly DispatcherlessTimer _faseTimer;

    public SimulatorProvider()
    {
        _faseTimer = new DispatcherlessTimer(MudarFase, TimeSpan.FromSeconds(5));
    }

    private void MudarFase()
    {
        var rnd = _rng.NextDouble();
        if (rnd < 0.33)
            _faseAtual = FasePressao.Equilibrado;
        else if (rnd < 0.66)
            _faseAtual = FasePressao.DominanciaCompra;
        else
            _faseAtual = FasePressao.DominanciaVenda;
    }

    public async Task ConnectAsync(ProviderCredentials credentials)
    {
        Status = ConnectionStatus.Connecting;
        OnConnectionChanged?.Invoke(new ConnectionChangedEvent(Status, "Simulador iniciando..."));
        await Task.Delay(300);
        Status = ConnectionStatus.Connected;
        OnConnectionChanged?.Invoke(new ConnectionChangedEvent(Status, "Simulador conectado"));
        _faseTimer.Start();
        _simTask = Task.Run(() => SimulationLoop(_cts.Token));
    }

    public async Task DisconnectAsync()
    {
        _faseTimer.Stop();
        _cts.Cancel();
        if (_simTask != null) await _simTask.WaitAsync(TimeSpan.FromSeconds(2));
        Status = ConnectionStatus.Disconnected;
        OnConnectionChanged?.Invoke(new ConnectionChangedEvent(Status, "Simulador desconectado"));
    }

    public void Subscribe(string ticker)
    {
        if (_tickers.Contains(ticker)) return;
        _tickers.Add(ticker);
        _states[ticker] = new TickerState(ticker.StartsWith("WIN") ? 128450m : 125000m);
    }

    public void Unsubscribe(string ticker)
    {
        _tickers.Remove(ticker);
        _states.TryRemove(ticker, out _);
    }

    private async Task SimulationLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var ticker in _tickers.ToList())
            {
                if (!_states.TryGetValue(ticker, out var state)) continue;

                var move = _rng.Next(-2, 3) * 5;
                state.Last = Math.Max(100000, state.Last + move);

                int tradeCount = _rng.Next(1, 5);
                for (int i = 0; i < tradeCount; i++)
                    EmitTrade(ticker, state);

                EmitBookUpdate(ticker, state);
                EmitQuote(ticker, state);
            }

            await Task.Delay(100, ct).ContinueWith(_ => { });
        }
    }

    private static readonly string[] Brokers =
        ["XP", "BTG", "RLP", "Goldman", "Itaú", "Morgan", "CSHG", "Bradesco"];

    private void EmitTrade(string ticker, TickerState state)
    {
        var offset = _rng.Next(-4, 5) * 5;
        var price  = state.Last + offset;

        var vol = IsBigLot()
            ? _rng.Next(20, 120) * 5
            : _rng.Next(1, 10)  * 5;

        // Agressor influenciado pela fase atual
        TradeAggressor aggressor;
        var rnd = _rng.NextDouble();
        aggressor = _faseAtual switch
        {
            FasePressao.DominanciaCompra => rnd < 0.80 ? TradeAggressor.Buy  : TradeAggressor.Sell,
            FasePressao.DominanciaVenda  => rnd < 0.80 ? TradeAggressor.Sell : TradeAggressor.Buy,
            _                            => rnd < 0.50 ? TradeAggressor.Buy  : TradeAggressor.Sell,
        };

        var broker = Brokers[_rng.Next(Brokers.Length)];

        OnTrade?.Invoke(new TradeEvent(ticker, price, vol, broker, aggressor, DateTime.Now));

        state.TotalVolume += vol;
        if (aggressor == TradeAggressor.Buy) state.Delta += vol;
        else state.Delta -= vol;
    }

    private void EmitBookUpdate(string ticker, TickerState state)
{
    int levels = 30;

    // Limpar todos os níveis existentes antes de emitir novos
    // (evita acúmulo de níveis antigos quando o preço se move)
    for (int i = 1; i <= 60; i++)
    {
        OnBook?.Invoke(new BookLevel(ticker, BookSide.Bid, state.Last - i * 5 - 300m, 0, "", DateTime.Now));
        OnBook?.Invoke(new BookLevel(ticker, BookSide.Ask, state.Last + i * 5 + 300m, 0, "", DateTime.Now));
    }

    for (int i = 1; i <= levels; i++)
    {
        var bidPrice = state.Last - i * 5;
        var askPrice = state.Last + i * 5;

        var bidVol = IsBigLot() ? _rng.Next(20, 160) * 5 : _rng.Next(1, 15) * 5;
        var askVol = IsBigLot() ? _rng.Next(20, 160) * 5 : _rng.Next(1, 15) * 5;

        var bidBroker = Brokers[_rng.Next(Brokers.Length)];
        var askBroker = Brokers[_rng.Next(Brokers.Length)];

        OnBook?.Invoke(new BookLevel(ticker, BookSide.Bid, bidPrice, bidVol, bidBroker, DateTime.Now));
        OnBook?.Invoke(new BookLevel(ticker, BookSide.Ask, askPrice, askVol, askBroker, DateTime.Now));
    }
}

    private void EmitQuote(string ticker, TickerState state)
    {
        state.High = Math.Max(state.High, state.Last);
        state.Low  = Math.Min(state.Low,  state.Last);

        OnQuote?.Invoke(new QuoteEvent(
            Ticker: ticker,
            Last:   state.Last,
            Bid:    state.Last - 5,
            Ask:    state.Last + 5,
            Open:   state.Open,
            High:   state.High,
            Low:    state.Low,
            Volume: state.TotalVolume,
            Time:   DateTime.Now
        ));
    }

    private bool IsBigLot() => _rng.NextDouble() < 0.06;

    public void Dispose() => DisconnectAsync().GetAwaiter().GetResult();
}

// Timer simples sem dispatcher
internal class DispatcherlessTimer
{
    private readonly Action _callback;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cts;

    public DispatcherlessTimer(Action callback, TimeSpan interval)
    {
        _callback = callback;
        _interval = interval;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(_interval, token).ContinueWith(_ => { });
                if (!token.IsCancellationRequested)
                    _callback();
            }
        });
    }

    public void Stop() => _cts?.Cancel();
}

internal class TickerState(decimal basePrice)
{
    public decimal Last        { get; set; } = basePrice;
    public decimal Open        { get; }      = basePrice;
    public decimal High        { get; set; } = basePrice + 50;
    public decimal Low         { get; set; } = basePrice - 50;
    public long    TotalVolume { get; set; } = 0;
    public long    Delta       { get; set; } = 0;
}