using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace MarketCore.HistoricalImporter;

/// <summary>Integração histórico de negócios ProfitDLL64 (<c>GetHistoryTrades</c> e export by-period tipado).</summary>
public sealed class ProfitHistoryService : IDisposable
{
    private DLLNewHistoryCallback? _historyCallback;

    private readonly IProfitHistoryTradeSink _sink;
    private bool _disposed;
    private static NativeHistoryByPeriodFn? s_historyByPeriod;
    private static GetHistoryTradesFn? s_getHistoryTrades;
    private static nint s_dllModule;
    private static volatile bool s_optionalSubscribeTried;
    private static int s_historySampleLogs;
    private static readonly object StaticSync = new();
    private static long s_historyTicks;

    private static readonly string[] HistoryExchanges = ["F", "B", "BMF"];

    private const int HistoryWaitMaxMs = 90_000;
    private const int HistoryQuietMs = 2_000;
    /// <summary>Limite por chamada a <c>GetHistoryTrades</c>: a DLL pode bloquear indefinidamente mal o servidor falhe.</summary>
    public static TimeSpan NativeGetHistoryTradesCallTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Dias máximos por cada chamada a <c>GetHistoryTrades</c>. O manual ProfitDLL não fixa limite de período;
    /// na prática o servidor/licença pode falhar ou devolver vazio para intervalos grandes (ex.: ~8 dias).
    /// Podes aumentar ou diminuir antes de <see cref="RequestHistoricalData"/> conforme testes na tua conta.
    /// </summary>
    public static int MaxHistoryChunkDays { get; set; } = 7;

    public ProfitHistoryService(IProfitHistoryTradeSink sink)
    {
        _sink = sink;
        _historyCallback = OnDllNewHistoryTypedTrade;
    }

    /// <returns>Código retornado pela DLL no último pedido (0 costuma ser OK — ver manual Nelogica).</returns>
    public int RequestHistoricalData(string symbol, DateTime start, DateTime end) =>
        RequestHistoricalDataCore(symbol, start, end, useAsyncWait: false, CancellationToken.None)
            .GetAwaiter().GetResult();

    /// <inheritdoc cref="RequestHistoricalData"/>
    /// <remarks>Use no WPF: não bloqueia o dispatcher durante a espera (importante para a DLL).</remarks>
    public Task<int> RequestHistoricalDataAsync(
        string symbol,
        DateTime start,
        DateTime end,
        CancellationToken cancellationToken = default) =>
        RequestHistoricalDataCore(symbol, start, end, useAsyncWait: true, cancellationToken);

