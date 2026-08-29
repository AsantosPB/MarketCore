using MarketCore.Engine.Storage;

namespace MarketCore.Engine.Features;

/// <summary>
/// Estado completo de todas as features num momento específico.
/// Trafega entre Feature Engine → Agent Engine → Decision Core.
/// Gerado a cada 100 ms pelo SnapshotTimer.
/// </summary>
public class FeatureSnapshot
{
    public long   Timestamp        { get; set; }
    public string SessionDate      { get; set; } = string.Empty;

    // ── Preço ─────────────────────────────────────────────────────────────
    public double Price            { get; set; }
    public double Bid              { get; set; }
    public double Ask              { get; set; }
    public double Spread           { get; set; }

    // ── Book ──────────────────────────────────────────────────────────────
    public double BookImbalance    { get; set; }
    public double Microprice       { get; set; }
    public double MicropriceDelta  { get; set; }  // variação do microprice vs anterior
    public double BidDepth         { get; set; }  // volume total no melhor bid
    public double AskDepth         { get; set; }  // volume total no melhor ask
    public double DepthImbalance   { get; set; }  // (BidDepth - AskDepth) / (BidDepth + AskDepth)
    public double StackingScore    { get; set; }  // -100..+100
    public double PullingScore     { get; set; }  // -100..+100

    // ── Flow — Delta em múltiplas janelas ─────────────────────────────────
    public long   Delta100ms       { get; set; }
    public long   Delta500ms       { get; set; }
    public long   Delta1s          { get; set; }
    public long   Delta2s          { get; set; }
    public long   Delta5s          { get; set; }

    // ── Flow — Order Flow Imbalance ───────────────────────────────────────
    public double Ofi100ms         { get; set; }
    public double Ofi500ms         { get; set; }
    public double Ofi1s            { get; set; }

    // ── Flow — Taxas ──────────────────────────────────────────────────────
    public double TradeRate        { get; set; }
    public double VolumeRate       { get; set; }
    public double AggressionRatio  { get; set; }  // aggBuy / (aggBuy + aggSell), 0..1

    // ── Aceleração ────────────────────────────────────────────────────────
    public double Velocity         { get; set; }
    public double Acceleration     { get; set; }
    public double Volatility30s    { get; set; }

    // ── Contexto ──────────────────────────────────────────────────────────
    public double Vwap             { get; set; }
    public double DistanceVwap     { get; set; }
    public double DistanceHigh     { get; set; }
    public double DistanceLow      { get; set; }
    public double AbsorptionScore  { get; set; }  // -100..+100

    // ── Regime e janela temporal ──────────────────────────────────────────
    public string Regime           { get; set; } = "INDEFINIDO";
    public double Confidence       { get; set; }  // confiança do RegimeDetector (0-100)
    public string TimeWindow       { get; set; } = string.Empty;
    public bool   HasEconomicEvent { get; set; }
    public int    EventImpact      { get; set; }

    // ── Conversão para persistência ───────────────────────────────────────

    /// <summary>Converte para MarketSnapshot (schema DuckDB). Campos extras são descartados.</summary>
    public MarketSnapshot ToMarketSnapshot() => new()
    {
        Timestamp        = Timestamp,
        SessionDate      = SessionDate,
        Price            = Price,
        Bid              = Bid,
        Ask              = Ask,
        Spread           = Spread,
        BookImbalance    = BookImbalance,
        Microprice       = Microprice,
        Delta100ms       = Delta100ms,
        Delta500ms       = Delta500ms,
        Delta1s          = Delta1s,
        Delta5s          = Delta5s,
        Ofi100ms         = Ofi100ms,
        Ofi500ms         = Ofi500ms,
        Ofi1s            = Ofi1s,
        TradeRate        = TradeRate,
        VolumeRate       = VolumeRate,
        Volatility30s    = Volatility30s,
        Vwap             = Vwap,
        DistanceVwap     = DistanceVwap,
        AbsorptionScore  = AbsorptionScore,
        StackingScore    = StackingScore,
        PullingScore     = PullingScore,
        Velocity         = Velocity,
        Acceleration     = Acceleration,
        Regime           = Regime,
        TimeWindow       = TimeWindow,
        HasEconomicEvent = HasEconomicEvent,
        EventImpact      = EventImpact,
    };
}
