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
            return;

        lock (_sync)
        {
            _buffer.Add(rec);
            if (_buffer.Count >= BufferCapacity)
                FlushToDatabaseCore();
        }
    }

    /// <summary>Força COPY BINARY do buffer atual.</summary>
    public void FlushToDatabase() => FlushPendingExports();

    public void FlushPendingExports()
    {
        lock (_sync)
        {
            FlushToDatabaseCore();
        }
    }

    /// <summary>Esvazia buffer sem lock externo — chamar apenas com <c>lock (_sync)</c>.</summary>
    private void FlushToDatabaseCore()
    {
        if (_buffer.Count == 0)
            return;

        var batch = _buffer.ToList();
        _buffer.Clear();

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
    }

    private static void WriteNullableVarchar(NpgsqlBinaryImporter writer, string? value)
    {
        if (value == null)
            writer.WriteNull();
        else
            writer.Write(value, NpgsqlDbType.Varchar);
    }
}
