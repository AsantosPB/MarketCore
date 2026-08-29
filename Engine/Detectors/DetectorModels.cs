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

// ── Fase 6 — Event Detector + Regime Detector ─────────────────────────────

public enum MarketRegime
{
    Unknown    = 0,
    TrendUp    = 1,
    TrendDown  = 2,
    Range      = 3,
    HighVol    = 4,
    LowVol     = 5,
    Breakout   = 6,
    Transition = 7
}

public enum MarketEventType
{
    AggressionSpike   = 1,
    BookImbalance     = 2,
    Absorption        = 3,
    PriceAcceleration = 4,
    VolumeSpike       = 5,
    LargeTrade        = 6,
    BookPull          = 7,
    BookStack         = 8,
    DeltaDivergence   = 9,
    FailedBreakout    = 10,
    TradeRateSpike    = 11,
    RegimeChange      = 12
}

public class MarketEvent
{
    public MarketEventType Type      { get; set; }
    public DateTime        Timestamp { get; set; }
    public double          Magnitude { get; set; }
    public string          Detail    { get; set; } = string.Empty;
}

public class RegimeState
{
    public MarketRegime Regime     { get; set; }
    public double       Confidence { get; set; }
    public DateTime     Since      { get; set; }
    public MarketRegime Previous   { get; set; }
}
