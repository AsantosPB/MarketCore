using System;
using System.IO;
using System.Threading;

namespace MarketCore.Providers.Nelogica;

/// <summary>
/// Amostrador de latência da ProfitDLL — grava, a cada 1 segundo, um snapshot com:
///
///   • <b>tradeAge/bookAge</b> — idade do último callback recebido, medida como
///     <c>DateTime.Now - bolsa</c>. Se a DLL entrega dados frescos, é ~200-500 ms.
///     Se cresce sem parar, a DLL está entregando dados atrasados (backpressure
///     na Nelogica) ou a callback está sendo bloqueada por processamento nosso.
///
///   • <b>tradeProcAge/bookProcAge</b> — idem, mas medido no fim do processamento
///     (depois de sair da fila interna e invocar handlers). Se este é maior que
///     tradeAge por uma diferença crescente, o gargalo é nosso worker.
///
///   • <b>tradeQ/bookQ/depthQ</b> — tamanho das filas internas. Filas próximas de
///     zero = worker acompanha. Filas crescendo = worker não dá conta.
///
///   • <b>rxRate/procRate</b> — callbacks recebidos e processados por segundo.
///
/// Independente do Pregão Viva Voz — este monitor liga junto com o processador da
/// DLL e grava mesmo com o PVV desligado, o que permite isolar se o atraso é da
/// DLL ou do PVV.
///
/// Arquivo: <c>%LocalAppData%\MarketCore\dll_latency.log</c>
/// </summary>
internal static class DllLatencyMonitor
{
    // Campos escritos por callbacks e loops (várias threads). Todos volatile/Interlocked.
    // ticks == DateTime.Ticks do bolsa (exchange time convertido para local).
    public static long LastTradeExchangeTicks;
    public static long LastTradeProcessedExchangeTicks;
    public static long LastBookExchangeTicks;
    public static long LastBookProcessedExchangeTicks;

    /// <summary>
    /// Profundidade atual da fila de book — escrita pelo BookProcessingLoop a cada 256 eventos,
    /// lida pelo MarketEngine.HandleBook para decidir se pula computações PVV (backpressure).
    /// Quando &gt; <c>PvvBackpressureThreshold</c>, PVV é pulado para que os deltas de book
    /// sejam aplicados sem overhead dos scans O(n); PVV retoma quando a fila esvazia.
    /// </summary>
    public static volatile int BookQueueDepth;

    /// <summary>Acima desse tamanho de fila, PVV é desligado temporariamente.</summary>
    public const int PvvBackpressureThreshold = 500;

    public static long TradesReceivedTotal;
    public static long TradesProcessedTotal;
    public static long BooksReceivedTotal;
    public static long BooksProcessedTotal;

