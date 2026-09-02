using System;
using System.Collections.Generic;
using System.Linq;
using MarketCore.Engine.Storage;
using MarketCore.Engine.Dataset;

namespace MarketCore.Engine.Patterns;

/// <summary>
/// Avalia condições de padrões contra DatasetRecords e calcula métricas estatísticas.
/// Não usa IA — apenas threshold matching e estatística descritiva.
/// </summary>
public class PatternEvaluator
{
    // ── API pública ───────────────────────────────────────────────────────

    /// <summary>Retorna true se o record satisfaz TODAS as condições.</summary>
    public bool Satisfaz(DatasetRecord record, List<PatternCondition> conditions)
    {
        foreach (var cond in conditions)
        {
            double valor = GetFeatureValue(record.Features, cond.Feature);
            bool ok = cond.Operator switch
            {
                ">"  => valor >  cond.Threshold,
                "<"  => valor <  cond.Threshold,
                ">=" => valor >= cond.Threshold,
                "<=" => valor <= cond.Threshold,
                "==" => Math.Abs(valor - cond.Threshold) < 0.001,
                _     => false
            };
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>Calcula métricas estatísticas do subconjunto que satisfaz as condições.</summary>
    public PatternStats CalcularStats(List<DatasetRecord> records,
                                      List<PatternCondition> conditions)
    {
        var filtered = records.Where(r => Satisfaz(r, conditions)).ToList();
        if (filtered.Count < 10)
            return new PatternStats { SampleCount = filtered.Count };

        var returns = filtered.Select(r => r.Labels.FutureReturn5s).ToList();
        var wins    = returns.Where(r => r > +5).ToList();
        var losses  = returns.Where(r => r < -5).ToList();

        double winRate  = wins.Count   / (double)filtered.Count;
        double lossRate = losses.Count / (double)filtered.Count;
        double avgRet   = returns.Average();
        double avgWin   = wins.Count   > 0 ? wins.Average()   : 0;
        double avgLoss  = losses.Count > 0 ? losses.Average() : 0;
        double sumWins  = wins.Count   > 0 ? wins.Sum()       : 0;
        double sumLoss  = losses.Count > 0 ? Math.Abs(losses.Sum()) : 0;

        double expectancy   = (winRate * avgWin) - (lossRate * Math.Abs(avgLoss));
        double profitFactor = sumLoss > 0 ? sumWins / sumLoss : (sumWins > 0 ? double.MaxValue : 0);
        double sharpe       = StdDev(returns) > 0 ? avgRet / StdDev(returns) : 0;

        // WinRate por regime
        var byRegime = filtered
            .GroupBy(r => r.Features.Regime ?? "Unknown")
            .ToDictionary(
                g => g.Key,
                g => g.Count(r => r.Labels.FutureReturn5s > +5) / (double)g.Count());

        return new PatternStats
        {
            SampleCount    = filtered.Count,
            WinRate        = winRate,
            LossRate       = lossRate,
            AvgReturn1s    = avgRet,
            MedianReturn1s = Median(returns),
            Expectancy     = expectancy,
            ProfitFactor   = profitFactor,
            MfeAvg         = filtered.Average(r => r.Labels.Mfe5s),
            MaeAvg         = filtered.Average(r => r.Labels.Mae5s),
            Sharpe         = sharpe,
            MaxDrawdown    = 0,   // calculado opcionalmente via backtest
            AvgDuration    = filtered.Average(r => r.Labels.TimeTo20Pts),
            WinRateByRegime = byRegime
        };
    }

    /// <summary>Avalia um padrão já descoberto em novos dados (validação ou decay).</summary>
    public PatternStats AvaliarPadrao(DiscoveredPattern pattern,
                                      List<DatasetRecord> records)
        => CalcularStats(records, pattern.Conditions);

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>Retorna o valor de uma feature por nome. Retorna 0.0 para features desconhecidas.</summary>
    internal static double GetFeatureValue(MarketSnapshot s, string feature)
        => feature switch
        {
            "BookImbalance"   => s.BookImbalance,
            "Delta1s"         => s.Delta1s,
            "Delta5s"         => s.Delta5s,
            "Ofi1s"           => s.Ofi1s,
            "Ofi100ms"        => s.Ofi100ms,
            "Ofi500ms"        => s.Ofi500ms,
            "TradeRate"       => s.TradeRate,
            "VolumeRate"      => s.VolumeRate,
            "AbsorptionScore" => s.AbsorptionScore,
            "Velocity"        => s.Velocity,
            "Acceleration"    => s.Acceleration,
            "Volatility30s"   => s.Volatility30s,
            "DistanceVwap"    => s.DistanceVwap,
            "StackingScore"   => s.StackingScore,
            "PullingScore"    => s.PullingScore,
            "Delta100ms"      => s.Delta100ms,
            "Delta500ms"      => s.Delta500ms,
            "Microprice"      => s.Microprice,
            // AggressionRatio nao existe em MarketSnapshot — retorna 0 (feature nao salva no DuckDB)
            "AggressionRatio" => 0.0,
            _                  => 0.0
        };

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2.0
            : sorted[mid];
    }

    private static double StdDev(List<double> values)
    {
        if (values.Count < 2) return 0;
        double mean = values.Average();
        double variance = values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1);
        return Math.Sqrt(variance);
    }
}
