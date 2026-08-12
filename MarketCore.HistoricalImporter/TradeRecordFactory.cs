namespace MarketCore.HistoricalImporter;

/// <summary>Monta <see cref="TradeRecord"/> a partir dos parâmetros do callback histórico da DLL.</summary>
internal static class TradeRecordFactory
{
    public static bool TryCreate(
        string? fallbackContract,
        string? symbol,
        string? date,
        string? time,
        double price,
        int qty,
        string? buyBroker,
        string? sellBroker,
        int aggressor,
        out TradeRecord record)
    {
        record = default!;
        if (qty <= 0 || price <= 0 || double.IsNaN(price) || double.IsInfinity(price))
            return false;

        string contract = string.IsNullOrWhiteSpace(symbol) ? (fallbackContract ?? "") : symbol.Trim();
        if (string.IsNullOrEmpty(contract))
            return false;

        try
        {
            record = new TradeRecord
            {
                Timestamp = DllDateTime.ParseTradeTimestamp(date ?? "", time ?? ""),
                Price = (decimal)price,
                Quantity = qty,
                Aggressor = MapAggressor(aggressor),
                BuyerBroker = NullIfEmpty(buyBroker),
                SellerBroker = NullIfEmpty(sellBroker),
                Contract = contract.Length > 10 ? contract[..10] : contract,
                SessionDate = DllDateTime.ParseSessionDate(date ?? "")
            };
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Nelogica DLL: 2=compra agressão 3=venda agressão (cf. Manual TNewTradeCallback).</summary>
    private static char MapAggressor(int aggressor) =>
        aggressor switch
        {
            2 => 'C',
            3 => 'V',
            _ => 'U'
        };

    private static string? NullIfEmpty(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        string t = s.Trim();
        return t.Length > 100 ? t[..100] : t;
    }
}
