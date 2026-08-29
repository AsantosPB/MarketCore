namespace MarketCore.Engine.Storage;

/// <summary>
/// Snapshot de mercado com features derivadas — gravado no DuckDB (tabela market_snapshots).
/// Gerado pelo Feature Engine a cada tick significativo.
/// </summary>
public class MarketSnapshot
{
    public long   Timestamp        { get; set; }
    public string SessionDate      { get; set; } = string.Empty;
    public double Price            { get; set; }
    public double Bid              { get; set; }
    public double Ask              { get; set; }
    public double Spread           { get; set; }
    public double BookImbalance    { get; set; }
    public double Microprice       { get; set; }
    public long   Delta100ms       { get; set; }
    public long   Delta500ms       { get; set; }
    public long   Delta1s          { get; set; }
    public long   Delta5s          { get; set; }
    public double Ofi100ms         { get; set; }
    public double Ofi500ms         { get; set; }
    public double Ofi1s            { get; set; }
    public double TradeRate        { get; set; }
    public double VolumeRate       { get; set; }
    public double Volatility30s    { get; set; }
    public double Vwap             { get; set; }
    public double DistanceVwap     { get; set; }
    public double AbsorptionScore  { get; set; }
    public double StackingScore    { get; set; }
    public double PullingScore     { get; set; }
    public double Velocity         { get; set; }
    public double Acceleration     { get; set; }
    public string Regime           { get; set; } = string.Empty;
    public string TimeWindow       { get; set; } = string.Empty;
    public bool   HasEconomicEvent { get; set; }
    public int    EventImpact      { get; set; }
}

/// <summary>
/// Decisão gerada pelo Decision Core — gravada no SQLite (tabela decisions).
/// Registra score final, direção, aprovação de risco e se entrada foi tomada.
/// </summary>
public class DecisionRecord
{
    public long   Timestamp     { get; set; }
    public double FinalScore    { get; set; }
    public string Direction     { get; set; } = string.Empty;
    public string DecisionState { get; set; } = string.Empty;
    public string AgentScores   { get; set; } = string.Empty; // JSON
    public string Regime        { get; set; } = string.Empty;
    public string TimeWindow    { get; set; } = string.Empty;
    public bool   RiskApproved  { get; set; }
    public bool   EntryTaken    { get; set; }
    public string BlockReason   { get; set; } = string.Empty;
}

/// <summary>
/// Operação completa (entrada + saída) — gravada no SQLite (tabela trades).
/// Inclui P&amp;L, slippage, MFE/MAE e metadados do padrão que originou a entrada.
/// </summary>
public class TradeRecord
{
    public string TradeId          { get; set; } = string.Empty;
    public long   EntryTime        { get; set; }
    public double EntryPrice       { get; set; }
    public string Side             { get; set; } = string.Empty;
    public int    Quantity         { get; set; }
    public long   ExitTime         { get; set; }
    public double ExitPrice        { get; set; }
    public double GrossPnl         { get; set; }
    public double Slippage         { get; set; }
    public double NetPnl           { get; set; }
    public double Mfe              { get; set; }
    public double Mae              { get; set; }
    public string ExitReason       { get; set; } = string.Empty;
    public int    PatternId        { get; set; }
    public string StrategyVersion  { get; set; } = string.Empty;
}
