using System.Collections.Concurrent;
using MarketCore.Contracts;
using MarketCore.Engine.Detectors;
using MarketCore.Models;

namespace MarketCore.Engine;

public sealed class MarketEngine : IDisposable
{
    private readonly IMarketDataProvider _provider;
    private readonly CancellationTokenSource _cts = new();

    public event Action<TradeEvent>?             OnTrade;
    public event Action<BookSnapshot>?           OnBookSnapshot;
    public event Action<QuoteEvent>?             OnQuote;
    public event Action<ConnectionChangedEvent>? OnConnectionChanged;

    private readonly ConcurrentDictionary<string, BookState> _books = new();
    private readonly ConcurrentQueue<BookSnapshot> _uiQueue = new();
    private Thread? _uiDispatchThread;

    public readonly SpoofDetector     Spoof     = new();
    public readonly IcebergDetector   Iceberg   = new();
    public readonly RenewableDetector Renewable = new();

    private decimal _lastPrice = 0;

    public string ProviderName => _provider.ProviderName;
    public ConnectionStatus Status => _provider.Status;

    public MarketEngine(IMarketDataProvider provider)
    {
        _provider = provider;
        _provider.OnTrade             += HandleTrade;
        _provider.OnBook              += HandleBook;
        _provider.OnQuote             += HandleQuote;
        _provider.OnConnectionChanged += HandleConnectionChanged;
    }

    public Task ConnectAsync(ProviderCredentials credentials)
    {
        StartUiDispatch();
        return _provider.ConnectAsync(credentials);
    }

    public Task DisconnectAsync() => _provider.DisconnectAsync();

    public void Subscribe(string ticker)
    {
        _books[ticker] = new BookState(ticker);
        _provider.Subscribe(ticker);
    }

    public void Unsubscribe(string ticker)
    {
        _books.TryRemove(ticker, out _);
        _provider.Unsubscribe(ticker);
    }

    public BookSnapshot? GetBook(string ticker)
        => _books.TryGetValue(ticker, out var state) ? state.CurrentSnapshot : null;

    private void HandleTrade(TradeEvent trade) => OnTrade?.Invoke(trade);

    private void HandleBook(BookLevel level)
    {
        if (!_books.TryGetValue(level.Ticker, out var state)) return;
        state.Update(level);

        Spoof.ProcessLevel(level, _lastPrice);
        Renewable.ProcessLevel(level);

        if (state.NeedsUiUpdate)
        {
            var snap = state.CurrentSnapshot;
            Iceberg.ProcessSnapshot(snap);
            _uiQueue.Enqueue(snap);
            state.NeedsUiUpdate = false;
        }
    }

    private void HandleQuote(QuoteEvent quote)
    {
        _lastPrice = quote.Last;
        OnQuote?.Invoke(quote);
    }

    private void HandleConnectionChanged(ConnectionChangedEvent evt)
        => OnConnectionChanged?.Invoke(evt);

    private void StartUiDispatch()
    {
        _uiDispatchThread = new Thread(() =>
        {
            const int frameMs = 16;
            while (!_cts.Token.IsCancellationRequested)
            {
                var frameStart = DateTime.UtcNow;
                var latest = new Dictionary<string, BookSnapshot>();
                while (_uiQueue.TryDequeue(out var snap))
                    latest[snap.Ticker] = snap;
                foreach (var snap in latest.Values)
                {
                    try { OnBookSnapshot?.Invoke(snap); }
                    catch { }
                }
                var elapsed = (int)(DateTime.UtcNow - frameStart).TotalMilliseconds;
                var sleep = frameMs - elapsed;
                if (sleep > 0) Thread.Sleep(sleep);
            }
        })
        {
            IsBackground = true,
            Name = "MarketEngine-UiDispatch",
            Priority = ThreadPriority.Normal
        };
        _uiDispatchThread.Start();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _provider.OnTrade             -= HandleTrade;
        _provider.OnBook              -= HandleBook;
        _provider.OnQuote             -= HandleQuote;
        _provider.OnConnectionChanged -= HandleConnectionChanged;
        _provider.Dispose();
        _cts.Dispose();
    }
}

internal sealed class BookState
{
    private readonly string _ticker;
    private readonly Dictionary<decimal, BookLevel> _bids = new();
    private readonly Dictionary<decimal, BookLevel> _asks = new();
    private BookSnapshot _snapshot;

    public bool NeedsUiUpdate { get; set; }
    public BookSnapshot CurrentSnapshot => _snapshot;

    public BookState(string ticker)
    {
        _ticker = ticker;
        _snapshot = new BookSnapshot(ticker, Array.Empty<BookLevel>(), Array.Empty<BookLevel>(), DateTime.Now);
    }

    public void Update(BookLevel level)
    {
        var dict = level.Side == BookSide.Bid ? _bids : _asks;
        if (level.Volume <= 0)
            dict.Remove(level.Price);
        else
            dict[level.Price] = level;

        _snapshot = new BookSnapshot(
            Ticker:    _ticker,
            Bids:      _bids.Values.OrderByDescending(b => b.Price).ToArray(),
            Asks:      _asks.Values.OrderBy(a => a.Price).ToArray(),
            Timestamp: level.Time
        );
        NeedsUiUpdate = true;
    }
}

public sealed record BookSnapshot(
    string                   Ticker,
    IReadOnlyList<BookLevel> Bids,
    IReadOnlyList<BookLevel> Asks,
    DateTime                 Timestamp
)
{
    public decimal? BestBid => Bids.Count > 0 ? Bids[0].Price : null;
    public decimal? BestAsk => Asks.Count > 0 ? Asks[0].Price : null;
    public decimal? Spread  => BestBid.HasValue && BestAsk.HasValue ? BestAsk - BestBid : null;
}