using System;
using System.Linq;
using MarketCore.Engine.Dataset;
using MarketCore.Engine.Detectors;
using MarketCore.Engine.Features;
using MarketCore.Engine.Patterns;

namespace MarketCore.Engine.Agents;

// ── Fase 11 — PatternAgent ────────────────────────────────────────────────

/// <summary>
/// Agente especializado em padrões históricos aprovados pelo Pattern Engine.
/// Encontra o padrão com maior Expectancy que satisfaz as condições correntes
/// e retorna score baseado na expectancy e win rate.
/// Retorna NeutralSignal quando não há padrões ativos ou nenhum satisfaz.
/// </summary>
public class PatternAgent : IAgent
{
    private readonly PatternRegistry  _registry;
    private readonly PatternEvaluator _evaluator;

    public string AgentId   => "PATTERN";
    public string AgentName => "Pattern Agent";

    public PatternAgent(PatternRegistry registry)
    {
        _registry  = registry;
        _evaluator = new PatternEvaluator();
    }

    public AgentSignal Evaluate(FeatureSnapshot snap, RegimeState regime)
    {
        var padroesAtivos = _registry.PadroesAtivos();
        if (!padroesAtivos.Any())
            return NeutralSignal();

        // Converter snapshot para DatasetRecord para o evaluador
        // Labels = null é aceitável — Satisfaz usa apenas Features
        var record = new DatasetRecord { Features = snap.ToMarketSnapshot() };

        // Encontrar padrão ativo com maior Expectancy que satisfaz as condições
        var melhor = padroesAtivos
            .Where(p => _evaluator.Satisfaz(record, p.Conditions))
            .OrderByDescending(p => p.TrainingStats.Expectancy)
            .FirstOrDefault();

        if (melhor == null)
            return NeutralSignal();

        // Score baseado na expectancy e win rate do padrão
        var exp      = melhor.TrainingStats.Expectancy;
        var winRate  = melhor.TrainingStats.WinRate;
        var baseScore = (int)Math.Min(100, exp * 10 + (winRate - 0.5) * 100);
        baseScore = Math.Max(0, baseScore);

        // Direção: positivo = compra, negativo = venda (baseado no retorno médio)
        var avgReturn = melhor.TrainingStats.AvgReturn1s;
        var score     = avgReturn >= 0 ? +baseScore : -baseScore;
        score         = Math.Clamp(score, -100, 100);

        return new AgentSignal
        {
            AgentId     = AgentId,
            Direction   = ScoreToDirection(score),
            Score       = score,
            Confidence  = (int)(winRate * 100),
            ValidUntil  = DateTime.UtcNow.AddMilliseconds(800),
            ReasonCodes = new[]
            {
                $"PATTERN_{melhor.PatternId}",
                $"EXP_{exp:F1}",
                $"WR_{winRate:P0}",
                $"CONDITIONS_{melhor.Conditions.Count}"
            }
        };
    }

    private AgentSignal NeutralSignal() => new AgentSignal
    {
        AgentId     = AgentId,
        Direction   = Direction.Neutral,
        Score       = 0,
        Confidence  = 0,
        ValidUntil  = DateTime.UtcNow.AddSeconds(1),
        ReasonCodes = new[] { "NO_PATTERN" }
    };

    private static Direction ScoreToDirection(int score)
        => score >  20 ? Direction.Buy
         : score < -20 ? Direction.Sell
         : Direction.Neutral;
}
