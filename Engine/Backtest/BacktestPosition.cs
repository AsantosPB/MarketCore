using System;
using MarketCore.Engine.Storage;

namespace MarketCore.Engine.Backtest;

// ── Fase 10 — BacktestPosition ────────────────────────────────────────────

/// <summary>
/// Gerencia a posição aberta durante o backtest.
/// Rastreia entrada, MFE/MAE e gera TradeRecord ao fechar.
/// </summary>
public class BacktestPosition
{
    public enum PositionSide { Flat, Long, Short }

    // ── Estado da posição ─────────────────────────────────────────────────
    public PositionSide Side        { get; private set; } = PositionSide.Flat;
    public double       EntryPrice  { get; private set; }
    public int          Quantity    { get; private set; }
    public double       RealizedPnL { get; private set; }
    public DateTime     EntryTime   { get; private set; }

    /// <summary>Máxima excursão favorável (pts) desde a entrada.</summary>
    public double       Mfe         { get; private set; }

    /// <summary>Máxima excursão adversa (pts, valor negativo) desde a entrada.</summary>
    public double       Mae         { get; private set; }

    public bool IsFlat => Side == PositionSide.Flat;

    // ── P&L não realizado ─────────────────────────────────────────────────
    /// <summary>P&amp;L não realizado (pts × contratos) para o preço corrente.</summary>
    public double UnrealizedPnL(double currentPrice) => Side switch
    {
        PositionSide.Long  =>  (currentPrice - EntryPrice) * Quantity,
        PositionSide.Short => -(currentPrice - EntryPrice) * Quantity,
        _                  => 0
    };

    // ── Abertura ──────────────────────────────────────────────────────────
    /// <summary>Abre posição comprada ao preço e hora informados.</summary>
    public void AbrirLong(double price, int qty, DateTime time)
    {
        Side       = PositionSide.Long;
        EntryPrice = price;
        Quantity   = qty;
        EntryTime  = time;
        Mfe        = 0;
        Mae        = 0;
    }

    /// <summary>Abre posição vendida ao preço e hora informados.</summary>
    public void AbrirShort(double price, int qty, DateTime time)
    {
        Side       = PositionSide.Short;
        EntryPrice = price;
        Quantity   = qty;
        EntryTime  = time;
        Mfe        = 0;
        Mae        = 0;
    }

    // ── Fechamento ────────────────────────────────────────────────────────
    /// <summary>
    /// Fecha a posição e retorna o TradeRecord completo.
    /// Slippage é preenchido pelo BacktestEngine via OrderFill após o retorno.
    /// </summary>
    public TradeRecord Fechar(double exitPrice, DateTime exitTime, string reason)
    {
        double grossPnl = Side == PositionSide.Long
            ? (exitPrice - EntryPrice) * Quantity
            : -(exitPrice - EntryPrice) * Quantity;

        var record = new TradeRecord
        {
            TradeId         = Guid.NewGuid().ToString(),
            EntryTime       = EntryTime.Ticks,
            EntryPrice      = EntryPrice,
            Side            = Side.ToString(),
            Quantity        = Quantity,
            ExitTime        = exitTime.Ticks,
            ExitPrice       = exitPrice,
            GrossPnl        = grossPnl,
            Slippage        = 0,        // atualizado pelo BacktestEngine via OrderFill
            NetPnl          = grossPnl, // atualizado após subtração do slippage
            Mfe             = Mfe,
            Mae             = Mae,
            ExitReason      = reason,
            StrategyVersion = "Fase10"
        };

        RealizedPnL += grossPnl;
        Side         = PositionSide.Flat;
        return record;
    }

    // ── Atualização de MFE/MAE ────────────────────────────────────────────
    /// <summary>
    /// Atualiza MFE e MAE com o preço corrente.
    /// Deve ser chamado a cada snapshot enquanto há posição aberta.
    /// </summary>
    public void AtualizarMfeMae(double currentPrice)
    {
        var unrealized = UnrealizedPnL(currentPrice);
        if (unrealized > Mfe) Mfe = unrealized;
        if (unrealized < Mae) Mae = unrealized;
    }
}
