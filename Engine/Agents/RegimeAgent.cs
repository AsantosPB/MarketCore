using System;
using System.Collections.Generic;
using MarketCore.Engine.Detectors;
using MarketCore.Engine.Features;

namespace MarketCore.Engine.Agents;

// ── Fase 11 — RegimeAgent ─────────────────────────────────────────────────

/// <summary>
/// Agente especializado em contexto de regime e janela temporal.
/// Modifica (amplifica ou atenua) o score com base no regime detectado,
/// na janela de negociação e na presença de eventos econômicos.
/// Bloqueia completamente o sinal em leilão e durante eventos de alto impacto.
/// </summary>
public class RegimeAgent : IAgent
{
    public string AgentId   => "REGIME";
    public string AgentName => "Regime Agent";

    public AgentSignal Evaluate(FeatureSnapshot snap, RegimeState regime)
    {
        int score = 0;
        var reasons = new List<string>();

        // ── Regime de mercado ────────────────────────────────────────────
        switch (regime?.Regime)
        {
            case MarketRegime.TrendUp:
                score += 30;
                reasons.Add("TREND_UP");
                break;

            case MarketRegime.TrendDown:
                score -= 30;
                reasons.Add("TREND_DOWN");
                break;

            case MarketRegime.HighVol:
                // Alta volatilidade — reduzir convicção
                score = (int)(score * 0.7);
                reasons.Add("HIGH_VOL_FILTER");
                break;

            case MarketRegime.Transition:
                // Transição — sinal fraco, direto não operável
                score = (int)(score * 0.5);
                reasons.Add("TRANSITION_FILTER");
                break;

            case MarketRegime.Range:
                reasons.Add("RANGE_CONTEXT");
                break;

            case MarketRegime.Breakout:
                // Breakout pode amplificar em qualquer direção
                score = (int)(score * 1.1);
                reasons.Add("BREAKOUT_CONTEXT");
                break;
        }

        // ── Janela temporal ──────────────────────────────────────────────
        switch (snap.TimeWindow)
        {
            case "Leilao":
                score = 0;  // nunca operar no leilão — liquidez fictícia
                reasons.Add("LEILAO_BLOCKED");
                break;

            case "Abertura":
                // Abertura tem spread alto e volatilidade
                score = (int)(score * 0.8);
                reasons.Add("ABERTURA_REDUCED");
                break;

            case "DadosUSA":
                // Aguardar dados americanos — spread imprevisível
                score = (int)(score * 0.6);
                reasons.Add("DADOS_USA_REDUCED");
                break;

            case "Fechamento":
                // Liquidez caindo — cautela
                score = (int)(score * 0.85);
                reasons.Add("FECHAMENTO_REDUCED");
                break;
        }

        // ── Evento econômico de alto impacto ─────────────────────────────
        if (snap.HasEconomicEvent && snap.EventImpact >= 2)
        {
            score = 0;  // bloquear totalmente
            reasons.Add("ECONOMIC_EVENT_BLOCKED");
        }

        // ── Distância do VWAP — cautela quando muito longe ───────────────
        if (snap.DistanceVwap > 100)
        {
            score = (int)(score * 0.8);
            reasons.Add("FAR_FROM_VWAP");
        }

        score = Math.Clamp(score, -100, 100);

        return new AgentSignal
        {
            AgentId     = AgentId,
            Direction   = ScoreToDirection(score),
            Score       = score,
            Confidence  = (int)(regime?.Confidence ?? 50),
            ValidUntil  = DateTime.UtcNow.AddSeconds(2),
            ReasonCodes = reasons.ToArray()
        };
    }

    private static Direction ScoreToDirection(int score)
        => score >  20 ? Direction.Buy
         : score < -20 ? Direction.Sell
         : Direction.Neutral;
}
