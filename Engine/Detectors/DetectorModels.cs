namespace MarketCore.Engine.Detectors;

public record SpoofEvent(
    DateTime Time,
    string   Ticker,
    string   Side,
    string   Broker,
    decimal  Price,
    int      VolumeBefore,
    int      VolumeAfter,
    decimal  PriceDistance
);

public record IcebergEvent(
    DateTime Time,
    string   Ticker,
    string   Side,
    string   Broker,
    decimal  FromPrice,
    decimal  ToPrice,
    int      Volume,
    string   Direction
);

public record RenewableEvent(
    DateTime Time,
    string   Ticker,
    string   Side,
    string   Broker,
    decimal  Price,
    int      VolumePerCycle,
    int      Renewals,
    int      TotalExecuted
);