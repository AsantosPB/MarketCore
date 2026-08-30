using System;
using System.Collections.Generic;

namespace MarketCore.Engine.Patterns;

/// <summary>Ciclo de vida de um padrão descoberto pelo Pattern Engine.</summary>
public enum PatternStatus
{
    Discovered  = 0,
    Validating  = 1,
    Approved    = 2,
    Rejected    = 3,
    Paper       = 4,
    Live        = 5,
    Monitoring  = 6,
    Decay       = 7,
    Deprecated  = 8
}

/// <summary>Condição simples: Feature Operador Threshold (ex.: BookImbalance > 0.60).</summary>
public class PatternCondition
{
    public string Feature   { get; set; } = string.Empty;
    public string Operator  { get; set; } = string.Empty;
    public double Threshold { get; set; }
}

/// <summary>Métricas estatísticas de um conjunto de registros que satisfazem um padrão.</summary>
public class PatternStats
{
    public int    SampleCount    { get; set; }
    public double WinRate        { get; set; }  // % retorno1s > +5pts
    public double LossRate       { get; set; }
    public double AvgReturn1s    { get; set; }
    public double MedianReturn1s { get; set; }
    public double Expectancy     { get; set; }  // WinRate*AvgWin - LossRate*AvgLoss
    public double ProfitFactor   { get; set; }  // somaGanhos / somaPercas
    public double MfeAvg         { get; set; }
    public double MaeAvg         { get; set; }
    public double Sharpe         { get; set; }
    public double MaxDrawdown    { get; set; }
    public double AvgDuration    { get; set; }  // ms até resolução
    public Dictionary<string, double> WinRateByRegime { get; set; } = new();
}

/// <summary>Padrão descoberto com condições, métricas por conjunto e lifecycle.</summary>
public class DiscoveredPattern
{
    public int                    PatternId        { get; set; }
    public int                    Version          { get; set; }
    public DateTime               CreatedAt        { get; set; }
    public List<PatternCondition> Conditions       { get; set; } = new();
    public PatternStats           TrainingStats    { get; set; } = new();
    public PatternStats           ValidationStats  { get; set; } = new();
    public PatternStats           OutOfSampleStats { get; set; } = new();
    public string                 TrainingPeriod   { get; set; } = string.Empty;
    public string                 ValidationPeriod { get; set; } = string.Empty;
    public string                 TestPeriod       { get; set; } = string.Empty;
    public PatternStatus          Status           { get; set; }
    public string                 PrimaryRegime    { get; set; } = string.Empty;

    // Decay monitoring
    public double RecentWinRate    { get; set; }
    public double DiscoveryWinRate { get; set; }

    /// <summary>
    /// True se a win rate recente caiu mais de 15% em relação à win rate na descoberta.
    /// </summary>
    public bool InDecay => RecentWinRate < DiscoveryWinRate * 0.85;
}
