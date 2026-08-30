namespace MarketCore.Engine.Risk;

public enum RiskCheckResult
{
    Approved   = 0,
    Blocked    = 1,
    KillSwitch = 2
}

public enum BlockReason
{
    None                    = 0,
    JaPositionado           = 1,
    LimitePerdaDiaria       = 2,
    MaxTradesDia            = 3,
    SpreadAlto              = 4,
    LiquidezBaixa           = 5,
    VolatilidadeAlta        = 6,
    ForaJanela              = 7,
    EventoEconomicoIminente = 8,
    Cooldown                = 9,
    FeedDesconectado        = 10,
    LatenciaAlta            = 11,
    BookStale               = 12,
    KillSwitchAtivo         = 13,
    PosicaoInconsistente    = 14
}

public class RiskDecision
{
    public RiskCheckResult Result    { get; set; }
    public BlockReason     Reason    { get; set; }
    public string          Detail    { get; set; } = string.Empty;
    public DateTime        Timestamp { get; set; }
}

public class RiskConfig
{
    public int    MaxPosition               { get; set; } = 1;
    public double MaxDailyLossBrl           { get; set; } = 1000;
    public int    MaxTradesPerDay           { get; set; } = 20;
    public double MaxSpreadPoints           { get; set; } = 10;
    public int    CooldownAfterLossMs       { get; set; } = 60000;
    public int    BlockMinutesBeforeCritical { get; set; } = 30;
    public int    WaitSecondsAfterCritical  { get; set; } = 5;
    public double MaxVolatility             { get; set; } = 1.5;
    public int    MaxLatencyMs              { get; set; } = 50;
    public int    BookStaleSeconds          { get; set; } = 10;
}

public class RiskState
{
    public bool      KillSwitchAtivo        { get; set; }
    public string    KillSwitchMotivo       { get; set; } = string.Empty;
    public double    PerdaDiariaAcumulada   { get; set; }
    public int       TradesDoDia            { get; set; }
    public DateTime? UltimoLossAt           { get; set; }

    public bool EmCooldown
        => UltimoLossAt.HasValue &&
           (DateTime.UtcNow - UltimoLossAt.Value).TotalMilliseconds < 60000;
}
