using System;
using System.Collections.Generic;
using MarketCore.Engine.Detectors;
using MarketCore.Engine.Features;

namespace MarketCore.Engine.Agents;

// ── Fase 11 — FlowAgent ───────────────────────────────────────────────────

/// <summary>
/// Agente especializado em fluxo de agressão e delta.
/// Analisa Delta1s, Delta5s, OFI e AggressionRatio para determinar
/// a pressão direcional do fluxo de ordens.
/// </summary>
public class FlowAgent : IAgent
{
    public string AgentId   => "FLOW";
    public string AgentName => "Flow Agent";

    public AgentSignal Evaluate(FeatureSnapshot snap, RegimeState regime)
    {
        int score = 0;

        // ── Delta 1s — peso maior (fluxo imediato) ─────────────────────
        if      (snap.Delta1s >  600) score += 40;
        else if (snap.Delta1s >  400) score += 25;
        else if (snap.Delta1s >  200) score += 15;
        else if (snap.Delta1s < -600) score -= 40;
        else if (snap.Delta1s < -400) score -= 25;
        else if (snap.Delta1s < -200) score -= 15;

        // ── Delta 5s — tendência mais longa ────────────────────────────
        if      (snap.Delta5s >  1000) score += 20;
        else if (snap.Delta5s >   500) score += 10;
        else if (snap.Delta5s < -1000) score -= 20;
        else if (snap.Delta5s <  -500) score -= 10;

        // ── OFI confirma o delta — normalizado em [-1.0, +1.0] ─────────
        if      (snap.Ofi1s >  0.60) score += 15;
        else if (snap.Ofi1s >  0.30) score +=  8;
        else if (snap.Ofi1s < -0.60) score -= 15;
        else if (snap.Ofi1s < -0.30) score -=  8;

        // ── Aggression ratio ────────────────────────────────────────────
        if      (snap.AggressionRatio > 0.70) score += 15;
        else if (snap.AggressionRatio > 0.60) score +=  8;
        else if (snap.AggressionRatio < 0.30) score -= 15;
        else if (snap.AggressionRatio < 0.40) score -=  8;

        score = Math.Clamp(score, -100, 100);

        return new AgentSignal
        {
            AgentId     = AgentId,
            Direction   = ScoreToDirection(score),
            Score       = score,
            Confidence  = CalcularConfianca(snap),
            ValidUntil  = DateTime.UtcNow.AddMilliseconds(800),
            ReasonCodes = GerarReasonCodes(snap)
        };
    }

    // ── Alta confiança quando delta e OFI convergem ─────────────────────
    private int CalcularConfianca(FeatureSnapshot snap)
    {
        bool deltaPositivo = snap.Delta1s > 0;
        bool ofiPositivo   = snap.Ofi1s   > 0;
        bool aggBull       = snap.AggressionRatio > 0.55;

        int convergentes = 0;
        if (deltaPositivo == (snap.Delta5s > 0)) convergentes++;
        if (deltaPositivo == ofiPositivo)         convergentes++;
        if (deltaPositivo == aggBull)             convergentes++;

        return convergentes switch
        {
            3 => 90,
            2 => 70,
            1 => 45,
            _ => 25
        };
    }

    private string[] GerarReasonCodes(FeatureSnapshot snap)
    {
        var codes = new List<string>();

        if      (snap.Delta1s >  400) codes.Add("DELTA1S_HIGH");
        else if (snap.Delta1s < -400) codes.Add("DELTA1S_LOW");

        if      (snap.Ofi1s >  0.40) codes.Add("OFI_CONFIRMA_BUY");
        else if (snap.Ofi1s < -0.40) codes.Add("OFI_CONFIRMA_SELL");

        if      (snap.AggressionRatio > 0.65) codes.Add("AGG_BUY");
        else if (snap.AggressionRatio < 0.35) codes.Add("AGG_SELL");

        if      (snap.Delta5s >  800) codes.Add("DELTA5S_TREND_UP");
        else if (snap.Delta5s < -800) codes.Add("DELTA5S_TREND_DOWN");

        return codes.Count > 0 ? codes.ToArray() : new[] { "FLOW_NEUTRAL" };
    }

    private static Direction ScoreToDirection(int score)
        => score >  20 ? Direction.Buy
         : score < -20 ? Direction.Sell
         : Direction.Neutral;
}
