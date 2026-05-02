using System.Collections.Concurrent;
using MarketCore.Contracts;
using MarketCore.Engine.Detectors;
using MarketCore.Engine.Recording;
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

    public readonly SpoofDetector      Spoof      = new();
    public readonly IcebergDetector    Iceberg    = new();
    public readonly RenewableDetector  Renewable  = new();
    public readonly ExhaustionDetector Exhaustion = new();

    private IMarketRecorder? _recorder;
    private bool _recordingEnabled = false;

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

    public void HabilitarGravacao(string diretorioBase, bool isSimulator = false)
    {
        if (isSimulator)
            diretorioBase = System.IO.Path.Combine(diretorioBase, "_SIM");

        _recorder = new MarketRecorder(diretorioBase);

        _recorder.ErroGravacao += (s, e) =>
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[RECORDER ERRO] {e.Mensagem}");
            Console.ResetColor();
        };

        _recorder.AvisoGravacao += (s, e) =>
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[RECORDER] {e.Mensagem}");
            Console.ResetColor();
        };

        _recordingEnabled = true;
        Console.WriteLine($"[RECORDER] Gravação de TRADES + BOOK habilitada em: {diretorioBase}");
    }

    public void DesabilitarGravacao()
    {
        _recordingEnabled = false;
        _recorder?.Dispose();
        _recorder = null;
        Console.WriteLine("[RECORDER] Gravação desabilitada");
    }

    public void GravarFlowScore(double preco, double scoreTotal,
        double brokerFlow, double fluxoDireto, double book, double detectores)
    {
        if (_recordingEnabled && _recorder != null)
            _ = _recorder.GravarFlowScoreAsync(
                "WIN", preco, scoreTotal, brokerFlow, fluxoDireto, book, detectores);
    }

    public async Task ConnectAsync(ProviderCredentials credentials)
    {
        StartUiDispatch();

        if (_recordingEnabled && _recorder != null)
        {
            var hoje = DateOnly.FromDateTime(DateTime.Now);
            var iniciou = await _recorder.IniciarPregaoAsync(hoje);
            if (!iniciou)
            {
                Console.WriteLine("[RECORDER] Falha ao iniciar pregão - gravação desabilitada");
                _recordingEnabled = false;
            }
        }

        await _provider.ConnectAsync(credentials);
    }

    public async Task DisconnectAsync()
    {
        if (_recordingEnabled && _recorder != null)
        {
            var status = _recorder.Status;
            Console.WriteLine($"\n[RECORDER] Pregão {status.PregaoAtivo} finalizado. " +
                              $"Trades: {status.TotaisTrades}, Books: {status.TotaisBooks}");
            await _recorder.FinalizarPregaoAsync();
        }

        await _provider.DisconnectAsync();
    }

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

    private void HandleTrade(TradeEvent trade)
    {
        Exhaustion.ProcessarTrade(trade);

        if (_recordingEnabled && _recorder != null)
            _ = _recorder.GravarTradeAsync(ExtrairAtivo(trade.Ticker), trade);

        OnTrade?.Invoke(trade);
    }

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

            if (_recordingEnabled && _recorder != null)
                _ = _recorder.GravarBookAsync(ExtrairAtivo(snap.Ticker), snap);

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
    {
        if (_recordingEnabled && _recorder != null)
            _ = _recorder.GravarEventoAsync($"CONNECTION: {evt.Status} - {evt.Message}", DateTime.UtcNow);

        OnConnectionChanged?.Invoke(evt);
    }

    private string ExtrairAtivo(string ticker)
    {
        if (ticker.StartsWith("WIN")) return "WIN";
        if (ticker.StartsWith("WDO")) return "WDO";
        if (ticker.StartsWith("WSP")) return "WSP";
        return ticker;
    }

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
        _recorder?.Dispose();
    }
}

internal sealed class BookState
{
    private readonly string _ticker;

    // Chave: OfferId quando disponível (oferta individual), Price quando OfferId=0 (agregado)
    private readonly Dictionary<long, BookLevel>  _bidsByOffer = new();
    private readonly Dictionary<long, BookLevel>  _asksByOffer = new();

    // Para ofertas sem ID (OfferId=0), usa preço como chave negativa para não colidir
    private long _pseudoIdCounter = -1;
    private readonly Dictionary<decimal, long> _priceToPseudoId = new();

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
        var dict = level.Side == BookSide.Bid ? _bidsByOffer : _asksByOffer;

        if (level.OfferId != 0)
        {
            // Oferta individual com ID único
            if (level.Volume <= 0)
                dict.Remove(level.OfferId);
            else
                dict[level.OfferId] = level;
        }
        else
        {
            // Sem OfferId — agrega por preço (fallback)
            if (!_priceToPseudoId.TryGetValue(level.Price, out long pid))
            {
                pid = _pseudoIdCounter--;
                _priceToPseudoId[level.Price] = pid;
            }

            if (level.Volume <= 0)
            {
                dict.Remove(pid);
                _priceToPseudoId.Remove(level.Price);
            }
            else
            {
                dict[pid] = level;
            }
        }

        var bids = _bidsByOffer.Values
            .OrderByDescending(b => b.Price)
            .ThenBy(b => b.OfferId)
            .ToArray();

        var asks = _asksByOffer.Values
            .OrderBy(a => a.Price)
            .ThenBy(a => a.OfferId)
            .ToArray();

        // Preços cruzados = dados obsoletos, limpa tudo
        if (bids.Length > 0 && asks.Length > 0 && bids[0].Price >= asks[0].Price)
        {
            _bidsByOffer.Clear();
            _asksByOffer.Clear();
            _priceToPseudoId.Clear();

            var dict2 = level.Side == BookSide.Bid ? _bidsByOffer : _asksByOffer;
            if (level.Volume > 0 && level.OfferId != 0)
                dict2[level.OfferId] = level;

            bids = _bidsByOffer.Values.OrderByDescending(b => b.Price).ToArray();
            asks = _asksByOffer.Values.OrderBy(a => a.Price).ToArray();
        }

        _snapshot = new BookSnapshot(
            Ticker: _ticker,
            Bids:   bids,
            Asks:   asks,
            Time:   level.Time
        );

        NeedsUiUpdate = true;
    }
}
