namespace MarketCore.Models;
public enum TradeAggressor { Buy, Sell, Unknown }
public enum BookSide { Bid, Ask }
public record TradeEvent(string Ticker, decimal Price, int Volume, string Broker, TradeAggressor Aggressor, DateTime Time);
/// <param name="ExchangeTime">Horário da oferta vindo da DLL (<c>bHasDate</c>); usado para fila FIFO no mesmo preço, alinhada ao ProfitChart.</param>
public record BookLevel(string Ticker, BookSide Side, decimal Price, int Volume, string Broker, DateTime Time, long OfferId = 0, int Action = 0, int Position = 0, DateTime? ExchangeTime = null);
public record QuoteEvent(string Ticker, decimal Last, decimal Bid, decimal Ask, decimal Open, decimal High, decimal Low, long Volume, DateTime Time);
public enum ConnectionStatus { Disconnected, Connecting, Connected, Error }
public record ConnectionChangedEvent(ConnectionStatus Status, string Message);
public record BookSnapshot(string Ticker, IReadOnlyList<BookLevel> Bids, IReadOnlyList<BookLevel> Asks, DateTime Time);

/// <summary>Snapshot oficial por lado para reconciliação com o estado da DLL (atFullBook).</summary>
public record BookFullRefresh(string Ticker, IReadOnlyList<BookLevel> Bids, IReadOnlyList<BookLevel> Asks);
