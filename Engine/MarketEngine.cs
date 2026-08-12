using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using MarketCore.Contracts;
using MarketCore.Engine.Detectors;
using MarketCore.Engine.Recording;
using MarketCore.Models;

namespace MarketCore.Engine;

public sealed class MarketEngine : IDisposable
{
    private static void LogEngineFault(string where, Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarketCore");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "crash.log"),
                $"==== {DateTime.UtcNow:o} MarketEngine.{where} ====\n{ex}\n\n");
        }
        catch { /* ignore */ }
    }

    private readonly IMarketDataProvider _provider;
    private readonly CancellationTokenSource _cts = new();

    public event Action<TradeEvent>?             OnTrade;
    public event Action<BookSnapshot>?           OnBookSnapshot;
    public event Action<QuoteEvent>?             OnQuote;
    public event Action<ConnectionChangedEvent>? OnConnectionChanged;

    private readonly ConcurrentDictionary<string, BookState> _books = new();
    private readonly ConcurrentQueue<BookSnapshot> _uiQueue = new();
    private Thread? _uiDispatchThread;
    private int _uiDispatchStarted;

    /// <summary>Fila trades → subscribers (<see cref="OnTrade"/>) fora da thread de callback da DLL.</summary>
    private readonly ConcurrentQueue<TradeEvent> _tradeFanoutQueue = new();
    private Thread? _tradeFanoutThread;
    private int _tradeFanoutStarted;

    /// <summary>Faz rebuild + Iceberg/recorder/UI fora da thread da DLL para não sufocar a fila de ofertas.</summary>
    private Thread? _snapshotPublishThread;
    private int _snapshotPublishStarted;

    /// <summary>Spoof/Renewable rodam aqui para não competir com Apply do livro na thread do provider.</summary>
    private readonly ConcurrentQueue<BookLevel> _detectorLevelQueue = new();
    private Thread? _detectorDrainThread;
    private int _detectorDrainStarted;

    /// <summary>
    /// Teto “soft” só para esta fila de <b>detectores</b> (não é o livro DOM).
    /// Se <see cref="ConcurrentQueue{T}.Count"/> &gt; este valor, descartamos eventos antigos até ficar em metade —
    /// o estado do livro em <see cref="BookState"/> já foi aplicado; perde-se apenas histórico para Spoof/Renewable.
    /// </summary>
    private const int DetectorQueueSoftCap = 400_000;

    /// <summary>
    /// Quando <c>false</c>, os deltas de livro não entram na fila dos microestrutura (Spoof/Renewable): não há crescimento nem poda aos 400k.
    /// Iceberg continua com snapshots na thread de publicação; exaustão e outros fluxos baseados em trade não são afetados.
    /// </summary>
    public static bool EnableBookMicrostructureDetectors { get; set; } = true;

    public readonly SpoofDetector      Spoof      = new();
    public readonly IcebergDetector    Iceberg    = new();
    public readonly RenewableDetector  Renewable  = new();
    public readonly ExhaustionDetector Exhaustion = new();

    private IMarketRecorder? _recorder;
    private bool _recordingEnabled = false;

    private decimal _lastPrice = 0;

    private volatile IAnaliseQuantDataSink? _analiseQuantSink;

    public string ProviderName => _provider.ProviderName;
    public ConnectionStatus Status => _provider.Status;

    /// <summary>Registra sink da Análise Quantitativa (thread-safe; chamado da UI).</summary>
    public void SetAnaliseQuantSink(IAnaliseQuantDataSink? sink) => _analiseQuantSink = sink;

    public MarketEngine(IMarketDataProvider provider)
    {
        _provider = provider;
        _provider.OnTrade             += HandleTrade;
        _provider.OnBook              += HandleBook;
        _provider.OnBookFullRefresh   += HandleBookFullRefresh;
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
        StartTradeFanout();
        StartBookSnapshotPublishing();
        StartDetectorDrain();

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
        => TryResolveBookState(ticker, out var state) ? state.GetSnapshotEnsuringFresh() : null;

    /// <summary>Diagnóstico: retorna string com contadores internos do BookState para exibição na UI.</summary>
    public string GetBookDiagnostics(string ticker)
    {
        if (!TryResolveBookState(ticker, out var state))
            return "no-state";
        int bidC, askC;
        lock (state.SyncRoot) { bidC = state.InternalBidCount; askC = state.InternalAskCount; }
        long fr = Interlocked.Read(ref state.FullRefreshCount);
        long fre = Interlocked.Read(ref state.FullRefreshEmptyCount);
        long dc = Interlocked.Read(ref state.DeltaCount);
        return $"int:{bidC}B/{askC}A  FR:{fr}(e{fre})  Δ:{dc}";
    }

    private bool TryResolveBookState(string ticker, out BookState state)
    {
        if (_books.TryGetValue(ticker, out state!))
            return true;

        string root = ExtrairAtivo(ticker);
        foreach (var kv in _books)
        {
            if (string.Equals(ExtrairAtivo(kv.Key), root, StringComparison.OrdinalIgnoreCase))
            {
                state = kv.Value;
                return true;
            }
        }

        state = null!;
        return false;
    }

    private void HandleTrade(TradeEvent trade)
    {
        try
        {
            Exhaustion.ProcessarTrade(trade);

            if (_recordingEnabled && _recorder != null)
                _ = _recorder.GravarTradeAsync(ExtrairAtivo(trade.Ticker), trade);

            _tradeFanoutQueue.Enqueue(trade);
        }
        catch (Exception ex)
        {
            LogEngineFault(nameof(HandleTrade), ex);
        }
    }

    private void HandleBook(BookLevel level)
    {
        try
        {
            if (!TryResolveBookState(level.Ticker, out var state)) return;

            // Filtros baratos ANTES de entrar no lock — 95% dos eventos são descartados aqui.
            bool pvvCandidate =
                MarketCore.Providers.Nelogica.PregaoVivaVozHook.OnBookUpdate != null
                && (level.Action == 0 || level.Action == 1)
                && level.Volume > 0
                && !string.IsNullOrEmpty(level.Broker);

            int nivelPvv = 0;
            int volumeAgregadoBroker = 0;
            lock (state.SyncRoot)
            {
                state.ApplyBookDeltaUnsafe(level);
            }

            // PVV: computações de rank e volume movidas para FORA do lock.
            // Seguro porque BookProcessingLoop é single-threaded — o mesmo thread que
            // escreve (ApplyBookDeltaUnsafe) é o que lê aqui. Nenhum outro writer existe
            // concorrentemente. Leitores (snapshot) só copiam sob lock, não mutam.
            // Resultado: lock held ~50-70% menos tempo, liberando snapshot threads.
            if (pvvCandidate)
            {
                // Nível agregado por preço: 1 = boca, 2 = segundo melhor preço, ...
                nivelPvv = state.PriceRankUnsafe(level.Side, level.Price, maxRank: 5);
                // Regra pedida pelo Anderson: SOMA quando mesma corretora + mesmo preço.
                // Preços diferentes ficam individuais (esse cálculo já filtra por price+broker).
                volumeAgregadoBroker = state.AggregateBrokerVolumeAtPriceUnsafe(
                    level.Side, level.Price, level.Broker);
            }

            var pvvBookHook = MarketCore.Providers.Nelogica.PregaoVivaVozHook.OnBookUpdate;
            if (pvvBookHook != null && nivelPvv >= 1 && nivelPvv <= 5 && volumeAgregadoBroker > 0)
            {
                string lado = level.Side == BookSide.Bid ? "compra" : "venda";
                // callbackInfo já formatada viaja pareada com o evento até o log — elimina
                // decorrelação que o global UltimoBookCallbackInfo causava.
                string bolsa = level.ExchangeTime.HasValue
                    ? level.ExchangeTime.Value.ToLocalTime().ToString("HH:mm:ss.fff")
                    : "--:--:--.---";
                string callbackInfo =
                    $"BOOK  bolsa={bolsa} ticker={level.Ticker} agent={level.Broker} lado={lado} nivel={nivelPvv} qtd={volumeAgregadoBroker}";
                pvvBookHook(level.Ticker, level.Broker, lado, nivelPvv, volumeAgregadoBroker, callbackInfo);
            }

            // Detectores: inclui deletes da DLL quando relevante para microestrutura.
            if (EnableBookMicrostructureDetectors && level.Volume != -1)
            {
                if (_detectorLevelQueue.Count > DetectorQueueSoftCap)
                {
                    while (_detectorLevelQueue.Count > DetectorQueueSoftCap / 2)
                        _detectorLevelQueue.TryDequeue(out _);
                }

                _detectorLevelQueue.Enqueue(level);
            }
        }
        catch (Exception ex)
        {
            LogEngineFault(nameof(HandleBook), ex);
        }
    }

    private void HandleBookFullRefresh(BookFullRefresh e)
    {
        try
        {
            if (!TryResolveBookState(e.Ticker, out var state)) return;

            lock (state.SyncRoot)
                state.ReplaceFullBookUnsafe(e.Bids, e.Asks);

            if (EnableBookMicrostructureDetectors)
            {
                if (_detectorLevelQueue.Count > DetectorQueueSoftCap)
                {
                    while (_detectorLevelQueue.Count > DetectorQueueSoftCap / 2)
                        _detectorLevelQueue.TryDequeue(out _);
                }

                if (e.Bids != null)
                {
                    foreach (var lvl in e.Bids)
                        _detectorLevelQueue.Enqueue(lvl);
                }

                if (e.Asks != null)
                {
                    foreach (var lvl in e.Asks)
                        _detectorLevelQueue.Enqueue(lvl);
                }
            }
        }
        catch (Exception ex)
        {
            LogEngineFault(nameof(HandleBookFullRefresh), ex);
        }
    }

    private void StartDetectorDrain()
    {
        if (Interlocked.Exchange(ref _detectorDrainStarted, 1) != 0)
            return;

        _detectorDrainThread = new Thread(() =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                int n = 0;
                // Por volta do thread de detectores — não confundir com teto da fila (ver DetectorQueueSoftCap).
                const int maxDetectorLevelsPerDrainPass = 8192;
                while (n < maxDetectorLevelsPerDrainPass && _detectorLevelQueue.TryDequeue(out var lvl))
                {
                    n++;
                    try
                    {
                        if (EnableBookMicrostructureDetectors)
                        {
                            Spoof.ProcessLevel(lvl, _lastPrice);
                            Renewable.ProcessLevel(lvl);
                        }
                    }
                    catch { }
                }

                if (n > 0)
                    Thread.Sleep(0);
                else
                    Thread.Sleep(1);
            }
        })
        {
            IsBackground = true,
            Name = "MarketEngine-Detectors",
            Priority = ThreadPriority.AboveNormal
        };
        _detectorDrainThread.Start();
    }

    /// <returns>True se existe algum ticker com rebuild publicado nesta volta.</returns>
    private bool PublishDirtyBookSnapshots()
    {
        bool flushed = false;

        foreach (var kv in _books)
        {
            if (!kv.Value.TryFlushSnapshotIfDirty(out var snap))
                continue;

            flushed = true;

            try { Iceberg.ProcessSnapshot(snap); }
            catch { }

            if (_recordingEnabled && _recorder != null)
                _ = _recorder.GravarBookAsync(ExtrairAtivo(snap.Ticker), snap);

            _uiQueue.Enqueue(snap);
        }

        return flushed;
    }

    private void StartBookSnapshotPublishing()
    {
        if (Interlocked.Exchange(ref _snapshotPublishStarted, 1) != 0)
            return;

        _snapshotPublishThread = new Thread(() =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                bool flushed = false;
                const int maxBurstPasses = 32;
                for (int burst = 0; burst < maxBurstPasses && PublishDirtyBookSnapshots(); burst++)
                    flushed = true;

                if (flushed)
                    Thread.Sleep(0);
                else
                    Thread.Sleep(1);
            }
        })
        {
            IsBackground = true,
            Name = "MarketEngine-BookSnapshots",
            Priority = ThreadPriority.AboveNormal
        };
        _snapshotPublishThread.Start();
    }

    private void HandleQuote(QuoteEvent quote)
    {
        try
        {
            _lastPrice = quote.Last;
            try
            {
                OnQuote?.Invoke(quote);
            }
            catch (Exception subEx)
            {
                LogEngineFault(nameof(HandleQuote) + ".OnQuote", subEx);
            }
        }
        catch (Exception ex)
        {
            LogEngineFault(nameof(HandleQuote), ex);
        }
    }

    private void HandleConnectionChanged(ConnectionChangedEvent evt)
    {
        try
        {
            if (_recordingEnabled && _recorder != null)
                _ = _recorder.GravarEventoAsync($"CONNECTION: {evt.Status} - {evt.Message}", DateTime.UtcNow);

            try
            {
                OnConnectionChanged?.Invoke(evt);
            }
            catch (Exception subEx)
            {
                LogEngineFault(nameof(HandleConnectionChanged) + ".OnConnectionChanged", subEx);
            }
        }
        catch (Exception ex)
        {
            LogEngineFault(nameof(HandleConnectionChanged), ex);
        }
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
        if (Interlocked.Exchange(ref _uiDispatchStarted, 1) != 0)
            return;

        _uiDispatchThread = new Thread(() =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var latest = new Dictionary<string, BookSnapshot>();
                while (_uiQueue.TryDequeue(out var snap))
                    latest[snap.Ticker] = snap;

                if (latest.Count > 0)
                {
                    foreach (var snap in latest.Values)
                    {
                        try { OnBookSnapshot?.Invoke(snap); }
                        catch { }
                    }
                    continue;
                }

                Thread.Sleep(1);
            }
        })
        {
            IsBackground = true,
            Name = "MarketEngine-UiDispatch",
            Priority = ThreadPriority.AboveNormal
        };
        _uiDispatchThread.Start();
    }

    private void StartTradeFanout()
    {
        if (Interlocked.Exchange(ref _tradeFanoutStarted, 1) != 0)
            return;

        _tradeFanoutThread = new Thread(() =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                int burst = 0;
                while (_tradeFanoutQueue.TryDequeue(out TradeEvent trade))
                {
                    burst++;
                    try
                    {
                        OnTrade?.Invoke(trade);
                    }
                    catch (Exception subEx)
                    {
                        LogEngineFault(nameof(HandleTrade) + ".OnTrade", subEx);
                    }

                    try
                    {
                        _analiseQuantSink?.OnTrade(trade);
                    }
                    catch (Exception subEx)
                    {
                        LogEngineFault(nameof(HandleTrade) + ".AnaliseSink", subEx);
                    }
                    if ((burst & 4095) == 0)
                        Thread.Sleep(0);
                }

                if (burst == 0)
                    Thread.Sleep(1);
            }
        })
        {
            IsBackground = true,
            Name = "MarketEngine-TradeFanout",
            Priority = ThreadPriority.AboveNormal
        };
        _tradeFanoutThread.Start();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _snapshotPublishThread?.Join(TimeSpan.FromMilliseconds(2500));
        _detectorDrainThread?.Join(TimeSpan.FromMilliseconds(2500));
        _tradeFanoutThread?.Join(TimeSpan.FromMilliseconds(2500));
        _uiDispatchThread?.Join(TimeSpan.FromMilliseconds(2500));
        _provider.OnTrade             -= HandleTrade;
        _provider.OnBook              -= HandleBook;
        _provider.OnBookFullRefresh   -= HandleBookFullRefresh;
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

    // ═══════════════════════════════════════════════════════════════════════════
    // Conforme manual Nelogica:
    //
    // nPosition refere-se ao índice na lista LINEAR de todas as ofertas do lado —
    // análogo a percorrer níveis de preço (PosiçãoUP) × ofertas no nível (Posição).
    //
    // nPosition é contado do FINAL:  realIdx = size - nPosition - 1
    //
    // Actions:
    //   0 = atAdd        → Inserir na posição linear (índice = size - nPosition)
    //   1 = atEdit       → Atualizar na posição
    //   2 = atDelete     → RemoveAt(realIdx); realIdx = size - nPosition - 1 (exemplo OfferBookCallbackV2)
    //   3 = atDeleteFrom → RemoveRange(realIdx, nPosition+1) com mesmo realIdx — manual Nelogica
    //   4 = atFullBook   → (provider) mescla por OfferId sem limpar o lado (ver ReplaceFullBookUnsafe)
    //   5 = FullBookAdd  → Adiciona direto ao final (sem calcular nPosition)
    //
    // Side: Compra=0 (BID), Venda=1 (ASK)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Limite físico das listas internas por lado — WINM26 chega a 20.000+ ofertas em momentos de pico.</summary>
    private const int MAX_LEVELS = 30000;

    /// <summary>
    /// Teto operacional igual ao físico (<see cref="MAX_LEVELS"/>): remoções de níveis vêm da DLL (<c>atDelete</c>/<c>atDeleteFrom</c>),
    /// não por eviction até quase-cheio que competia visualmente com o livro esperado (~80%).
    /// </summary>
    private const int OperationalBookSideCap = MAX_LEVELS;

    /// <summary>Linhas por lado no snapshot. WIN pode ter centenas de ofertas nos melhores preços; 300 cortava no meio da fila.</summary>
    private const int VisibleBookLines = 2500;

    /// <summary>Linhas por lado no book de ofertas <b>individual</b> (uma linha por oferta/corretora, não por preço) — igual ao ProfitChart. Menor que <see cref="VisibleBookLines"/> porque cada oferta agora ocupa uma linha própria.</summary>
    private const int VisibleBookOrderLines = 300;

    /// <summary>Sempre ordenação completa: heap com k=n era ok, mas sort total evita qualquer surpresa com livros grandes.</summary>
    private const int FullSortThreshold = int.MaxValue;

    private readonly List<BookLevel> _bids = new();
    private readonly List<BookLevel> _asks = new();

    // ── Diagnóstico: contadores internos expostos para a UI ──
    internal int InternalBidCount => _bids.Count;
    internal int InternalAskCount => _asks.Count;

    /// <summary>
    /// Retorna o nível agregado por preço em que <paramref name="price"/> se encontra no lado
    /// (1 = boca / melhor preço, 2 = segundo melhor, …). Retorna <c>0</c> se o preço não está
    /// presente. Se houver mais de <paramref name="maxRank"/> preços melhores, retorna
    /// <c>maxRank + 1</c> (permite bail-out barato para eventos fora da faixa de interesse).
    /// Deve ser chamado dentro do <see cref="SyncRoot"/>.
    /// </summary>
    internal int PriceRankUnsafe(BookSide side, decimal price, int maxRank = 5)
    {
        var list = side == BookSide.Bid ? _bids : _asks;
        // Stack-allocated buffer em vez de new HashSet<decimal>() — elimina alocação por chamada.
        // maxRank é tipicamente 5, então 8 slots cobrem com folga.
        Span<decimal> betterPrices = stackalloc decimal[maxRank];
        int distinctCount = 0;
        bool found = false;
        for (int i = 0; i < list.Count; i++)
        {
            var l = list[i];
            if (l.Volume <= 0) continue;
            if (l.Price == price) { found = true; continue; }
            bool isBetter = side == BookSide.Bid ? l.Price > price : l.Price < price;
            if (isBetter)
            {
                // Verificar duplicata no buffer (até 5 comparações — constante)
                bool dup = false;
                for (int j = 0; j < distinctCount; j++)
                {
                    if (betterPrices[j] == l.Price) { dup = true; break; }
                }
                if (!dup)
                {
                    distinctCount++;
                    if (distinctCount > maxRank)
                        return maxRank + 1;
                    betterPrices[distinctCount - 1] = l.Price;
                }
            }
        }
        return found ? distinctCount + 1 : 0;
    }

    /// <summary>
    /// Soma o volume TOTAL de ofertas do broker naquele preço específico e lado.
    /// Nelogica manda cada oferta individual como um callback separado (ex: Goldman
    /// pode ter 4 ofertas de 34 no mesmo preço = 136 total no display). O motor do
    /// Pregão Viva Voz precisa ver o AGREGADO pra decidir se supera o mínimo.
    /// Comparação de broker é case-insensitive; ofertas com Volume ≤ 0 são ignoradas.
    /// Deve ser chamado dentro do <see cref="SyncRoot"/>.
    /// </summary>
    internal int AggregateBrokerVolumeAtPriceUnsafe(BookSide side, decimal price, string broker)
    {
        if (string.IsNullOrEmpty(broker)) return 0;
        var list = side == BookSide.Bid ? _bids : _asks;
        long soma = 0;
        for (int i = 0; i < list.Count; i++)
        {
            var l = list[i];
            if (l.Volume <= 0) continue;
            if (l.Price != price) continue;
            if (!string.Equals(l.Broker, broker, StringComparison.OrdinalIgnoreCase)) continue;
            soma += l.Volume;
        }
        return soma > int.MaxValue ? int.MaxValue : (int)soma;
    }
    internal long FullRefreshCount;
    internal long FullRefreshEmptyCount;
    internal long DeltaCount;

    private BookSnapshot _snapshot;
    private bool _snapshotDirty;

    /// <summary>Incrementado a cada delta — detecta se o livro mudou durante rebuild fora do lock.</summary>
    private ulong _mutationGen;
    private static int _normalizeDiagCount;

    /// <summary>Evita FindIndex O(n) em milhares de ofertas quando a DLL envia OfferId.</summary>
    private readonly Dictionary<long, int> _bidOfferIdx = new();
    private readonly Dictionary<long, int> _askOfferIdx = new();
    private bool _bidOfferIdxStale = true;
    private bool _askOfferIdxStale = true;

    /// <summary>Lock só para mutar listas / copiar / atribuir snapshot (rebuild pesado roda fora).</summary>
    internal readonly object SyncRoot = new();

    public BookState(string ticker)
    {
        _ticker = ticker;
        _snapshot = new BookSnapshot(ticker, Array.Empty<BookLevel>(), Array.Empty<BookLevel>(), DateTime.Now);
    }

    internal BookSnapshot LatestSnapshot => _snapshot;

    private void InvalidateOfferIdx(BookSide side)
    {
        if (side == BookSide.Bid) _bidOfferIdxStale = true;
        else _askOfferIdxStale = true;
    }

    private void EnsureOfferIdx(BookSide side)
    {
        if (side == BookSide.Bid)
        {
            if (!_bidOfferIdxStale) return;
            _bidOfferIdx.Clear();
            for (int i = 0; i < _bids.Count; i++)
            {
                long oid = _bids[i].OfferId;
                if (oid > 0)
                    _bidOfferIdx[oid] = i;
            }

            _bidOfferIdxStale = false;
        }
        else
        {
            if (!_askOfferIdxStale) return;
            _askOfferIdx.Clear();
            for (int i = 0; i < _asks.Count; i++)
            {
                long oid = _asks[i].OfferId;
                if (oid > 0)
                    _askOfferIdx[oid] = i;
            }

            _askOfferIdxStale = false;
        }
    }

    private bool TryOfferIdx(BookSide side, long offerId, out int idx)
    {
        EnsureOfferIdx(side);
        if (offerId <= 0)
        {
            idx = -1;
            return false;
        }

        return side == BookSide.Bid
            ? _bidOfferIdx.TryGetValue(offerId, out idx)
            : _askOfferIdx.TryGetValue(offerId, out idx);
    }

    /// <summary>
    /// Quando <c>Count + vagas_pedidas</c> ultrapassa <see cref="OperationalBookSideCap"/> (igual ao teto físico),
    /// remove a oferta mais fora de mercado pelo preço até haver vagas.
    /// </summary>
    private void EvictOffMarketWorstPricesUntilRoom(List<BookLevel> list, BookSide side, int roomNeeded)
    {
        if (roomNeeded <= 0)
            return;

        bool removed = false;
        while (list.Count + roomNeeded > OperationalBookSideCap && list.Count > 0)
        {
            int victim = FindTailOfWorstPriceQueue(list, side);
            if (victim < 0)
                break;
            list.RemoveAt(victim);
            removed = true;
        }

        if (removed)
            InvalidateOfferIdx(side);
    }

    /// <summary>
    /// Escolhe a vítima só no nível mais fora de mercado: menor preço (bid) ou maior preço (ask).
    /// Em caso de várias linhas nesse nível escolhemos fim da “fila” — horário de oferta mais recente;
    /// se igual, o maior índice na lista (últimas entradas incrementais que costumam ficar no fim).
    /// Evita apanhar sempre o primeiro igual-pior caso, que acabava parecido visualmente ao topo ordenado.
    /// </summary>
    private static int FindTailOfWorstPriceQueue(List<BookLevel> list, BookSide side)
    {
        int n = list.Count;
        if (n == 0)
            return -1;

        if (side == BookSide.Bid)
        {
            decimal minP = list[0].Price;
            for (int i = 1; i < n; i++)
            {
                decimal p = list[i].Price;
                if (p < minP)
                    minP = p;
            }

            int victim = -1;
            DateTime bestTime = DateTime.MinValue;

            for (int i = 0; i < n; i++)
            {
                if (list[i].Price != minP)
                    continue;

                DateTime t = list[i].ExchangeTime ?? DateTime.MinValue;
                bool better = victim < 0 || t > bestTime || (t == bestTime && i > victim);
                if (better)
                {
                    victim = i;
                    bestTime = t;
                }
            }

            return victim;
        }
        else
        {
            decimal maxP = list[0].Price;
            for (int i = 1; i < n; i++)
            {
                decimal p = list[i].Price;
                if (p > maxP)
                    maxP = p;
            }

            int victim = -1;
            DateTime bestTime = DateTime.MinValue;

            for (int i = 0; i < n; i++)
            {
                if (list[i].Price != maxP)
                    continue;

                DateTime t = list[i].ExchangeTime ?? DateTime.MinValue;
                bool better = victim < 0 || t > bestTime || (t == bestTime && i > victim);
                if (better)
                {
                    victim = i;
                    bestTime = t;
                }
            }

            return victim;
        }
    }

    private void RebuildAllOfferIndexes()
    {
        _bidOfferIdx.Clear();
        _askOfferIdx.Clear();
        for (int i = 0; i < _bids.Count; i++)
        {
            long oid = _bids[i].OfferId;
            if (oid > 0)
                _bidOfferIdx[oid] = i;
        }

        for (int i = 0; i < _asks.Count; i++)
        {
            long oid = _asks[i].OfferId;
            if (oid > 0)
                _askOfferIdx[oid] = i;
        }

        _bidOfferIdxStale = false;
        _askOfferIdxStale = false;
    }

    /// <summary>ProfitDLL pode omitir corretora no delta (<c>bHasAgent=0</c>); mantém a já conhecida pelo mesmo <c>OfferId</c>.</summary>
    private static BookLevel CoalesceBrokerFromPrevious(BookLevel incoming, BookLevel? previous)
    {
        if (previous == null) return incoming;
        if (!string.IsNullOrEmpty(incoming.Broker)) return incoming;
        if (incoming.OfferId <= 0 || incoming.OfferId != previous.OfferId) return incoming;
        if (string.IsNullOrEmpty(previous.Broker)) return incoming;
        return incoming with { Broker = previous.Broker };
    }

    /// <summary>Atualiza a mesma oferta (mesmo <c>OfferId</c>): preço absoluto, quantidade delta ou absoluta, hora preservada.</summary>
    private static BookLevel MergeBookLevelFromDelta(BookLevel incoming, BookLevel previous)
    {
        string broker = string.IsNullOrEmpty(incoming.Broker) ? previous.Broker : incoming.Broker;
        decimal price = incoming.Price > 0 ? incoming.Price : previous.Price;
        int volume = previous.Volume;
        if (incoming.VolumeIsDelta)
        {
            if (incoming.Volume != 0)
                volume = previous.Volume + incoming.Volume;
        }
        else if (incoming.Volume > 0)
        {
            volume = incoming.Volume;
        }

        if (volume <= 0)
            volume = previous.Volume;

        long offerId = incoming.OfferId > 0 ? incoming.OfferId : previous.OfferId;
        DateTime? exchangeTime = incoming.ExchangeTime ?? previous.ExchangeTime;
        return incoming with
        {
            Broker = broker,
            Price = price,
            Volume = volume,
            OfferId = offerId,
            ExchangeTime = exchangeTime
        };
    }

    private static int InsertIndexFromNPosition(int listCount, int nPosition) =>
        listCount - nPosition;

    private static int EditIndexFromNPosition(int listCount, int nPosition) =>
        listCount - nPosition - 1;

    private static bool TryInsertOfferAtPosition(List<BookLevel> list, int nPosition, BookLevel level)
    {
        if (nPosition < 0)
            return false;

        if (list.Count == 0)
        {
            list.Add(level);
            return true;
        }

        if (nPosition < list.Count)
        {
            list.Insert(InsertIndexFromNPosition(list.Count, nPosition), level);
            return true;
        }

        // Posição mais profunda do que tudo que rastreamos: na nossa convenção, "pior" fica no índice 0.
        list.Insert(0, level);
        return true;
    }

    private static bool TryMoveOfferToNPosition(List<BookLevel> list, int fromIdx, int nPosition)
    {
        if (fromIdx < 0 || fromIdx >= list.Count || nPosition < 0 || nPosition >= list.Count)
            return false;

        int targetIdx = EditIndexFromNPosition(list.Count, nPosition);

        if (targetIdx < 0 || targetIdx >= list.Count || targetIdx == fromIdx)
            return false;

        var offer = list[fromIdx];
        list.RemoveAt(fromIdx);
        if (targetIdx > fromIdx)
            targetIdx--;

        targetIdx = Math.Clamp(targetIdx, 0, list.Count);
        list.Insert(targetIdx, offer);
        return true;
    }

    /// <summary>Mutação das listas internas sob <see cref=”SyncRoot”/> (rebuild sai em thread de snapshot).</summary>
    internal void ApplyBookDeltaUnsafe(BookLevel level)
    {
        try
        {
            Interlocked.Increment(ref DeltaCount);
            var list = level.Side == BookSide.Bid ? _bids : _asks;

            // Sinalização de “limpar lado” (__CLEAR__/Volume=-1): não esvaziar — só atualizações no livro interno.
            if (level.Volume == -1)
                return;

            // NOTA: EnsureOfferIdx removido daqui — TryOfferIdx já chama internamente.
            // Evita rebuild O(n) do dicionário quando a action não usa o índice (ex: Action 5, deletes posicionais).

            if (level.Action == 5)
            {
                EvictOffMarketWorstPricesUntilRoom(list, level.Side, 1);
                if (list.Count < OperationalBookSideCap)
                    list.Add(level);

                InvalidateOfferIdx(level.Side);
                return;
            }

            switch (level.Action)
            {
                case 0:
                    if (level.OfferId > 0 && TryOfferIdx(level.Side, level.OfferId, out int existingIdx))
                    {
                        list[existingIdx] = MergeBookLevelFromDelta(level, list[existingIdx]);
                        // Só invalida se houve reposicionamento real (RemoveAt+Insert muda todos os índices).
                        // Edits in-place preservam OfferId→index — evita rebuild O(n) do dicionário.
                        bool moved = level.Position >= 0 && level.Position < list.Count
                                     && TryMoveOfferToNPosition(list, existingIdx, level.Position);
                        if (moved)
                            InvalidateOfferIdx(level.Side);
                        break;
                    }

                    EvictOffMarketWorstPricesUntilRoom(list, level.Side, 1);
                    if (TryInsertOfferAtPosition(list, level.Position, level))
                        InvalidateOfferIdx(level.Side);
                    break;

                case 1:
                    if (level.OfferId > 0 && TryOfferIdx(level.Side, level.OfferId, out int offerIdx))
                    {
                        list[offerIdx] = MergeBookLevelFromDelta(level, list[offerIdx]);
                        // Mesmo raciocínio: edits sem reposicionamento não tocam na estrutura da lista.
                        bool moved1 = level.Position >= 0 && level.Position < list.Count
                                      && TryMoveOfferToNPosition(list, offerIdx, level.Position);
                        if (moved1)
                            InvalidateOfferIdx(level.Side);
                        break;
                    }

                    if (level.Position < 0 || level.Position >= list.Count)
                    {
                        if (level.OfferId > 0
                            && TryOfferIdx(level.Side, level.OfferId, out int orphanIdx))
                        {
                            // In-place update por OfferId: nenhuma mudança estrutural.
                            list[orphanIdx] = MergeBookLevelFromDelta(level, list[orphanIdx]);
                        }

                        break;
                    }

                    int targetIdx = EditIndexFromNPosition(list.Count, level.Position);
                    if (level.OfferId > 0
                        && list[targetIdx].OfferId > 0
                        && list[targetIdx].OfferId != level.OfferId)
                    {
                        break;
                    }

                    // In-place update por posição: nenhuma mudança estrutural na lista.
                    list[targetIdx] = MergeBookLevelFromDelta(level, list[targetIdx]);
                    break;

                case 2:
                    // atDelete — espelhar exemplo Nelogica: RemoveAt(Count-nPosition-1) com nPosition válido.
                    if (level.Position >= 0 && level.Position < list.Count)
                    {
                        int idx = EditIndexFromNPosition(list.Count, level.Position);
                        list.RemoveAt(idx);
                        InvalidateOfferIdx(level.Side);
                    }

                    break;

                case 3:
                    // atDeleteFrom — RemoveRange(Count-nPosition-1, nPosition+1)
                    if (level.Position >= 0 && level.Position < list.Count)
                    {
                        int start = EditIndexFromNPosition(list.Count, level.Position);
                        int cnt = level.Position + 1;
                        if (start >= 0 && start + cnt <= list.Count)
                        {
                            list.RemoveRange(start, cnt);
                            InvalidateOfferIdx(level.Side);
                        }
                    }

                    break;
            }
        }
        finally
        {
            unchecked { _mutationGen++; }
            _snapshotDirty = true;
        }
    }

    /// <summary>Mescla <c>atFullBook</c> por oferta (OfferId): não limpa o lado inteiro — snapshots parciais não apagam profundidade já reconstruída por deltas.</summary>
    internal void ReplaceFullBookUnsafe(IReadOnlyList<BookLevel>? bids, IReadOnlyList<BookLevel>? asks)
    {
        Interlocked.Increment(ref FullRefreshCount);

        bool hadData = false;
        if (bids != null && bids.Count > 0)
        {
            MergeSideFromFullSnapshot(BookSide.Bid, _bids, bids);
            hadData = true;
        }

        if (asks != null && asks.Count > 0)
        {
            MergeSideFromFullSnapshot(BookSide.Ask, _asks, asks);
            hadData = true;
        }

        if (!hadData)
            Interlocked.Increment(ref FullRefreshEmptyCount);

        RebuildAllOfferIndexes();
        unchecked { _mutationGen++; }
        _snapshotDirty = true;
    }

    /// <summary>
    /// <c>atFullBook</c> na convenção interna (índice 0 = pior … N-1 = melhor): o parser entrega pos 0 = melhor,
    /// por isso percorremos o array de trás para a frente. Em vez de <c>Clear</c>+substituir (que apagava
    /// tudo quando o pacote trazia menos linhas que o incremental), actualizamos/inserimos por <c>OfferId</c>.
    /// </summary>
    private void MergeSideFromFullSnapshot(BookSide side, List<BookLevel> current, IReadOnlyList<BookLevel> incoming)
    {
        int max = Math.Min(incoming.Count, MAX_LEVELS);
        if (max <= 0)
            return;

        EnsureOfferIdx(side);

        for (int i = max - 1; i >= 0; i--)
        {
            var lvl = incoming[i];
            if (lvl.OfferId <= 0)
                continue;

            if (TryOfferIdx(side, lvl.OfferId, out int idx))
                current[idx] = lvl;
            else
            {
                EvictOffMarketWorstPricesUntilRoom(current, side, 1);
                if (current.Count < OperationalBookSideCap)
                    current.Add(lvl);
            }
        }

        InvalidateOfferIdx(side);
    }

    /// <summary>Rebuild uma vez se há delta pendente — usado pela thread dedicada de publicação.</summary>
    internal bool TryFlushSnapshotIfDirty(out BookSnapshot snapshot)
    {
        ulong genAtCopy;
        List<BookLevel> bidsCopy;
        List<BookLevel> asksCopy;

        lock (SyncRoot)
        {
            if (!_snapshotDirty)
            {
                snapshot = _snapshot;
                return false;
            }

            genAtCopy = _mutationGen;
            bidsCopy = new List<BookLevel>(_bids);
            asksCopy = new List<BookLevel>(_asks);
        }

        var built = BuildSortedSnapshot(_ticker, bidsCopy, asksCopy, DateTime.UtcNow);

        lock (SyncRoot)
        {
            _snapshot = built;
            snapshot = built;
            _snapshotDirty = genAtCopy != _mutationGen;
            return true;
        }
    }

    /// <summary>Livro lido por API; snapshot reflete o último rebuild após mutação.</summary>
    internal BookSnapshot GetSnapshotEnsuringFresh()
    {
        ulong genAtCopy;
        List<BookLevel> bidsCopy;
        List<BookLevel> asksCopy;

        lock (SyncRoot)
        {
            if (!_snapshotDirty)
                return _snapshot;

            genAtCopy = _mutationGen;
            bidsCopy = new List<BookLevel>(_bids);
            asksCopy = new List<BookLevel>(_asks);
        }

        var built = BuildSortedSnapshot(_ticker, bidsCopy, asksCopy, DateTime.UtcNow);

        lock (SyncRoot)
        {
            _snapshot = built;
            _snapshotDirty = genAtCopy != _mutationGen;
            return _snapshot;
        }
    }

    /// <summary>Sort/heap + agregação por preço; depois <see cref="BookSnapshotAggregation.NormalizeEconomicalTop"/> para topo econômico coerente.</summary>
    private static BookSnapshot BuildSortedSnapshot(string ticker, List<BookLevel> bids, List<BookLevel> asks, DateTime time)
    {
        bids.RemoveAll(static b => b.Price <= 0);
        asks.RemoveAll(static a => a.Price <= 0);

        // Incluir todos os níveis copiados; o trim aplicado depois usa VisibleBookLines (amplo para WIN).
        int kBid = bids.Count;
        int kAsk = asks.Count;

        var keyBids = new List<(BookLevel Lvl, int Seq)>();
        var keyAsks = new List<(BookLevel Lvl, int Seq)>();
        var dispBids = new List<BookLevel>();
        var dispAsks = new List<BookLevel>();

        if (bids.Count <= FullSortThreshold)
            SortAllBidsToDispLimited(bids, kBid, keyBids, dispBids);
        else
            HeapTopHighestBidsLimited(bids, kBid, keyBids, dispBids);

        if (asks.Count <= FullSortThreshold)
            SortAllAsksToDispLimited(asks, kAsk, keyAsks, dispAsks);
        else
            HeapTopLowestAsksLimited(asks, kAsk, keyAsks, dispAsks);

        // Book de ofertas individual (por corretora): capturado ANTES da agregação por preço,
        // enquanto dispBids/dispAsks ainda carregam Broker/OfferId reais de cada oferta —
        // mesma ordenação (preço, depois FIFO) do livro econômico. GetRange copia; o Clear+AddRange
        // da agregação logo abaixo não afeta essas listas.
        var rawBids = dispBids.Count > VisibleBookOrderLines
            ? dispBids.GetRange(0, VisibleBookOrderLines)
            : new List<BookLevel>(dispBids);
        var rawAsks = dispAsks.Count > VisibleBookOrderLines
            ? dispAsks.GetRange(0, VisibleBookOrderLines)
            : new List<BookLevel>(dispAsks);

        AggregateBookSideByPrice(dispBids, BookSide.Bid, ticker, time);
        AggregateBookSideByPrice(dispAsks, BookSide.Ask, ticker, time);

        // Livro econômico: melhor compra &lt; melhor venda.
        int preNormBids = dispBids.Count, preNormAsks = dispAsks.Count;
        BookSnapshotAggregation.NormalizeEconomicalTop(dispBids, dispAsks);
        int removedB = preNormBids - dispBids.Count, removedA = preNormAsks - dispAsks.Count;
        if ((removedB > 0 || removedA > 0) && _normalizeDiagCount < 20)
        {
            _normalizeDiagCount++;
            decimal topBid = preNormBids > 0 && dispBids.Count < preNormBids ? 0 : (dispBids.Count > 0 ? dispBids[0].Price : 0);
            decimal topAsk = preNormAsks > 0 && dispAsks.Count < preNormAsks ? 0 : (dispAsks.Count > 0 ? dispAsks[0].Price : 0);
            System.Diagnostics.Debug.WriteLine(
                $"[NORM DIAG] removedB={removedB} removedA={removedA} remainB={dispBids.Count} remainA={dispAsks.Count} topBid={topBid} topAsk={topAsk}");
        }

        TrimDispToVisible(dispBids, dispAsks, VisibleBookLines);

        return new BookSnapshot(
            Ticker: ticker,
            Bids:   dispBids.ToArray(),
            Asks:   dispAsks.ToArray(),
            Time:   time,
            RawBids: rawBids,
            RawAsks: rawAsks
        );
    }

    /// <summary>
    /// Livro UI por <b>nível de preço</b>; ver <see cref="BookSnapshotAggregation"/>.
    /// </summary>
    private static void AggregateBookSideByPrice(List<BookLevel> sorted, BookSide side, string ticker, DateTime timeUtc)
    {
        if (sorted.Count == 0)
            return;

        var aggregated = BookSnapshotAggregation.AggregateByPrice(sorted, side, ticker, timeUtc);
        sorted.Clear();
        sorted.AddRange(aggregated);
    }

    /// <summary>
    /// Mesmo preço: hora da oferta (FIFO) e, na falta dela, índice linear da DLL
    /// (melhor oferta no fim da lista → maior Seq na frente da fila).
    /// </summary>
    private static int CompareQueueAtSamePrice((BookLevel Lvl, int Seq) a, (BookLevel Lvl, int Seq) b)
    {
        bool aHasTime = a.Lvl.ExchangeTime is DateTime;
        bool bHasTime = b.Lvl.ExchangeTime is DateTime;
        if (aHasTime && bHasTime)
        {
            int t = a.Lvl.ExchangeTime!.Value.CompareTo(b.Lvl.ExchangeTime!.Value);
            if (t != 0) return t;
        }
        else if (aHasTime != bHasTime)
        {
            return aHasTime ? -1 : 1;
        }

        int s = b.Seq.CompareTo(a.Seq);
        if (s != 0) return s;
        return a.Lvl.OfferId.CompareTo(b.Lvl.OfferId);
    }

    private static void SortAllBidsToDispLimited(
        List<BookLevel> src, int k, List<(BookLevel Lvl, int Seq)> keyBids, List<BookLevel> dispBids)
    {
        keyBids.Clear();
        for (int i = 0; i < src.Count; i++)
            keyBids.Add((src[i], i));

        keyBids.Sort(static (a, b) =>
        {
            int c = b.Lvl.Price.CompareTo(a.Lvl.Price);
            if (c != 0) return c;
            return CompareQueueAtSamePrice(a, b);
        });

        dispBids.Clear();
        int n = Math.Min(k, keyBids.Count);
        for (int i = 0; i < n; i++)
            dispBids.Add(keyBids[i].Lvl);
    }

    private static void SortAllAsksToDispLimited(
        List<BookLevel> src, int k, List<(BookLevel Lvl, int Seq)> keyAsks, List<BookLevel> dispAsks)
    {
        keyAsks.Clear();
        for (int i = 0; i < src.Count; i++)
            keyAsks.Add((src[i], i));

        keyAsks.Sort(static (a, b) =>
        {
            int c = a.Lvl.Price.CompareTo(b.Lvl.Price);
            if (c != 0) return c;
            return CompareQueueAtSamePrice(a, b);
        });

        dispAsks.Clear();
        int n = Math.Min(k, keyAsks.Count);
        for (int i = 0; i < n; i++)
            dispAsks.Add(keyAsks[i].Lvl);
    }

    /// <summary>Min-heap em preço mantém os k melhores bids (maiores preços) em O(n log k).</summary>
    private static void HeapTopHighestBidsLimited(
        List<BookLevel> src, int k, List<(BookLevel Lvl, int Seq)> keyBids, List<BookLevel> dispBids)
    {
        var pq = new PriorityQueue<(BookLevel Lvl, int I), (decimal Px, int Idx)>(
            Comparer<(decimal Px, int Idx)>.Create((a, b) =>
            {
                int c = a.Px.CompareTo(b.Px);
                return c != 0 ? c : a.Idx.CompareTo(b.Idx);
            }));

        for (int i = 0; i < src.Count; i++)
        {
            BookLevel lvl = src[i];
            if (pq.Count < k)
                pq.Enqueue((lvl, i), (lvl.Price, i));
            else
            {
                pq.TryPeek(out _, out var worst);
                if (lvl.Price > worst.Px || (lvl.Price == worst.Px && i < worst.Idx))
                {
                    pq.Dequeue();
                    pq.Enqueue((lvl, i), (lvl.Price, i));
                }
            }
        }

        keyBids.Clear();
        while (pq.Count > 0)
        {
            var t = pq.Dequeue();
            keyBids.Add((t.Lvl, t.I));
        }

        keyBids.Sort(static (a, b) =>
        {
            int c = b.Lvl.Price.CompareTo(a.Lvl.Price);
            if (c != 0) return c;
            return CompareQueueAtSamePrice(a, b);
        });

        dispBids.Clear();
        for (int i = 0; i < keyBids.Count; i++)
            dispBids.Add(keyBids[i].Lvl);
    }

    /// <summary>Max-heap em preço (via comparer inverso) mantém os k melhores asks (menores preços).</summary>
    private static void HeapTopLowestAsksLimited(
        List<BookLevel> src, int k, List<(BookLevel Lvl, int Seq)> keyAsks, List<BookLevel> dispAsks)
    {
        var pq = new PriorityQueue<(BookLevel Lvl, int I), (decimal Px, int Idx)>(
            Comparer<(decimal Px, int Idx)>.Create((a, b) =>
            {
                int c = b.Px.CompareTo(a.Px);
                return c != 0 ? c : b.Idx.CompareTo(a.Idx);
            }));

        for (int i = 0; i < src.Count; i++)
        {
            BookLevel lvl = src[i];
            if (pq.Count < k)
                pq.Enqueue((lvl, i), (lvl.Price, i));
            else
            {
                pq.TryPeek(out _, out var worstHighest);
                if (lvl.Price < worstHighest.Px || (lvl.Price == worstHighest.Px && i < worstHighest.Idx))
                {
                    pq.Dequeue();
                    pq.Enqueue((lvl, i), (lvl.Price, i));
                }
            }
        }

        keyAsks.Clear();
        while (pq.Count > 0)
        {
            var t = pq.Dequeue();
            keyAsks.Add((t.Lvl, t.I));
        }

        keyAsks.Sort(static (a, b) =>
        {
            int c = a.Lvl.Price.CompareTo(b.Lvl.Price);
            if (c != 0) return c;
            return CompareQueueAtSamePrice(a, b);
        });

        dispAsks.Clear();
        for (int i = 0; i < keyAsks.Count; i++)
            dispAsks.Add(keyAsks[i].Lvl);
    }

    private static void TrimDispToVisible(List<BookLevel> bidsSortedDesc, List<BookLevel> asksSortedAsc, int visible)
    {
        if (bidsSortedDesc.Count > visible)
            bidsSortedDesc.RemoveRange(visible, bidsSortedDesc.Count - visible);

        if (asksSortedAsc.Count > visible)
            asksSortedAsc.RemoveRange(visible, asksSortedAsc.Count - visible);
    }
}