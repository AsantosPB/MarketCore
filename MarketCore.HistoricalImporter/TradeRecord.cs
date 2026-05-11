namespace MarketCore.HistoricalImporter;

public sealed class TradeRecord
{
    public DateTime Timestamp { get; set; }
    public decimal Price { get; set; }
    public int Quantity { get; set; }
    /// <summary><c>C</c> compra, <c>V</c> venda (conforme DLL).</summary>
    public char Aggressor { get; set; }
    public string? BuyerBroker { get; set; }
    public string? SellerBroker { get; set; }
    public string Contract { get; set; } = "";
    /// <summary>Data da sessão (pregão) no fuso local da bolsa.</summary>
    public DateTime SessionDate { get; set; }
}
