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

    /// <summary>Faz rebuild + Iceberg/recorder/UI fora da thread da DLL para não sufocar a fila de ofertas.</summary>
    private Thread? _snapshotPublishThread;
    private int _snapshotPublishStarted;

    /// <summary>Spoof/Renewable rodam aqui para não competir com Apply do livro na thread do provider.</summary>
    private readonly ConcurrentQueue<BookLevel> _detectorLevelQueue = new();
    private Thread? _detectorDrainThread;
    private int _detectorDrainStarted;

    private const int DetectorQueueSoftCap = 400_000;

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
        => _books.TryGetValue(ticker, out var state) ? state.GetSnapshotEnsuringFresh() : null;

    private void HandleTrade(TradeEvent trade)
    {
        try
        {
            Exhaustion.ProcessarTrade(trade);

            if (_recordingEnabled && _recorder != null)
                _ = _recorder.GravarTradeAsync(ExtrairAtivo(trade.Ticker), trade);

            try
            {
                OnTrade?.Invoke(trade);
            }
            catch (Exception subEx)
            {
                LogEngineFault(nameof(HandleTrade) + ".OnTrade", subEx);
            }
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
            if (!_books.TryGetValue(level.Ticker, out var state)) return;

            lock (state.SyncRoot)
                state.ApplyBookDeltaUnsafe(level);

            if (_detectorLevelQueue.Count > DetectorQueueSoftCap)
            {
                while (_detectorLevelQueue.Count > DetectorQueueSoftCap / 2)
                    _detectorLevelQueue.TryDequeue(out _);
            }

            _detectorLevelQueue.Enqueue(level);
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
            if (!_books.TryGetValue(e.Ticker, out var state)) return;

            lock (state.SyncRoot)
                state.ReplaceFullBookUnsafe(e.Bids, e.Asks);

            if (_detectorLevelQueue.Count > DetectorQueueSoftCap)
            {
                while (_detectorLevelQueue.Count > DetectorQueueSoftCap / 2)
                    _detectorLevelQueue.TryDequeue(out _);
            }

            foreach (var lvl in e.Bids)
                _detectorLevelQueue.Enqueue(lvl);
            foreach (var lvl in e.Asks)
                _detectorLevelQueue.Enqueue(lvl);
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
                while (n < 8192 && _detectorLevelQueue.TryDequeue(out var lvl))
                {
                    n++;
                    try
                    {
                        Spoof.ProcessLevel(lvl, _lastPrice);
                        Renewable.ProcessLevel(lvl);
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

    public void Dispose()
    {
        _cts.Cancel();
        _snapshotPublishThread?.Join(TimeSpan.FromMilliseconds(2500));
        _detectorDrainThread?.Join(TimeSpan.FromMilliseconds(2500));
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
    //   0 = atAdd        → Inserir APÓS posição
    //   1 = atEdit       → Atualizar na posição
    //   2 = atDelete     → Deletar na posição
    //   3 = atDeleteFrom → Remover TODAS a partir da posição
    //   4 = atFullBook   → (tratado no provider, gera action=5 por entrada)
    //   5 = FullBookAdd  → Adiciona direto ao final (sem calcular nPosition)
    //
    // Side: Compra=0 (BID), Venda=1 (ASK)
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Limite do modelo incremental (lista interna DLL) — não confundir com linhas na tela.</summary>
    private const int MAX_LEVELS = 10000;

    /// <summary>Linhas por lado no snapshot. WIN pode ter centenas de ofertas nos melhores preços; 300 cortava no meio da fila.</summary>
    private const int VisibleBookLines = 2500;

    /// <summary>Espaço extra só para NormalizeCrossSpread descascar cruces sem esvaziar o snapshot.</summary>
    private const int CrossSpreadRebuildSlack = 40;

    /// <summary>Sempre ordenação completa: heap com k=n era ok, mas sort total evita qualquer surpresa com livros grandes.</summary>
    private const int FullSortThreshold = int.MaxValue;

    private readonly List<BookLevel> _bids = new();
    private readonly List<BookLevel> _asks = new();

    private BookSnapshot _snapshot;
    private bool _snapshotDirty;

    /// <summary>Incrementado a cada delta — detecta se o livro mudou durante rebuild fora do lock.</summary>
    private ulong _mutationGen;

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

    /// <summary>Inserir APÓS a posição incremental (manual Nelogica): índice = size - Pos - 1, depois +1.</summary>
    private static int InsertIndexAfterNPosition(int listCount, int nPosition)
    {
        int realIdx = listCount - nPosition - 1;
        return Math.Clamp(realIdx + 1, 0, listCount);
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

    /// <summary>Mutação das listas internas sob <see cref="SyncRoot"/> (rebuild sai em thread de snapshot).</summary>
    internal void ApplyBookDeltaUnsafe(BookLevel level)
    {
        try
        {
            var list = level.Side == BookSide.Bid ? _bids : _asks;

            if (level.Volume == -1)
            {
                list.Clear();
                InvalidateOfferIdx(level.Side);
                return;
            }

            EnsureOfferIdx(level.Side);

            if (level.Action == 5)
            {
                if (list.Count < MAX_LEVELS)
                    list.Add(level);

                InvalidateOfferIdx(level.Side);
                return;
            }

            int realIdx = list.Count - level.Position - 1;

            switch (level.Action)
            {
                case 0:
                    if (level.OfferId > 0 && TryOfferIdx(level.Side, level.OfferId, out int existingIdx))
                    {
                        int tgt = InsertIndexAfterNPosition(list.Count, level.Position);
                        if (existingIdx != tgt)
                        {
                            var prev = list[existingIdx];
                            list.RemoveAt(existingIdx);
                            InvalidateOfferIdx(level.Side);
                            EnsureOfferIdx(level.Side);
                            tgt = InsertIndexAfterNPosition(list.Count, level.Position);
                            list.Insert(tgt, CoalesceBrokerFromPrevious(level, prev));
                            if (list.Count > MAX_LEVELS)
                                list.RemoveAt(list.Count - 1);
                            InvalidateOfferIdx(level.Side);
                            break;
                        }

                        list[existingIdx] = CoalesceBrokerFromPrevious(level, list[existingIdx]);
                        break;
                    }

                    {
                        int insertIdx = InsertIndexAfterNPosition(list.Count, level.Position);
                        list.Insert(insertIdx, level);
                        if (list.Count > MAX_LEVELS)
                            list.RemoveAt(list.Count - 1);
                        InvalidateOfferIdx(level.Side);
                        break;
                    }

                case 1:
                    if (level.OfferId > 0 && TryOfferIdx(level.Side, level.OfferId, out int editIdx))
                    {
                        if (realIdx >= 0 && realIdx < list.Count && editIdx != realIdx)
                        {
                            var prev = list[editIdx];
                            list.RemoveAt(editIdx);
                            InvalidateOfferIdx(level.Side);
                            EnsureOfferIdx(level.Side);
                            int tgt = Math.Clamp(list.Count - level.Position - 1, 0, list.Count);
                            list.Insert(tgt, CoalesceBrokerFromPrevious(level, prev));
                            if (list.Count > MAX_LEVELS)
                                list.RemoveAt(list.Count - 1);
                            InvalidateOfferIdx(level.Side);
                            break;
                        }

                        list[editIdx] = CoalesceBrokerFromPrevious(level, list[editIdx]);
                        break;
                    }

                    if (realIdx >= 0 && realIdx < list.Count)
                    {
                        list[realIdx] = CoalesceBrokerFromPrevious(level, list[realIdx]);
                        InvalidateOfferIdx(level.Side);
                    }

                    break;

                case 2:
                    if (level.OfferId > 0 && TryOfferIdx(level.Side, level.OfferId, out int delIdx))
                    {
                        list.RemoveAt(delIdx);
                        InvalidateOfferIdx(level.Side);
                        break;
                    }

                    if (realIdx >= 0 && realIdx < list.Count)
                    {
                        list.RemoveAt(realIdx);
                        InvalidateOfferIdx(level.Side);
                    }

                    break;

                case 3:
                    if (list.Count == 0) break;
                    if (level.Position < 0) break;

                    // Sem esvaziar o lado inteiro: se nPosition ficou "além" do nosso tamanho local
                    // (defasagem/atraso), realIdx < 0 — antigo Math.Max(0, realIdx) zerava e apagava TUDO.
                    int removeFrom = list.Count - level.Position - 1;
                    if (removeFrom < 0 || removeFrom >= list.Count) break;

                    list.RemoveRange(removeFrom, list.Count - removeFrom);
                    InvalidateOfferIdx(level.Side);
                    break;
            }
        }
        finally
        {
            unchecked { _mutationGen++; }
            _snapshotDirty = true;
        }
    }

    /// <summary>Substitui os dois lados pelo snapshot oficial da DLL (atFullBook).</summary>
    internal void ReplaceFullBookUnsafe(IReadOnlyList<BookLevel> bids, IReadOnlyList<BookLevel> asks)
    {
        _bids.Clear();
        _asks.Clear();
        foreach (var lvl in bids)
        {
            if (_bids.Count >= MAX_LEVELS) break;
            _bids.Add(lvl);
        }

        foreach (var lvl in asks)
        {
            if (_asks.Count >= MAX_LEVELS) break;
            _asks.Add(lvl);
        }

        RebuildAllOfferIndexes();
        unchecked { _mutationGen++; }
        _snapshotDirty = true;
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

    /// <summary>
    /// Livro oficial não “cruza”: melhor compra ≤ melhor venda após atualizações parciais
    /// às vezes existe lixo até o próximo ciclo da DLL — remove entradas claramente inválidas.
    /// </summary>
    /// <remarks>
    /// bids: maior→menor; asks: menor→maior.
    /// Cruza quando melhor compra &gt; melhor venda. Igual quando melhor compra == melhor venda
    /// em snapshot incremental costuma ser lixo/visual — remove topo de venda até desencostar (próximo ask).
    /// </remarks>
    private static void NormalizeCrossSpread(List<BookLevel> bidsSortedDesc, List<BookLevel> asksSortedAsc)
    {
        if (bidsSortedDesc.Count == 0 || asksSortedAsc.Count == 0) return;

        decimal bestBid0 = bidsSortedDesc[0].Price;
        decimal bestAsk0 = asksSortedAsc[0].Price;
        // Espalhamento válido ou só grudado em 1 tick: nada agressivo.
        if (bestBid0 < bestAsk0 || bestBid0 == bestAsk0)
            return;

        const int safety = 512;
        for (int n = 0; n < safety; n++)
        {
            if (bidsSortedDesc.Count == 0 || asksSortedAsc.Count == 0)
            {
                // Antes: RestoreLists reintroduzia níveis inválidos (ex. ask a 0) quando o descruzamento
                // removia todo um lado — o book voltava desordenado/cruzado. Melhor publicar um lado
                // limpo ou vazio até o próximo atFullBook/deltas da DLL.
                return;
            }

            decimal bestBid = bidsSortedDesc[0].Price;
            decimal bestAsk = asksSortedAsc[0].Price;

            if (bestBid < bestAsk) return;

            if (bestBid > bestAsk)
            {
                int rm = asksSortedAsc.RemoveAll(a => a.Price < bestBid);
                if (rm > 0) continue;

                rm = bidsSortedDesc.RemoveAll(b => b.Price > bestAsk);
                if (rm > 0) continue;

                bidsSortedDesc.RemoveAt(0);
                continue;
            }

            return;
        }
    }

    /// <summary>Sort/heap e normalização fora do <see cref="SyncRoot"/> para não travar deltas da DLL.</summary>
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

        NormalizeCrossSpread(dispBids, dispAsks);
        TrimDispToVisible(dispBids, dispAsks, VisibleBookLines);

        return new BookSnapshot(
            Ticker: ticker,
            Bids:   dispBids.ToArray(),
            Asks:   dispAsks.ToArray(),
            Time:   time
        );
    }

    /// <summary>Mesmo preço: ordem = lista interna (semântica incremental da DLL). Ordenar por ExchangeTime misturava
    /// linhas do atFullBook (sem data) com deltas (com data) e embaralhava a fila vs ProfitChart.</summary>
    private static int CompareQueueAtSamePrice((BookLevel Lvl, int Seq) a, (BookLevel Lvl, int Seq) b)
    {
        int s = a.Seq.CompareTo(b.Seq);
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