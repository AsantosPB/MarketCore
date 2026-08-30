using System;
using System.Collections.Generic;
using MarketCore.Engine.Detectors;
using MarketCore.Engine.Features;

namespace MarketCore.Engine.Agents;

// ── Fase 11 — AbsorptionAgent ─────────────────────────────────────────────

/// <summary>
/// Agente especializado em detecção de absorção de fluxo.
/// AbsorptionScore positivo = grandes compras absorvendo venda agressora (bullish).
/// AbsorptionScore negativo = grandes vendas absorvendo compra agressora (bearish).
/// Absorção com baixa velocidade (preço estático) é mais significativa.
/// </summary>
public class AbsorptionAgent : IAgent
{
    public string AgentId   => "ABSORPTION";
    public string AgentName => "Absorption Agent";

    public AgentSignal Evaluate(FeatureSnapshot snap, RegimeState regime)
    {
        int score = 0;

        // ── AbsorptionScore: +100 = absorção compradora máxima ──────────
        if      (snap.AbsorptionScore >  80) score += 50;
        else if (snap.AbsorptionScore >  60) score += 35;
        else if (snap.AbsorptionScore >  40) score += 20;
        else if (snap.AbsorptionScore < -80) score -= 50;
        else if (snap.AbsorptionScore < -60) score -= 35;
        else if (snap.AbsorptionScore < -40) score -= 20;

        // ── Absorção com preço parado é mais forte ──────────────────────
        // Velocity baixa + absorção alta → mercado "mastigando" a pressão
        if (Math.Abs(snap.Velocity) < 2 &&
            Math.Abs(snap.AbsorptionScore) > 50)
            score = (int)(score * 1.2);

        score = Math.Clamp(score, -100, 100);

        return new AgentSignal
        {
            AgentId     = AgentId,
            Direction   = ScoreToDirection(score),
            Score       = score,
            Confidence  = CalcularConfianca(snap),
            ValidUntil  = DateTime.UtcNow.AddMilliseconds(1000),
            ReasonCodes = GerarReasonCodes(snap)
        };
    }

    // ── Confiança: alta quando absorção forte + volatilidade baixa ──────
    private int CalcularConfianca(FeatureSnapshot snap)
    {
        var absScore = Math.Abs(snap.AbsorptionScore);
        var velBaixa = Math.Abs(snap.Velocity) < 2;
        var volBaixa = snap.Volatility30s < 50;

        if (absScore > 70 && velBaixa && volBaixa) return 92;
        if (absScore > 60 && velBaixa)             return 78;
        if (absScore > 40)                         return 60;
        return 35;
    }

    private string[] GerarReasonCodes(FeatureSnapshot snap)
    {
        var codes = new List<string>();

        if      (snap.AbsorptionScore >  60) codes.Add("ABSORPTION_BUY_STRONG");
        else if (snap.AbsorptionScore >  40) codes.Add("ABSORPTION_BUY");
        else if (snap.AbsorptionScore < -60) codes.Add("ABSORPTION_SELL_STRONG");
        else if (snap.AbsorptionScore < -40) codes.Add("ABSORPTION_SELL");

        if (Math.Abs(snap.Velocity) < 2 &&
            Math.Abs(snap.AbsorptionScore) > 50)
            codes.Add("STATIC_PRICE_ABSORPTION");

        if (snap.Volatility30s < 30) codes.Add("LOW_VOL_CONTEXT");

        return codes.Count > 0 ? codes.ToArray() : new[] { "ABSORPTION_NEUTRAL" };
    }

    private static Direction ScoreToDirection(int score)
        => score >  20 ? Direction.Buy
         : score < -20 ? Direction.Sell
         : Direction.Neutral;
}
