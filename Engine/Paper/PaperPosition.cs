using MarketCore.Engine.Storage;

namespace MarketCore.Engine.Paper;

/// <summary>
/// Fase 14 — Gerencia a posição durante o paper trading.
/// Rastreia MFE/MAE e gera TradeRecord ao fechar.
/// </summary>
public class PaperPosition
{
    public enum PositionSide { Flat, Long, Short }

    public PositionSide Side        { get; private set; } = PositionSide.Flat;
    public double       EntryPrice  { get; private set; }
    public int          Quantity    { get; private set; }
    public DateTime     EntryTime   { get; private set; }
    public double       Mfe         { get; private set; }
    public double       Mae         { get; private set; }
    public string       EntryReason { get; private set; } = string.Empty;

    public bool IsFlat => Side == PositionSide.Flat;

    public double UnrealizedPnL(double currentPrice) => Side switch
    {
        PositionSide.Long  =>  (currentPrice - EntryPrice) * Quantity,
        PositionSide.Short => -(currentPrice - EntryPrice) * Quantity,
        _                  => 0
    };

    public void AbrirLong(double price, int qty, DateTime time, string reason)
    {
        Side        = PositionSide.Long;
        EntryPrice  = price;
        Quantity    = qty;
        EntryTime   = time;
        Mfe         = 0;
        Mae         = 0;
        EntryReason = reason;
    }

    public void AbrirShort(double price, int qty, DateTime time, string reason)
    {
        Side        = PositionSide.Short;
        EntryPrice  = price;
        Quantity    = qty;
        EntryTime   = time;
        Mfe         = 0;
        Mae         = 0;
        EntryReason = reason;
    }

    public TradeRecord Fechar(double exitPrice, DateTime exitTime, string reason)
    {
        var grossPnl = Side == PositionSide.Long
            ?  (exitPrice - EntryPrice) * Quantity
            : -(exitPrice - EntryPrice) * Quantity;

        var record = new TradeRecord
        {
            TradeId         = Guid.NewGuid().ToString(),
            EntryTime       = EntryTime.Ticks,
            EntryPrice      = EntryPrice,
            Side            = Side == PositionSide.Long ? "BUY" : "SELL",
            Quantity        = Quantity,
            ExitTime        = exitTime.Ticks,
            ExitPrice       = exitPrice,
            GrossPnl        = grossPnl,
            Slippage        = 0,   // preenchido pelo caller com fill.Slippage
            NetPnl          = grossPnl,
            Mfe             = Mfe,
            Mae             = Mae,
            ExitReason      = reason,
            PatternId       = 0,
            StrategyVersion = "paper-v1"
        };

        Side = PositionSide.Flat;
        return record;
    }

    public void AtualizarMfeMae(double currentPrice)
    {
        if (IsFlat) return;
        var pnl = UnrealizedPnL(currentPrice);
        if (pnl > Mfe) Mfe = pnl;
        if (pnl < Mae) Mae = pnl;
    }
}
