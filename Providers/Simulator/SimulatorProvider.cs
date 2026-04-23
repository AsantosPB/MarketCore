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

    public async Task ConnectAsync(ProviderCredentials credentials)
    {
        Status = ConnectionStatus.Connecting;
        OnConnectionChanged?.Invoke(new ConnectionChangedEvent(Status, "Simulador iniciando..."));
        await Task.Delay(300);
        Status = ConnectionStatus.Connected;
        OnConnectionChanged?.Invoke(new ConnectionChangedEvent(Status, "Simulador conectado"));
        _simTask = Task.Run(() => SimulationLoop(_cts.Token));
    }

    public async Task DisconnectAsync()
    {
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

                // Drift de preço em múltiplos de 5
                var move = _rng.Next(-2, 3) * 5;
                state.Last = Math.Max(100000, state.Last + move);

                // Trades — volume real do WINFUT no pico
                int tradeCount = _rng.Next(10, 30);
                for (int i = 0; i < tradeCount; i++)
                    EmitTrade(ticker, state);

                // Book completo
                EmitBookUpdate(ticker, state);

                // Cotação
                EmitQuote(ticker, state);
            }

            await Task.Delay(10, ct).ContinueWith(_ => { });
        }
    }

    private static readonly string[] Brokers =
        ["XP", "BTG", "RLP", "Goldman", "Itaú", "Morgan", "CSHG", "Bradesco"];

    private void EmitTrade(string ticker, TickerState state)
    {
        // Preço sempre múltiplo de 5
        var offset = _rng.Next(-4, 5) * 5;
        var price  = state.Last + offset;

        // Volume: maioria pequeno, ocasionalmente grande
        var vol = IsBigLot()
            ? _rng.Next(20, 120) * 5   // lote grande: 100 a 600
            : _rng.Next(1, 10)  * 5;   // lote pequeno: 5 a 50

        var aggressor = _rng.NextDouble() > 0.5 ? TradeAggressor.Buy : TradeAggressor.Sell;
        var broker    = Brokers[_rng.Next(Brokers.Length)];

        OnTrade?.Invoke(new TradeEvent(ticker, price, vol, broker, aggressor, DateTime.Now));

        state.TotalVolume += vol;
        if (aggressor == TradeAggressor.Buy) state.Delta += vol;
        else state.Delta -= vol;
    }

    private void EmitBookUpdate(string ticker, TickerState state)
    {
        int levels = 30;
        for (int i = 1; i <= levels; i++)
        {
            // Preços sempre múltiplos de 5
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

internal class TickerState(decimal basePrice)
{
    public decimal Last        { get; set; } = basePrice;
    public decimal Open        { get; }      = basePrice;
    public decimal High        { get; set; } = basePrice + 50;
    public decimal Low         { get; set; } = basePrice - 50;
    public long    TotalVolume { get; set; } = 0;
    public long    Delta       { get; set; } = 0;
}