    // Snapshot das filas: injetado pelo ProfitDLLProvider (para não ter que expor internals).
    private static Func<(int tradeQ, int bookQ, int depthQ)>? _queueSnapshot;

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MarketCore",
        "dll_latency.log");

    private static readonly object _gate = new();
    private static Thread? _samplerThread;
    private static volatile bool _running;

    // Contadores da última amostra, pra calcular taxa (delta / intervalo).
    private static long _prevTradesRx, _prevTradesProc, _prevBooksRx, _prevBooksProc;
    private static DateTime _prevSampleAt;

    /// <summary>Liga o amostrador. Idempotente — múltiplas chamadas são no-op.</summary>
    public static void Start(Func<(int tradeQ, int bookQ, int depthQ)> queueSnapshot)
    {
        lock (_gate)
        {
            if (_running) return;
            _queueSnapshot = queueSnapshot;
            _running = true;
            _prevSampleAt = DateTime.Now;
            _prevTradesRx = TradesReceivedTotal;
            _prevTradesProc = TradesProcessedTotal;
            _prevBooksRx = BooksReceivedTotal;
            _prevBooksProc = BooksProcessedTotal;

            _samplerThread = new Thread(SamplerLoop)
            {
                IsBackground = true,
                Name = "DllLatencyMonitor",
                Priority = ThreadPriority.BelowNormal
            };
            _samplerThread.Start();

            AppendHeader();
        }
    }

    /// <summary>Para o amostrador. Idempotente.</summary>
    public static void Stop()
    {
        lock (_gate)
        {
            if (!_running) return;
            _running = false;
        }
        _samplerThread?.Join(TimeSpan.FromSeconds(2));
        _samplerThread = null;
    }

    private static void AppendHeader()
    {
        try
        {
            var dir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            string header =
                $"===== DllLatencyMonitor iniciado em {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ====={Environment.NewLine}" +
                $"# colunas: tradeAge | tradeProcAge | bookAge | bookProcAge | tradeQ | bookQ | depthQ | tradesRx/s | tradesProc/s | booksRx/s | booksProc/s{Environment.NewLine}" +
                $"# tradeAge/bookAge  = agora - bolsa do último recebido (latência da DLL até nós){Environment.NewLine}" +
                $"# tradeProcAge/…   = agora - bolsa do último processado (inclui nosso worker){Environment.NewLine}" +
                $"# Se tradeAge cresce → DLL está lenta ou a callback está bloqueada.{Environment.NewLine}" +
                $"# Se tradeAge estável e tradeProcAge cresce → nosso worker é o gargalo.{Environment.NewLine}";
            File.AppendAllText(LogPath, header);
        }
        catch { /* best effort */ }
    }

    private static void SamplerLoop()
    {
        while (_running)
        {
            try
            {
                Thread.Sleep(1000);
                WriteSample();
            }
            catch (ThreadInterruptedException) { break; }
            catch { /* nunca deixa o thread morrer por erro de log */ }
        }
    }

    private static void WriteSample()
    {
        var now = DateTime.Now;
        double elapsedSec = (now - _prevSampleAt).TotalSeconds;
        if (elapsedSec <= 0) elapsedSec = 1;

        long tradesRx = Interlocked.Read(ref TradesReceivedTotal);
        long tradesProc = Interlocked.Read(ref TradesProcessedTotal);
        long booksRx = Interlocked.Read(ref BooksReceivedTotal);
        long booksProc = Interlocked.Read(ref BooksProcessedTotal);

        double tradesRxRate = (tradesRx - _prevTradesRx) / elapsedSec;
        double tradesProcRate = (tradesProc - _prevTradesProc) / elapsedSec;
        double booksRxRate = (booksRx - _prevBooksRx) / elapsedSec;
        double booksProcRate = (booksProc - _prevBooksProc) / elapsedSec;

        _prevTradesRx = tradesRx;
        _prevTradesProc = tradesProc;
        _prevBooksRx = booksRx;
        _prevBooksProc = booksProc;
        _prevSampleAt = now;

        string tradeAge = FormatAge(now, Interlocked.Read(ref LastTradeExchangeTicks));
        string tradeProcAge = FormatAge(now, Interlocked.Read(ref LastTradeProcessedExchangeTicks));
        string bookAge = FormatAge(now, Interlocked.Read(ref LastBookExchangeTicks));
        string bookProcAge = FormatAge(now, Interlocked.Read(ref LastBookProcessedExchangeTicks));

        int tradeQ = 0, bookQ = 0, depthQ = 0;
        try
        {
            if (_queueSnapshot != null)
                (tradeQ, bookQ, depthQ) = _queueSnapshot();
        }
        catch { /* snapshot é best-effort */ }

        int bqDepth = BookQueueDepth;
        string pvvStatus = bqDepth >= PvvBackpressureThreshold ? "SKIP" : "ok";

        string linha =
            $"[{now:HH:mm:ss.fff}] " +
            $"tradeAge={tradeAge,-10} tradeProcAge={tradeProcAge,-10} " +
            $"bookAge={bookAge,-10} bookProcAge={bookProcAge,-10} " +
            $"tradeQ={tradeQ,-6} bookQ={bookQ,-6} depthQ={depthQ,-6} " +
            $"pvv={pvvStatus,-5} " +
            $"tradesRx/s={tradesRxRate,6:0} tradesProc/s={tradesProcRate,6:0} " +
            $"booksRx/s={booksRxRate,6:0} booksProc/s={booksProcRate,6:0}" +
            Environment.NewLine;

        try
        {
            File.AppendAllText(LogPath, linha);
        }
        catch { /* best effort */ }
    }

    private static string FormatAge(DateTime now, long ticks)
    {
        if (ticks == 0) return "n/a";
        var age = now - new DateTime(ticks);
        if (age.TotalSeconds < 1)
            return $"{age.TotalMilliseconds:0}ms";
        if (age.TotalMinutes < 1)
            return $"{age.TotalSeconds:0.0}s";
        return $"{(int)age.TotalMinutes}m{age.Seconds:00}s";
    }
}
