using System.Globalization;
using System.Text;

namespace MarketCore.HistoricalImporter;

/// <summary>Acumula negócios e grava CSV na pasta configurada (separador <c>;</c> para Excel PT-BR).</summary>
public sealed class CsvTradeExportBuffer : IProfitHistoryTradeSink
{
    public const int BufferCapacity = 10_000;

    private readonly List<TradeRecord> _buffer = new(BufferCapacity);
    private readonly object _sync = new();
    private readonly HashSet<string> _headerWritten = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pasta destino.</summary>
    public string OutputFolder { get; set; } = "";

    /// <summary>Rótulo de sessão nos nomes de ficheiro, ex.: <c>20260101_20260131</c>.</summary>
    public string FileSessionLabel { get; set; } = "";

    private string _fallbackContractSymbol = "";

    /// <summary>Novo período — limpa estado de cabeçalhos para não misturar com export anterior.</summary>
    public void BeginNewExportSession(string sessionLabel)
    {
        lock (_sync)
        {
            FileSessionLabel = sessionLabel;
            _headerWritten.Clear();
            TotalExported = 0;
        }
    }

    public void SetCurrentContract(string symbol) => _fallbackContractSymbol = symbol ?? "";

    public long TotalExported { get; private set; }

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
                _fallbackContractSymbol, symbol, date, time, price, qty,
                buyBroker, sellBroker, aggressor, out TradeRecord rec))
            return;

        lock (_sync)
        {
            _buffer.Add(rec);
            if (_buffer.Count >= BufferCapacity)
                FlushLocked();
        }
    }

    public void Flush() => FlushPendingExports();

    public void FlushPendingExports()
    {
        lock (_sync) FlushLocked();
    }

    /// <summary>Escreve todas as linhas pendentes, agrupando por contrato em ficheiros distintos.</summary>
    private void FlushLocked()
    {
        if (_buffer.Count == 0 || string.IsNullOrWhiteSpace(OutputFolder))
            return;

        Directory.CreateDirectory(OutputFolder.Trim());

        var batch = _buffer.ToList();
        _buffer.Clear();

        foreach (var grp in batch.GroupBy(r => r.Contract, StringComparer.OrdinalIgnoreCase))
        {
            WriteGroupToFile(grp.Key, grp.ToList());
            TotalExported += grp.Count();
        }
    }

    private void WriteGroupToFile(string contract, List<TradeRecord> rows)
    {
        string label = string.IsNullOrWhiteSpace(FileSessionLabel)
            ? "export"
            : FileSessionLabel.Trim();
        string safeContract = contract;
        foreach (char c in Path.GetInvalidFileNameChars())
            safeContract = safeContract.Replace(c, '_');
        string fileName = $"{safeContract}_{label}.csv";
        string path = Path.Combine(OutputFolder.Trim(), fileName);

        bool writeHeader = _headerWritten.Add(path);

        var sb = new StringBuilder(capacity: rows.Count * 96);
        if (writeHeader)
            sb.AppendLine("timestamp;price;quantity;aggressor;buyer_broker;seller_broker;contract;session_date");

        foreach (var r in rows)
        {
            sb.Append(Escape(r.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)))
                .Append(';')
                .Append(r.Price.ToString("F2", CultureInfo.InvariantCulture))
                .Append(';').Append(r.Quantity).Append(';').Append(r.Aggressor).Append(';')
                .Append(Escape(r.BuyerBroker ?? "")).Append(';')
                .Append(Escape(r.SellerBroker ?? "")).Append(';')
                .Append(Escape(r.Contract)).Append(';')
                .AppendLine(r.SessionDate.ToString("yyyy-MM-dd"));
        }

        File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
    }

    private static string Escape(string s)
    {
        if (s.Contains(';') || s.Contains('"') || s.Contains('\r') || s.Contains('\n'))
            return $"\"{s.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        return s;
    }
}
