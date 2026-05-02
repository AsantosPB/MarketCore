namespace MarketCore.Models;

// Direção da agressão no trade
public enum TradeAggressor { Buy, Sell, Unknown }

// Lado do book
public enum BookSide { Bid, Ask }

// Um negócio executado (vem do TNewTradeCallback)
public record TradeEvent(
    string         Ticker,
    decimal        Price,
    int            Volume,
    string         Broker,
    TradeAggressor Aggressor,
    DateTime       Time
);

// Uma oferta individual do book (vem do TOfferBookCallback)
public record BookLevel(
    string   Ticker,
    BookSide Side,
    decimal  Price,
    int      Volume,
    string   Broker,
    DateTime Time,
    long     OfferId = 0   // ID único da oferta — 0 = sem ID (book agregado)
);

// Cotação (vem do TChangeCotationCallback)
public record QuoteEvent(
    string   Ticker,
    decimal  Last,
    decimal  Bid,
    decimal  Ask,
    decimal  Open,
    decimal  High,
    decimal  Low,
    long     Volume,
    DateTime Time
);

// Status da conexão com o provedor
public enum ConnectionStatus { Disconnected, Connecting, Connected, Error }

public record ConnectionChangedEvent(
    ConnectionStatus Status,
    string           Message
);

// Snapshot completo do book em um momento específico (usado pelo Recorder)
public record BookSnapshot(
    string                   Ticker,
    IReadOnlyList<BookLevel> Bids,
    IReadOnlyList<BookLevel> Asks,
    DateTime                 Time
);