    private async Task<int> RequestHistoricalDataCore(
        string symbol,
        DateTime start,
        DateTime end,
        bool useAsyncWait,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        if (end < start)
            throw new ArgumentException("Data fim < início.");

        string sym = symbol.Trim().ToUpperInvariant();
        _sink.SetCurrentContract(sym);

        int startInt = ToYyyyMmDd(start);
        int endInt = ToYyyyMmDd(end);

        var bridge = new ProviderHistoryBridge(_sink);
        bridge.Subscribe();
        ProfitHistoryRelay.BeginHistoricalDownloadScope();
        Action<string, int>? onProg = static (tk, p) =>
        {
            if (p < 0) return;
            ProfitDllDiag.Append($"[Progress] {tk} {p}%");
        };
        ProfitHistoryRelay.HistoryProgress += onProg;
        try
        {
            Interlocked.Exchange(ref s_historySampleLogs, 0);

            long tickBefore = Volatile.Read(ref s_historyTicks);
            int lastRc = 0;
            bool haveGet = TryEnsureGetHistoryTradesLoaded();

            if (haveGet)
            {
                lastRc = await RequestViaGetHistoryTradesAllAsync(sym, start, end, useAsyncWait, cancellationToken)
                    .ConfigureAwait(false);
                ProfitDllDiag.Append(
                    $"{sym}: GetHistoryTrades fim rc={lastRc}; ticksΔ={Volatile.Read(ref s_historyTicks) - tickBefore}");
            }

            bool haveByPeriod = TryEnsureNativeHistoryByPeriodLoadedStrict();
            if (Volatile.Read(ref s_historyTicks) == tickBefore && haveByPeriod)
            {
                try
                {
                    lastRc = await Task.Run(() => s_historyByPeriod!(sym, startInt, endInt, _historyCallback!))
                        .WaitAsync(TimeSpan.FromMinutes(10), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    ProfitDllDiag.Append($"{sym}: DLLNewHistoryTypedTradesByPeriod TIMEOUT (10min)");
                }

                ProfitDllDiag.Append($"{sym}: fallback DLLNewHistoryTypedTradesByPeriod rc={lastRc}");
            }

            if (!haveGet && !haveByPeriod)
                throw new MissingMethodException(
                    "ProfitDLL64.dll: não foi encontrado GetHistoryTrades nem export DLLNewHistoryTypedTradesByPeriod.");

            return lastRc;
        }
        finally
        {
            ProfitHistoryRelay.HistoryProgress -= onProg;
            bridge.Unsubscribe();
            ProfitHistoryRelay.EndHistoricalDownloadScope();
        }
    }

    private static bool EnsureDllModuleLoaded()
    {
        if (s_dllModule != 0)
            return true;

        string path = ProfitDllDiag.ResolveProfitDllFullPathOrName();
        if (!NativeLibrary.TryLoad(path, out nint h))
            throw new DllNotFoundException($"Não foi possível carregar ProfitDLL64 ({path}).");

        s_dllModule = h;
        ProfitDllDiag.Append($"[ProfitHistoryService] NativeLibrary load \"{path}\" handle=0x{(ulong)h:X}");
        return true;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int OptionalSubscribeLikeFn(
        [MarshalAs(UnmanagedType.LPWStr)] string ticker,
        [MarshalAs(UnmanagedType.LPWStr)] string bolsa);

    /// <summary>Tenta exports opcionais (varia por build da DLL) antes de <c>GetHistoryTrades</c>.</summary>
    private static void TryOptionalSubscribeHistoryOnce()
    {
        if (s_optionalSubscribeTried)
            return;
        s_optionalSubscribeTried = true;

        try
        {
            EnsureDllModuleLoaded();
        }
        catch
        {
            return;
        }

        string[] exportNames =
        [
            "SubscribeAdjustHistory",
            "SubscribeAdjustedHistory",
            "SubscribeHistoryTicker",
            "SubscribeHistory"
        ];

        foreach (string export in exportNames)
        {
            if (!NativeLibrary.TryGetExport(s_dllModule, export, out nint fn))
                continue;

            try
            {
                var d = Marshal.GetDelegateForFunctionPointer<OptionalSubscribeLikeFn>(fn);
                foreach (string ex in HistoryExchanges)
                {
                    int r = d("WINFUT", ex);
                    ProfitDllDiag.Append($"optional {export} WINFUT/{ex} rc={r}");
                }
            }
            catch (Exception ex)
            {
                ProfitDllDiag.Append($"optional {export} ex: {ex.Message}");
            }
        }
    }

    /// <summary>Export by-period: só nomes documentados (<c>DLLNewHistoryTypedTradesByPeriod*</c>),
    /// para não ligar um export compatível pelo nome mas com assinatura errada.</summary>
    private static bool TryEnsureNativeHistoryByPeriodLoadedStrict()
    {
        if (s_historyByPeriod != null)
            return true;

        EnsureDllModuleLoaded();

        string[] candidates =
        [
            "DLLNewHistoryTypedTradesByPeriod",
            "DLLNewHistoryTypedTradesByPeriodW",
            "DLLNewHistoryTypedTradesByPeriodA",
            "_DLLNewHistoryTypedTradesByPeriod@16"
        ];

        foreach (string name in candidates)
        {
            if (!NativeLibrary.TryGetExport(s_dllModule, name, out nint fn))
                continue;

            s_historyByPeriod = Marshal.GetDelegateForFunctionPointer<NativeHistoryByPeriodFn>(fn);
            ProfitDllDiag.Append($"Export by-period resolvido: {name}");
            return true;
        }

        return false;
    }

    private static bool TryEnsureGetHistoryTradesLoaded()
    {
        if (s_getHistoryTrades != null)
            return true;

        EnsureDllModuleLoaded();

        string[] names =
        [
            "GetHistoryTrades",
            "GetHistoryTradesW",
            "_GetHistoryTrades@16"
        ];

        foreach (string name in names)
        {
            if (!NativeLibrary.TryGetExport(s_dllModule, name, out nint fn))
                continue;
            s_getHistoryTrades = Marshal.GetDelegateForFunctionPointer<GetHistoryTradesFn>(fn);
            ProfitDllDiag.Append($"GetHistoryTrades delegate ligado ao export: {name}");
            return true;
        }

        return false;
    }

    private async Task<int> RequestViaGetHistoryTradesAllAsync(
        string symbol,
        DateTime start,
        DateTime end,
        bool useAsyncWait,
        CancellationToken ct)
    {
        int maxDays = Math.Clamp(MaxHistoryChunkDays, 1, 366);
        List<(DateTime From, DateTime To)> chunks = SplitHistoryRange(start, end, maxDays);
        ProfitDllDiag.Append(
            $"GetHistoryTrades em {chunks.Count} janela(s) de até {maxDays} dia(s) cada ({start:yyyy-MM-dd} → {end:yyyy-MM-dd})");

        int lastRc = 0;
        for (int i = 0; i < chunks.Count; i++)
        {
            var (c0, c1) = chunks[i];
            ProfitDllDiag.Append($"  janela [{i + 1}/{chunks.Count}] {c0:dd/MM/yyyy HH:mm:ss} → {c1:dd/MM/yyyy HH:mm:ss}");

            var datePairs = BuildDateRangePairs(c0, c1);
            long baseline = Volatile.Read(ref s_historyTicks);

            string primary = symbol.Trim().ToUpperInvariant();
            lastRc = await RequestGetHistoryWorkAsync(primary, datePairs, useAsyncWait, ct).ConfigureAwait(false);

            if (Volatile.Read(ref s_historyTicks) == baseline
                && !primary.Equals("WINFUT", StringComparison.OrdinalIgnoreCase))
                lastRc = await RequestGetHistoryWorkAsync("WINFUT", datePairs, useAsyncWait, ct).ConfigureAwait(false);
        }

        return lastRc;
    }

    /// <summary>
    /// Parte o intervalo em blocos sobre o calendário: no máximo <paramref name="maxDaysPerChunk"/> dias por bloco,
    /// do instante inicial ao fim do último dia do bloco (ou <paramref name="end"/>), depois avança para o dia seguinte.
    /// </summary>
    private static List<(DateTime From, DateTime To)> SplitHistoryRange(DateTime start, DateTime end, int maxDaysPerChunk)
    {
        var list = new List<(DateTime, DateTime)>();
        DateTime cursor = start;
        while (cursor <= end)
        {
            DateTime lastCalendarDay = cursor.Date.AddDays(maxDaysPerChunk - 1);
            DateTime endOfLastDay = lastCalendarDay.Date.AddDays(1).AddMilliseconds(-1);
            DateTime chunkEnd = endOfLastDay < end ? endOfLastDay : end;
            list.Add((cursor, chunkEnd));
            if (chunkEnd >= end)
                break;

            cursor = chunkEnd.Date.AddDays(1);
            if (cursor <= chunkEnd)
                cursor = chunkEnd.AddMilliseconds(1);
        }

        return list;
    }

    private static List<(string Start, string End)> BuildDateRangePairs(DateTime start, DateTime end)
    {
        DateTime dayStart = start.Date;
        DateTime dayEnd = end.Date.AddDays(1).AddMilliseconds(-1);
        DateTime clippedStart = start > dayStart ? start : dayStart;

        // Manual: PWideChar + formato "DD/MM/YYYY HH:mm:SS" — prioridade sem fração de segundo.
        string ddStartNoMs = clippedStart.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        string ddEndNoMs = dayEnd.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);

        string ddStart = clippedStart.ToString("dd/MM/yyyy HH:mm:ss.fff", CultureInfo.InvariantCulture);
        string ddEnd = dayEnd.ToString("dd/MM/yyyy HH:mm:ss.fff", CultureInfo.InvariantCulture);
        string isoStartNoMs = clippedStart.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        string isoEndNoMs = dayEnd.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        string isoStart = clippedStart.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        string isoEnd = dayEnd.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

        return
        [
            (ddStartNoMs, ddEndNoMs),
            (ddStart, ddEnd),
            (isoStartNoMs, isoEndNoMs),
            (isoStart, isoEnd)
        ];
    }

    private static async Task<int> CallGetHistoryTradesWithTimeoutAsync(
        string ticker,
        string ex,
        string startStr,
        string endStr,
        CancellationToken ct)
    {
        TimeSpan budget = NativeGetHistoryTradesCallTimeout;
        if (budget < TimeSpan.FromSeconds(15))
            budget = TimeSpan.FromSeconds(15);
        else if (budget > TimeSpan.FromMinutes(15))
            budget = TimeSpan.FromMinutes(15);

        GetHistoryTradesFn fn = s_getHistoryTrades!;
        Task<int> work = Task.Run(() => fn(ticker, ex, startStr, endStr));

        try
        {
            return await work.WaitAsync(budget, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            ProfitDllDiag.Append(
                $"GetHistoryTrades TIMEOUT {budget.TotalSeconds:F0}s ticker={ticker} ex={ex} start={startStr} end={endStr}");
            throw;
        }
    }

    private async Task<int> RequestGetHistoryWorkAsync(
        string ticker,
        List<(string Start, string End)> datePairs,
        bool useAsyncWait,
        CancellationToken ct)
    {
        TryOptionalSubscribeHistoryOnce();

        int lastRc = 0;
        foreach (string ex in HistoryExchanges)
        {
            long tickRound = Volatile.Read(ref s_historyTicks);
            int rsT = SubscribeTicker(ticker, ex);
            int rsO = SubscribeOfferBook(ticker, ex);
            ProfitDllDiag.Append($"SubscribeTicker({ticker},{ex}) rc={rsT} SubscribeOfferBook rc={rsO}");

            try
            {
                foreach (var pair in datePairs)
                {
                    long tickBefore = Volatile.Read(ref s_historyTicks);
                    try
                    {
                        lastRc = await CallGetHistoryTradesWithTimeoutAsync(ticker, ex, pair.Start, pair.End, ct)
                            .ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        continue;
                    }

                    ProfitDllDiag.Append($"GetHistoryTrades ticker={ticker} ex={ex} rc={lastRc} start={pair.Start} end={pair.End}");

                    await WaitHistoryQuietAsync(tickBefore, HistoryWaitMaxMs, HistoryQuietMs, useAsyncWait, ct).ConfigureAwait(false);

                    if (Volatile.Read(ref s_historyTicks) > tickBefore)
                    {
                        ProfitDllDiag.Append($"{ticker}/{ex}: recebidos callbacks (Δticks)");
                        return lastRc;
                    }
                }
            }
            finally
            {
                _ = UnsubscribeOfferBook(ticker, ex);
                _ = UnsubscribeTicker(ticker, ex);
            }

            if (Volatile.Read(ref s_historyTicks) > tickRound)
                return lastRc;
        }

        return lastRc;
    }

    /// <summary>Espera após bursts de ticks; só considera “silêncio” depois do primeiro novo tick.</summary>
    private static async Task WaitHistoryQuietAsync(
        long tickAtStart,
        int maxWaitMs,
        int quietWindowMs,
        bool useAsyncWait,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastSeen = tickAtStart;
        long lastChangeAt = -1;

        while (sw.ElapsedMilliseconds < maxWaitMs)
        {
            ct.ThrowIfCancellationRequested();
            long now = Volatile.Read(ref s_historyTicks);
            if (now != lastSeen)
            {
                lastSeen = now;
                lastChangeAt = sw.ElapsedMilliseconds;
            }
            else if (lastChangeAt >= 0 && sw.ElapsedMilliseconds - lastChangeAt >= quietWindowMs)
                return;

            if (useAsyncWait)
                await Task.Delay(120, ct).ConfigureAwait(false);
            else
                Thread.Sleep(120);
        }
    }

    private static int ToYyyyMmDd(DateTime dt)
    {
        dt = dt.Date;
        return dt.Year * 10_000 + dt.Month * 100 + dt.Day;
    }

    private void OnDllNewHistoryTypedTrade(
        int opType,
        string symbol,
        string date,
        string time,
        double price,
        int qty,
        int tradeNum,
        int buyOrder,
        int sellOrder,
        string buyBroker,
        string sellBroker,
        int aggressor)
    {
        _sink.OnHistoricalTrade(
            opType, symbol, date, time, price, qty, tradeNum,
            buyOrder, sellOrder, buyBroker, sellBroker, aggressor);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _historyCallback = null;
    }

    [DllImport("ProfitDLL64.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private static extern int SubscribeTicker(
        [MarshalAs(UnmanagedType.LPWStr)] string ticker,
        [MarshalAs(UnmanagedType.LPWStr)] string bolsa);

    [DllImport("ProfitDLL64.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private static extern int UnsubscribeTicker(
        [MarshalAs(UnmanagedType.LPWStr)] string ticker,
        [MarshalAs(UnmanagedType.LPWStr)] string bolsa);

    [DllImport("ProfitDLL64.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private static extern int SubscribeOfferBook(
        [MarshalAs(UnmanagedType.LPWStr)] string ticker,
        [MarshalAs(UnmanagedType.LPWStr)] string bolsa);

    [DllImport("ProfitDLL64.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private static extern int UnsubscribeOfferBook(
        [MarshalAs(UnmanagedType.LPWStr)] string ticker,
        [MarshalAs(UnmanagedType.LPWStr)] string bolsa);

    private sealed class ProviderHistoryBridge
    {
        private readonly IProfitHistoryTradeSink _sink;
        private Action<string, string, uint, double, double, int, int, int, int>? _handler;

        public ProviderHistoryBridge(IProfitHistoryTradeSink sink) => _sink = sink;

        public void Subscribe()
        {
            lock (StaticSync)
            {
                _handler = OnHistory;
                ProfitHistoryRelay.NativeHistoryTrade += _handler;
            }
        }

        public void Unsubscribe()
        {
            lock (StaticSync)
            {
                if (_handler != null)
                {
                    ProfitHistoryRelay.NativeHistoryTrade -= _handler;
                    _handler = null;
                }
            }
        }

        private void OnHistory(
            string symbol,
            string date,
            uint tradeNumber,
            double price,
            double vol,
            int qtd,
            int buyAgent,
            int sellAgent,
            int tradeType)
        {
            Interlocked.Increment(ref s_historyTicks);

            int sn = Interlocked.Increment(ref s_historySampleLogs);
            if (sn <= 8)
                ProfitDllDiag.Append(
                    $"[HistoryCb#{sn}] sym={symbol} date={date} pr={price} vol={vol} qtd={qtd} agents={buyAgent}/{sellAgent}");

            string yyyymmdd;
            string hhmmssfff;
            if (!TrySplitDate(date, out yyyymmdd, out hhmmssfff))
            {
                yyyymmdd = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                hhmmssfff = "000000000";
            }

            int qtyFromDll = qtd;
            if (qtyFromDll <= 0)
            {
                qtyFromDll = vol > 0 ? Math.Max(1, (int)Math.Round(vol, MidpointRounding.AwayFromZero)) : 1;
            }

            _sink.OnHistoricalTrade(
                tradeType,
                symbol,
                yyyymmdd,
                hhmmssfff,
                price,
                qtyFromDll,
                unchecked((int)tradeNumber),
                0,
                0,
                buyAgent.ToString(CultureInfo.InvariantCulture),
                sellAgent.ToString(CultureInfo.InvariantCulture),
                tradeType);
        }
    }

    private static bool TrySplitDate(string date, out string yyyymmdd, out string hhmmssfff)
    {
        yyyymmdd = "";
        hhmmssfff = "";
        if (string.IsNullOrWhiteSpace(date))
            return false;

        string[] formats =
        [
            "dd/MM/yyyy HH:mm:ss.fff",
            "dd/MM/yyyy HH:mm:ss",
            "yyyy-MM-dd HH:mm:ss.fff",
            "yyyy-MM-dd HH:mm:ss"
        ];

        if (!DateTime.TryParseExact(date.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            && !DateTime.TryParse(date.Trim(), CultureInfo.GetCultureInfo("pt-BR"), DateTimeStyles.None, out dt))
            return false;

        yyyymmdd = dt.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        hhmmssfff = dt.ToString("HHmmssfff", CultureInfo.InvariantCulture);
        return true;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public delegate void DLLNewHistoryCallback(
        int opType,
        [MarshalAs(UnmanagedType.LPWStr)] string symbol,
        [MarshalAs(UnmanagedType.LPWStr)] string date,
        [MarshalAs(UnmanagedType.LPWStr)] string time,
        double price,
        int qty,
        int tradeNum,
        int buyOrder,
        int sellOrder,
        [MarshalAs(UnmanagedType.LPWStr)] string buyBroker,
        [MarshalAs(UnmanagedType.LPWStr)] string sellBroker,
        int aggressor);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int NativeHistoryByPeriodFn(
        [MarshalAs(UnmanagedType.LPWStr)] string symbol,
        int start,
        int end,
        DLLNewHistoryCallback callback);

    /// <summary>
    /// Manual ProfitDLL: <c>GetHistoryTrades</c> usa <b>PWideChar</b> → manter <see cref="CharSet.Unicode"/>.
    /// Não usar <c>CharSet.Ansi</c> com strings geridas: corrompe o marshalling.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int GetHistoryTradesFn(
        [MarshalAs(UnmanagedType.LPWStr)] string ticker,
        [MarshalAs(UnmanagedType.LPWStr)] string bolsa,
        [MarshalAs(UnmanagedType.LPWStr)] string dtDateStart,
        [MarshalAs(UnmanagedType.LPWStr)] string dtDateEnd);
}
