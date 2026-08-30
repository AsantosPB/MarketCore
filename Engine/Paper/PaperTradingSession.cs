using MarketCore.Engine.Storage;

namespace MarketCore.Engine.Paper;

/// <summary>
/// Fase 14 — Registro completo de uma sessão de paper trading.
/// Persiste no SQLite para análise posterior.
/// </summary>
public class PaperTradingSession
{
    public Guid      SessionId      { get; set; }
    public DateTime  Date           { get; set; }
    public DateTime  StartTime      { get; set; }
    public DateTime? EndTime        { get; set; }
    public int       TotalTrades    { get; set; }
    public int       WinTrades      { get; set; }
    public int       LossTrades     { get; set; }
    public double    WinRate        { get; set; }
    public double    GrossPnL       { get; set; }
    public double    TotalSlippage  { get; set; }
    public double    NetPnL         { get; set; }
    public double    MaxDrawdown    { get; set; }
    public double    Expectancy     { get; set; }
    public double    ProfitFactor   { get; set; }
    public double    AvgLatencyMs   { get; set; }
    public double    AvgSlippage    { get; set; }
    public string    Notes          { get; set; } = string.Empty;
    public List<TradeRecord> Trades { get; set; } = new();
}
