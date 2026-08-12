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
    private static readonly SemaphoreSlim s_dllGate = new(1, 1);
    private static long s_historyTicks;
    /// <summary>
    /// Última % de progresso conhecida para o ticker em download (case-insensitive).
    /// Atualizada pelo callback <c>Progress</c> e usada por <see cref="WaitHistoryQuietAsync"/>
    /// para sair imediatamente quando a DLL sinaliza 100%.
    /// </summary>
    private static volatile int s_lastProgressPct = -1;

    /// <summary>
    /// Bolsas para <c>GetHistoryTrades</c> (manual ProfitDLL):
    /// <c>"F"</c> = BM&amp;F (futuros), <c>"B"</c> = Bovespa (ações).
    /// A ordem de tentativa depende do ticker — ver <see cref="GetHistoryExchangesForTicker"/>.
    /// </summary>
    private const string BmfMonthLetters = "FGHJKMNQUVXZ";

    /// <summary>Prefixos de contratos negociados na BMF (mais longos primeiro onde importa).</summary>
    private static readonly string[] BmfInstrumentRoots =
    [
        "BGI", "CCM", "DAP", "FRC", "WIN", "WDO", "IND", "DOL", "WSP",
        "BIT", "ETH", "TF", "BG",
    ];

    /// <summary>
    /// Futuros BMF (contínuos *FUT, DI*, ou contrato com mês FGHJKMNQUVXZ + ano) → só bolsa <c>F</c>.
    /// Ação típica Bovespa (4 letras + dígito, ex. PETR4) → só <c>B</c>.
    /// Caso ambíguo → <c>F</c> depois <c>B</c> (comportamento legado).
    /// </summary>
    private static string[] GetHistoryExchangesForTicker(string sym)
    {
        if (IsBmfFuturesTicker(sym))
            return ["F"];
        if (IsTypicalBovespaEquityTicker(sym))
            return ["B"];
        return ["F", "B"];
    }

    private static bool IsBmfFuturesTicker(string sym)
    {
        if (sym.EndsWith("FUT", StringComparison.Ordinal))
            return true;
        // DI1, DI1F25, etc.
        if (sym.Length >= 3 && sym.StartsWith("DI", StringComparison.Ordinal) && char.IsAsciiDigit(sym[2]))
            return true;

        foreach (string root in BmfInstrumentRoots)
        {
            if (!sym.StartsWith(root, StringComparison.Ordinal))
                continue;
            ReadOnlySpan<char> rest = sym.AsSpan(root.Length);
            if (rest.Length < 2 || rest.Length > 4)
                continue;
            if (BmfMonthLetters.IndexOf(rest[0]) < 0)
                continue;
            ReadOnlySpan<char> yy = rest.Slice(1);
            bool allDigits = true;
            foreach (char c in yy)
            {
                if (!char.IsAsciiDigit(c))
                {
                    allDigits = false;
                    break;
                }
            }
            if (allDigits)
                return true;
        }

        return false;
    }

    /// <summary>Padrão clássico de ticker de ação na B3 (ex.: PETR4, VALE3) — 4 letras + dígito.</summary>
    private static bool IsTypicalBovespaEquityTicker(string s)
    {
        if (s.Length != 5 || !char.IsAsciiDigit(s[4]))
            return false;
        for (int i = 0; i < 4; i++)
        {
            if (!char.IsAsciiLetter(s[i]))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Teto duro para a espera após o primeiro callback chegar. Saímos cedo via
    /// <c>HistoryQuietMs</c> ou pelo sinal <c>Progress=100</c> + drain.
    /// </summary>
    private const int HistoryWaitMaxMs = 600_000; // 10 min
    /// <summary>
    /// Tempo máximo à espera do PRIMEIRO callback após <c>GetHistoryTrades</c> retornar.
    /// Se a DLL não emitir nada nestes segundos, abandonamos esta tentativa.
    /// </summary>
    private const int HistoryInitialWaitMs = 60_000;
    /// <summary>
    /// Janela de silêncio que conclui o download (sem sinal de Progress).
    /// </summary>
    private const int HistoryQuietMs = 60_000;
    /// <summary>
    /// Drain após <c>Progress=100</c>: o manual diz que Progress é específico do <c>THistoryTradeCallback</c>
    /// e vai de 0 a 100. Quando atinge 100 sabemos que a DLL terminou — damos 15 s de margem
    /// para os últimos callbacks chegarem antes de retornar.
    /// </summary>
    private const int HistoryProgressDoneDrainMs = 15_000;
    /// <summary>Tempo máximo para a chamada da DLL retornar (não confundir com a espera por callbacks).</summary>
    public static TimeSpan NativeGetHistoryTradesCallTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Notificação opcional para a UI durante o download: ticker, bolsa, formato e estado/observação.
    /// Permite mostrar "WINM26 / F / dd/MM HH:mm:ss : sem callbacks…" em vez de só "A descarregar…".
    /// </summary>
    public static event Action<string, string, string, string>? AttemptStatus;

    private static void RaiseAttemptStatus(string ticker, string exchange, string dateFormat, string note)
    {
        try { AttemptStatus?.Invoke(ticker, exchange, dateFormat, note); } catch { /* best effort */ }
    }

    /// <summary>
    /// Dias máximos por cada chamada a <c>GetHistoryTrades</c>. O manual ProfitDLL não fixa limite de período;
    /// na prática o servidor/licença pode falhar ou devolver vazio para intervalos grandes (ex.: ~8 dias).
    /// Podes aumentar ou diminuir antes de <see cref="RequestHistoricalData"/> conforme testes na tua conta.
    /// </summary>
    public static int MaxHistoryChunkDays { get; set; } = 7;

    /// <summary>
    /// Quando <c>true</c>, <c>WINFUT</c>/<c>WDOFUT</c>/<c>INDFUT</c> são substituídos pelo código do contrato
    /// (ex. <c>WINJ26</c>) antes de <c>GetHistoryTrades</c>. Predefinição <c>false</c>: recomendação Nelogica —
    /// pedir histórico com o ticker contínuo (<c>WINFUT</c>, etc.).
    /// </summary>
    public static bool ResolveContinuousAliasForHistory { get; set; }

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

        if (ResolveContinuousAliasForHistory)
            sym = ResolveContinuousAliasIfNeeded(sym, start);

        _sink.SetCurrentContract(sym);

        int startInt = ToYyyyMmDd(start);
        int endInt = ToYyyyMmDd(end);

        var bridge = new ProviderHistoryBridge(_sink);
        bridge.Subscribe();
        ProfitHistoryRelay.BeginHistoricalDownloadScope();
        Interlocked.Exchange(ref s_lastProgressPct, -1);
        Action<string, int>? onProg = (tk, p) =>
        {
            if (p < 0) return;
            // Sinal de fim: a DLL costuma emitir Progress=100 quando termina de despejar histórico.
            // Guardamos a última % para o WaitHistoryQuietAsync sair imediatamente em 100%.
            if (!string.IsNullOrEmpty(tk) && !ProgressTickerMatchesRequest(sym, tk))
            {
                ProfitDllDiag.Append($"[Progress] {tk} {p}% (outro ticker, ignorado para sinal de fim)");
                return;
            }
            Interlocked.Exchange(ref s_lastProgressPct, p);
            ProfitDllDiag.Append($"[Progress] {tk} {p}%");
        };
        ProfitHistoryRelay.HistoryProgress += onProg;
        await s_dllGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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
                    "ProfitDLL64.dll não expõe histórico por período nesta instalação. " +
                    "Confirme que ProfitDLL64.dll está junto ao executável e que a sessão de mercado está ligada. " +
                    "Export esperado: GetHistoryTrades (DLLNewHistoryTypedTradesByPeriod não está presente nesta build).");

            return lastRc;
        }
        finally
        {
            // Drain final: dá tempo para os últimos callbacks da DLL chegarem antes
            // de descer o bridge e fechar o escopo. Sem isto, em downloads grandes,
            // perdiam-se as últimas centenas/milhares de negócios do dia.
            try
            {
                long ticksBeforeFinalDrain = Volatile.Read(ref s_historyTicks);
                long lastChange = Environment.TickCount64;
                long deadline = lastChange + 5_000;
                while (Environment.TickCount64 < deadline)
                {
                    await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
                    long now = Volatile.Read(ref s_historyTicks);
                    if (now != ticksBeforeFinalDrain)
                    {
                        ticksBeforeFinalDrain = now;
                        lastChange = Environment.TickCount64;
                        deadline = lastChange + 5_000;
                    }
                    else if (Environment.TickCount64 - lastChange >= 1_500)
                    {
                        break;
                    }
                }
            }
            catch
            {
                /* drain best-effort */
            }

            ProfitHistoryRelay.HistoryProgress -= onProg;
            bridge.Unsubscribe();
            ProfitHistoryRelay.EndHistoricalDownloadScope();
            s_dllGate.Release();
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
        if (s_optionalSubscribeTried || ProfitMarketInit.IsDllInitializedInProcess)
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
                // Probes só com WINFUT (BMF) — bolsa F conforme manual.
                int r = d("WINFUT", "F");
                ProfitDllDiag.Append($"optional {export} WINFUT/F rc={r}");
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

        string[] candidates =
        [
            "GetHistoryTrades",
            "_GetHistoryTrades@16",
            "_GetHistoryTrades@20"
        ];

        foreach (string name in candidates)
        {
            if (!NativeLibrary.TryGetExport(s_dllModule, name, out nint fn))
                continue;

            s_getHistoryTrades = Marshal.GetDelegateForFunctionPointer<GetHistoryTradesFn>(fn);
            ProfitDllDiag.Append($"GetHistoryTrades ligado via export {name}");
            return true;
        }

        ProfitDllDiag.Append("Export GetHistoryTrades não encontrado na ProfitDLL64");
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

            string primary = symbol.Trim().ToUpperInvariant();

            // Um pedido por dia civil: intervalos multi-dia costumam ficar com Progress ~98%
            // e zero callbacks; por dia o servidor completa e despacha negócios.
            for (DateTime day = c0.Date; day <= c1.Date; day = day.AddDays(1))
            {
                DateTime segStart = day <= c0.Date ? c0 : day.Date;
                DateTime dayEndMoment = day.Date.AddDays(1).AddMilliseconds(-1);
                DateTime segEnd = dayEndMoment < c1 ? dayEndMoment : c1;
                if (segStart > segEnd)
                    continue;

                ProfitDllDiag.Append($"    segmento {segStart:dd/MM/yyyy HH:mm:ss} → {segEnd:dd/MM/yyyy HH:mm:ss}");

                var datePairs = BuildDateRangePairs(segStart, segEnd);
                lastRc = await RequestGetHistoryWorkAsync(primary, datePairs, useAsyncWait, ct).ConfigureAwait(false);
            }
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

        // Manual ProfitDLL (GetHistoryTrades): formato OBRIGATÓRIO "DD/MM/YYYY HH:mm:SS".
        // Formatos ISO (yyyy-MM-dd) NÃO são suportados — retornam NL_INVALID_ARGS (-2147483645).
        string ddStartNoMs = clippedStart.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        string ddEndNoMs = dayEnd.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);

        // Variante com .fff como fallback defensivo — algumas builds aceitam, outras ignoram.
        string ddStart = clippedStart.ToString("dd/MM/yyyy HH:mm:ss.fff", CultureInfo.InvariantCulture);
        string ddEnd = dayEnd.ToString("dd/MM/yyyy HH:mm:ss.fff", CultureInfo.InvariantCulture);

        return
        [
            (ddStartNoMs, ddEndNoMs),
            (ddStart, ddEnd),
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
        else if (budget > TimeSpan.FromMinutes(10))
            budget = TimeSpan.FromMinutes(10);

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
        bool skipMarketSetup = ProfitMarketInit.IsDllInitializedInProcess;
        if (!skipMarketSetup)
            TryOptionalSubscribeHistoryOnce();

        int lastRc = 0;
        string[] exchanges = GetHistoryExchangesForTicker(ticker);
        ProfitDllDiag.Append($"[GetHistoryTrades] ticker={ticker} bolsas={string.Join(',', exchanges)}");

        foreach (string ex in exchanges)
        {
            long tickRound = Volatile.Read(ref s_historyTicks);
            // Manual: antes de histórico pode ser necessário SubscribeTicker no ativo.
            // Com sessão já ligada também fazemos subscribe — sem isto vimos Progress ~98% e 0 callbacks.
            int rsT = SubscribeTicker(ticker, ex);
            int rsO = SubscribeOfferBook(ticker, ex);
            ProfitDllDiag.Append(
                skipMarketSetup
                    ? $"SubscribeTicker({ticker},{ex}) rc={rsT} OfferBook rc={rsO} (sessão já ativa)"
                    : $"SubscribeTicker({ticker},{ex}) rc={rsT} SubscribeOfferBook rc={rsO}");

            try
            {
                foreach (var pair in datePairs)
                {
                    long tickBefore = Volatile.Read(ref s_historyTicks);
                    Interlocked.Exchange(ref s_lastProgressPct, -1);

                    RaiseAttemptStatus(ticker, ex, pair.Start, "a chamar GetHistoryTrades…");

                    try
                    {
                        lastRc = await CallGetHistoryTradesWithTimeoutAsync(ticker, ex, pair.Start, pair.End, ct)
                            .ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        RaiseAttemptStatus(ticker, ex, pair.Start, "TIMEOUT na chamada DLL — tentando próximo formato");
                        continue;
                    }

                    ProfitDllDiag.Append($"GetHistoryTrades ticker={ticker} ex={ex} rc={lastRc} start={pair.Start} end={pair.End}");

                    // rc != 0: a DLL recusou o pedido. Esperar 10 min de callbacks que nunca virão
                    // é o que estava a causar downloads "parados" sem informação. Saímos com janela curta.
                    if (lastRc != 0)
                    {
                        RaiseAttemptStatus(ticker, ex, pair.Start, $"rc={lastRc} (recusado) — próxima tentativa");
                        await WaitHistoryQuietAsync(
                            tickBefore,
                            maxWaitMs: 5_000,
                            initialWaitMs: 5_000,
                            quietWindowMs: HistoryQuietMs,
                            useAsyncWait, ct).ConfigureAwait(false);

                        if (Volatile.Read(ref s_historyTicks) > tickBefore)
                            return lastRc; // surpresa: ainda assim chegaram callbacks
                        continue;
                    }

                    RaiseAttemptStatus(ticker, ex, pair.Start, "rc=0, à espera de callbacks…");

                    // rc == 0: pode haver dados a caminho. Damos 45s para o primeiro callback aparecer.
                    // Se nada chegar, abandonamos esta tentativa. Se chegar, esperamos a janela quieta.
                    await WaitHistoryQuietAsync(
                        tickBefore,
                        maxWaitMs: HistoryWaitMaxMs,
                        initialWaitMs: HistoryInitialWaitMs,
                        quietWindowMs: HistoryQuietMs,
                        useAsyncWait, ct).ConfigureAwait(false);

                    long ticksAfter = Volatile.Read(ref s_historyTicks);
                    if (ticksAfter > tickBefore)
                    {
                        ProfitDllDiag.Append(
                            $"{ticker}/{ex}: recebidos callbacks (Δticks={ticksAfter - tickBefore}, progress={s_lastProgressPct})");
                        RaiseAttemptStatus(ticker, ex, pair.Start, $"OK — {ticksAfter - tickBefore} callbacks");
                        return lastRc;
                    }

                    RaiseAttemptStatus(ticker, ex, pair.Start, "0 callbacks neste formato — próxima tentativa");
                }
            }
            finally
            {
                // Só desinscreve quando foi nós que fizemos login isolado — não quebrar livro ao vivo.
                if (!skipMarketSetup)
                {
                    long ticksBeforeDrain = Volatile.Read(ref s_historyTicks);
                    await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
                    long ticksAfterDrain = Volatile.Read(ref s_historyTicks);
                    if (ticksAfterDrain != ticksBeforeDrain)
                        ProfitDllDiag.Append($"{ticker}/{ex}: drain capturou {ticksAfterDrain - ticksBeforeDrain} callbacks adicionais antes do unsubscribe");

                    _ = UnsubscribeOfferBook(ticker, ex);
                    _ = UnsubscribeTicker(ticker, ex);
                }
            }

            if (Volatile.Read(ref s_historyTicks) > tickRound)
                return lastRc;
        }

        return lastRc;
    }

    /// <summary>
    /// Espera após bursts de ticks. Sai pelas seguintes condições, em ordem:
    ///   • <paramref name="initialWaitMs"/> sem nenhum callback (DLL ignorou o pedido) — desiste rápido;
    ///   • <paramref name="quietWindowMs"/> de silêncio APÓS o último callback (fim normal);
    ///   • <c>Progress=100</c> + <see cref="HistoryProgressDoneDrainMs"/> sem novos callbacks (fim sinalizado);
    ///   • <paramref name="maxWaitMs"/> total (teto duro).
    /// </summary>
    private static async Task WaitHistoryQuietAsync(
        long tickAtStart,
        int maxWaitMs,
        int initialWaitMs,
        int quietWindowMs,
        bool useAsyncWait,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastSeen = tickAtStart;
        long lastChangeAt = -1;
        long progress100At = -1;
        long ticksAtProgress100 = tickAtStart;

        while (sw.ElapsedMilliseconds < maxWaitMs)
        {
            ct.ThrowIfCancellationRequested();
            long now = Volatile.Read(ref s_historyTicks);
            if (now != lastSeen)
            {
                lastSeen = now;
                lastChangeAt = sw.ElapsedMilliseconds;
            }

            if (progress100At < 0 && s_lastProgressPct >= 100)
            {
                progress100At = sw.ElapsedMilliseconds;
                ticksAtProgress100 = now;
                ProfitDllDiag.Append(
                    $"[WaitQuiet] Progress=100 visto aos {progress100At}ms — drain {HistoryProgressDoneDrainMs}ms para últimos callbacks");
            }

            // Progress=100 + drain sem novos callbacks → fim sinalizado pela DLL.
            if (progress100At >= 0
                && sw.ElapsedMilliseconds - progress100At >= HistoryProgressDoneDrainMs
                && (lastChangeAt < 0 || sw.ElapsedMilliseconds - lastChangeAt >= HistoryProgressDoneDrainMs))
            {
                long extraCallbacks = now - ticksAtProgress100;
                ProfitDllDiag.Append(
                    $"[WaitQuiet] fim por Progress=100 (drain {HistoryProgressDoneDrainMs}ms; +{extraCallbacks} callbacks após 100%)");
                return;
            }

            // Janela quieta após último callback recebido (fallback se Progress não chegar).
            if (lastChangeAt >= 0 && sw.ElapsedMilliseconds - lastChangeAt >= quietWindowMs)
            {
                ProfitDllDiag.Append(
                    $"[WaitQuiet] fim por janela quieta de {quietWindowMs}ms (elapsed={sw.ElapsedMilliseconds}ms, lastChangeAt={lastChangeAt}ms)");
                return;
            }

            // Initial-wait: sem callbacks — mas se Progress está entre 1 e 99, a DLL está a carregar
            // histórico no servidor; abandonar aos 60s deixa o pedido eternamente em ~98% sem ticks.
            int prog = s_lastProgressPct;
            bool loadingHistory = prog >= 1 && prog < 100;
            if (lastChangeAt < 0
                && initialWaitMs > 0
                && sw.ElapsedMilliseconds >= initialWaitMs
                && !loadingHistory)
            {
                ProfitDllDiag.Append(
                    $"[WaitQuiet] abandono — sem callbacks em {initialWaitMs}ms (progress={prog})");
                return;
            }

            if (useAsyncWait)
                await Task.Delay(120, ct).ConfigureAwait(false);
            else
                Thread.Sleep(120);
        }

        ProfitDllDiag.Append(
            $"[WaitQuiet] saiu por maxWait={maxWaitMs}ms (lastChange={lastChangeAt}, progress={s_lastProgressPct})");
    }

    private static int ToYyyyMmDd(DateTime dt)
    {
        dt = dt.Date;
        return dt.Year * 10_000 + dt.Month * 100 + dt.Day;
    }

    /// <summary>
    /// Permite que pedidos com ticker contínuo (<c>WINFUT</c>) continuem a aceitar <c>Progress</c>
    /// quando a DLL reporta o contrato negociado (ex. <c>WINJ26</c>) — caso contrário o fim do download nunca é detectado.
    /// </summary>
    private static bool ProgressTickerMatchesRequest(string requestSym, string progressTicker)
    {
        if (string.IsNullOrEmpty(progressTicker))
            return true;

        if (progressTicker.IndexOf(requestSym, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        if (requestSym.IndexOf(progressTicker, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        string norm = requestSym.Replace("-", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal)
            .ToUpperInvariant();

        ReadOnlySpan<char> tk = progressTicker.AsSpan().Trim();
        return norm switch
        {
            "WINFUT" => tk.StartsWith("WIN", StringComparison.OrdinalIgnoreCase),
            "WDOFUT" => tk.StartsWith("WDO", StringComparison.OrdinalIgnoreCase),
            "INDFUT" => tk.StartsWith("IND", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    /// <summary>
    /// Opcional (<see cref="ResolveContinuousAliasForHistory"/>): mapeia <c>WINFUT</c>→<c>WINJ26</c>, etc.,
    /// pela data de vencimento. Recomendação Nelogica é usar <c>WINFUT</c> direto em <c>GetHistoryTrades</c>.
    /// </summary>
    private static string ResolveContinuousAliasIfNeeded(string sym, DateTime requestStart)
    {
        if (string.IsNullOrEmpty(sym))
            return sym;

        // Reconhece WINFUT, WDOFUT, INDFUT (e variantes com underscore/hífen).
        string normalized = sym.Replace("-", "").Replace("_", "");
        string? baseTicker = normalized switch
        {
            "WINFUT" => "WIN",
            "WDOFUT" => "WDO",
            "INDFUT" => "IND",
            _ => null,
        };

        if (baseTicker == null)
            return sym;

        // Encontra o vencimento de WIN/WDO/IND corrente na data de início (vencimentos em meses pares).
        DateTime d = requestStart.Date;
        int[] evenMonths = [2, 4, 6, 8, 10, 12];

        for (int monthsAhead = 0; monthsAhead <= 6; monthsAhead++)
        {
            DateTime candidateMonth = new DateTime(d.Year, d.Month, 1).AddMonths(monthsAhead);
            if (Array.IndexOf(evenMonths, candidateMonth.Month) < 0)
                continue;

            DateTime exp = ContractGenerator.GetExpirationWednesday(candidateMonth.Year, candidateMonth.Month);
            if (exp >= d)
            {
                // Letra do mês conforme convenção B3.
                char letter = candidateMonth.Month switch
                {
                    2 => 'G', 4 => 'J', 6 => 'M', 8 => 'Q', 10 => 'V', 12 => 'Z',
                    _ => '?'
                };
                int yy = candidateMonth.Year % 100;
                string resolved = $"{baseTicker}{letter}{yy:D2}";
                ProfitDllDiag.Append(
                    $"[ResolveAlias] '{sym}' em {d:dd/MM/yyyy} → '{resolved}' (venc. {exp:dd/MM/yyyy})");
                return resolved;
            }
        }

        ProfitDllDiag.Append(
            $"[ResolveAlias] '{sym}' em {d:dd/MM/yyyy} — não encontrei vencimento corrente; mantendo o alias (vai falhar)");
        return sym;
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
        Interlocked.Increment(ref s_historyTicks);

        // Algumas builds da DLL devolvem qty=0 no fluxo typed (a quantidade vai
        // implícita no número de chamadas ou pelo opType). Garantir 1 mínimo evita
        // que o TradeRecordFactory descarte silenciosamente o negócio.
        int qtyForSink = qty > 0 ? qty : 1;

        try
        {
            _sink.OnHistoricalTrade(
                opType, symbol, date, time, price, qtyForSink, tradeNum,
                buyOrder, sellOrder, buyBroker, sellBroker, aggressor);
        }
        catch (Exception ex)
        {
            ProfitDllDiag.Append($"[HistorySink] {ex.GetType().Name}: {ex.Message}");
        }
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

            try
            {
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
            catch (Exception ex)
            {
                ProfitDllDiag.Append($"[HistorySink] {ex.GetType().Name}: {ex.Message}");
            }
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
