using System;
using System.Collections.Generic;
using MarketCore.Engine.Replay;
using MarketCore.Engine.Storage;

namespace MarketCore.Engine.Backtest;

// ── Fase 10 — BacktestModels ──────────────────────────────────────────────

/// <summary>
/// Resultado consolidado de uma sessão de backtest.
/// Separa o alpha da estratégia do alpha da execução para diagnóstico
/// de degradação por slippage e latência.
/// </summary>
public class BacktestResult
{
    public DateTime          Date           { get; set; }
    public TimeSpan          Duration       { get; set; }

    // ── Contagens ─────────────────────────────────────────────────────────
    public int               TotalTrades    { get; set; }
    public int               WinTrades      { get; set; }
    public int               LossTrades     { get; set; }

    // ── Métricas de performance ───────────────────────────────────────────
    public double            WinRate        { get; set; }
    public double            GrossPnL       { get; set; }
    public double            TotalSlippage  { get; set; }
    public double            NetPnL         { get; set; }
    public double            MaxDrawdown    { get; set; }
    public double            Expectancy     { get; set; }
    public double            ProfitFactor   { get; set; }
    public double            Sharpe         { get; set; }

    // ── Métricas de execução ──────────────────────────────────────────────
    public double            AvgLatencyMs   { get; set; }
    public double            AvgSlippage    { get; set; }

    // ── Alpha decomposition ───────────────────────────────────────────────
    /// <summary>
    /// PnL bruto calculado usando o preço do sinal (sem slippage/latência).
    /// Representa o alpha puro da estratégia.
    /// </summary>
    public double            StrategyAlpha  { get; set; }

    /// <summary>
    /// Diferença entre NetPnL e StrategyAlpha.
    /// Valor negativo indica perda de alpha na execução (slippage + latência).
    /// ExecutionAlpha = NetPnL − StrategyAlpha
    /// </summary>
    public double            ExecutionAlpha { get; set; }

    // ── Histórico ─────────────────────────────────────────────────────────
    public List<TradeRecord> Trades         { get; set; } = new();
}

/// <summary>
/// Configuração de uma sessão de backtest.
/// Passada para BacktestEngine.ExecutarAsync().
/// </summary>
public class BacktestConfig
{
    /// <summary>Data do pregão a ser reprocessado.</summary>
    public DateTime    Date           { get; set; }

    /// <summary>Capital inicial da sessão (pts de índice).</summary>
    public double      InitialCapital { get; set; } = 10_000;

    /// <summary>Perda máxima diária — interrompe o backtest se atingida.</summary>
    public double      MaxDailyLoss   { get; set; } = 1_000;

    /// <summary>Número máximo de operações permitidas na sessão.</summary>
    public int         MaxTrades      { get; set; } = 20;

    /// <summary>Latência mínima simulada em ms.</summary>
    public int         MinLatencyMs   { get; set; } = 50;

    /// <summary>Latência máxima simulada em ms.</summary>
    public int         MaxLatencyMs   { get; set; } = 200;

    /// <summary>Fração do spread aplicada como slippage (0..1).</summary>
    public double      SlippageFactor { get; set; } = 0.5;

    /// <summary>Velocidade de replay. Default: MaxSpeed para backtest rápido.</summary>
    public ReplaySpeed Speed          { get; set; } = ReplaySpeed.MaxSpeed;
}
