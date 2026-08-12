using System.Threading;
using Npgsql;
using NpgsqlTypes;

namespace MarketCore.HistoricalImporter;

/// <summary>Acumula negócios históricos e grava em lote via COPY BINARY.</summary>
public sealed class HistoricalImporter : IProfitHistoryTradeSink
{
    public const int BufferCapacity = 10_000;

    private readonly List<TradeRecord> _buffer = new(BufferCapacity);
    private readonly object _sync = new();
    private readonly AppConfig _cfg;
    private string _contractSymbol = "";
    private int _flushScheduled;
    private long _totalAccepted;
    private long _totalRejected;

    public HistoricalImporter(AppConfig cfg) => _cfg = cfg;

    /// <summary>Contrato em uso quando o símbolo do callback vier vazio.</summary>
    public void SetCurrentContract(string symbol) => _contractSymbol = symbol ?? "";

    public long TotalBuffered
    {
        get
        {
            lock (_sync) return _buffer.Count;
        }
    }

    /// <summary>Total de registros já gravados no PostgreSQL (após flush).</summary>
    public long TotalFlushed { get; private set; }

    /// <summary>Total de callbacks recebidos que produziram um <see cref="TradeRecord"/> válido.</summary>
    public long TotalAccepted => Interlocked.Read(ref _totalAccepted);

    /// <summary>Total de callbacks rejeitados pelo factory (qty/preço/datas inválidos).</summary>
    public long TotalRejected => Interlocked.Read(ref _totalRejected);

    /// <summary>Último erro de gravação (se houver) — evita propagar exceção em callbacks nativos.</summary>
    public string? LastFlushError { get; private set; }

    /// <summary>Callback compatível com a DLL — alimenta o buffer e dispara flush em 10k.</summary>
    public void OnHistoricalTrade(
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
        if (!TradeRecordFactory.TryCreate(
                _contractSymbol, symbol, date, time, price, qty,
                buyBroker, sellBroker, aggressor, out TradeRecord rec))
        {
            Interlocked.Increment(ref _totalRejected);
            return;
        }

        Interlocked.Increment(ref _totalAccepted);

        bool scheduleFlush;
        lock (_sync)
        {
            _buffer.Add(rec);
            scheduleFlush = _buffer.Count >= BufferCapacity;
        }

        if (scheduleFlush)
            ScheduleFlush();
    }

    /// <summary>Força COPY BINARY do buffer atual.</summary>
    public void FlushToDatabase() => FlushPendingExports();

    public void FlushPendingExports()
    {
        List<TradeRecord> batch;
        lock (_sync)
        {
            if (_buffer.Count == 0)
                return;

            batch = _buffer.ToList();
            _buffer.Clear();
        }

        WriteBatch(batch);
    }

    /// <summary>Espera flushes em background terminarem (não devolve até <c>_flushScheduled</c> ficar a 0).</summary>
    public void WaitForPendingFlushes(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Volatile.Read(ref _flushScheduled) == 0)
                return;

            Thread.Sleep(50);
        }
    }

    private void ScheduleFlush()
    {
        if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) != 0)
            return;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                while (true)
                {
                    FlushPendingExports();

                    lock (_sync)
                    {
                        if (_buffer.Count < BufferCapacity)
                            break;
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _flushScheduled, 0);

                lock (_sync)
                {
                    if (_buffer.Count >= BufferCapacity)
                        ScheduleFlush();
                }
            }
        });
    }

    private void WriteBatch(List<TradeRecord> batch)
    {
        if (batch.Count == 0)
            return;

        bool retriedDatabase = false;
        while (true)
        {
            try
            {
                WriteBatchCore(batch);
                return;
            }
            catch (PostgresException ex) when (!retriedDatabase && ex.SqlState == "3D000")
            {
                retriedDatabase = true;
                new DatabaseSetup(_cfg).EnsureReadyForImportAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                LastFlushError = ex.Message;
                ProfitDllDiag.Append($"[PostgreSQL] flush falhou ({batch.Count} linhas): {ex.GetType().Name}: {ex.Message}");

                lock (_sync)
                {
                    _buffer.InsertRange(0, batch);
                }

                return;
            }
        }
    }

    private void WriteBatchCore(List<TradeRecord> batch)
    {
        using var conn = new NpgsqlConnection(_cfg.GetConnectionString());
        conn.Open();

        using (var writer = conn.BeginBinaryImport(
                   """
                   COPY trades (timestamp, price, quantity, aggressor, buyer_broker, seller_broker, contract, session_date)
                   FROM STDIN (FORMAT BINARY)
                   """))
        {
            foreach (var r in batch)
            {
                writer.StartRow();
                writer.Write(r.Timestamp, NpgsqlDbType.Timestamp);
                writer.Write(r.Price, NpgsqlDbType.Numeric);
                writer.Write(r.Quantity, NpgsqlDbType.Integer);
                writer.Write(r.Aggressor.ToString(), NpgsqlDbType.Char);
                WriteNullableVarchar(writer, r.BuyerBroker);
                WriteNullableVarchar(writer, r.SellerBroker);
                writer.Write(r.Contract, NpgsqlDbType.Varchar);
                writer.Write(r.SessionDate.Date, NpgsqlDbType.Date);
            }

            writer.Complete();
        }

        TotalFlushed += batch.Count;
        LastFlushError = null;
    }

    private static void WriteNullableVarchar(NpgsqlBinaryImporter writer, string? value)
    {
        if (value == null)
            writer.WriteNull();
        else
            writer.Write(value, NpgsqlDbType.Varchar);
    }
}
