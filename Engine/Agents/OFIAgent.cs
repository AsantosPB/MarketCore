using System;
using System.Collections.Generic;
using MarketCore.Engine.Detectors;
using MarketCore.Engine.Features;

namespace MarketCore.Engine.Agents;

// ── Fase 11 — OFIAgent ────────────────────────────────────────────────────

/// <summary>
/// Agente especializado em Order Flow Imbalance em múltiplas janelas.
/// Analisa a pressão direcional do fluxo em janelas de 100ms, 500ms e 1s.
/// Convergência de todas as janelas aumenta significativamente a confiança.
/// </summary>
public class OFIAgent : IAgent
{
    public string AgentId   => "OFI";
    public string AgentName => "OFI Agent";

    public AgentSignal Evaluate(FeatureSnapshot snap, RegimeState regime)
    {
        int score = 0;

        // ── OFI 100ms — curtíssimo prazo (mais ruidoso) ─────────────────
        if      (snap.Ofi100ms >  200) score += 20;
        else if (snap.Ofi100ms >  100) score += 10;
        else if (snap.Ofi100ms < -200) score -= 20;
        else if (snap.Ofi100ms < -100) score -= 10;

        // ── OFI 500ms — intermediário ───────────────────────────────────
        if      (snap.Ofi500ms >  400) score += 25;
        else if (snap.Ofi500ms >  200) score += 12;
        else if (snap.Ofi500ms < -400) score -= 25;
        else if (snap.Ofi500ms < -200) score -= 12;

        // ── OFI 1s — janela mais longa, peso maior ──────────────────────
        if      (snap.Ofi1s >  600) score += 35;
        else if (snap.Ofi1s >  300) score += 18;
        else if (snap.Ofi1s < -600) score -= 35;
        else if (snap.Ofi1s < -300) score -= 18;

        // ── Convergência de janelas amplifica o sinal ───────────────────
        bool convergindo =
            Math.Sign(snap.Ofi100ms) == Math.Sign(snap.Ofi500ms) &&
            Math.Sign(snap.Ofi500ms) == Math.Sign(snap.Ofi1s);

        if (convergindo && Math.Abs(score) > 30)
            score = (int)(score * 1.15);

        score = Math.Clamp(score, -100, 100);

        return new AgentSignal
        {
            AgentId     = AgentId,
            Direction   = ScoreToDirection(score),
            Score       = score,
            Confidence  = convergindo ? 85 : 55,
            ValidUntil  = DateTime.UtcNow.AddMilliseconds(600),
            ReasonCodes = GerarReasonCodes(snap, convergindo)
        };
    }

    private string[] GerarReasonCodes(FeatureSnapshot snap, bool convergindo)
    {
        var codes = new List<string>();

        if (convergindo) codes.Add("OFI_CONVERGENTE");

        if      (snap.Ofi1s >  400) codes.Add("OFI1S_STRONG_BUY");
        else if (snap.Ofi1s > 200)  codes.Add("OFI1S_BUY");
        else if (snap.Ofi1s < -400) codes.Add("OFI1S_STRONG_SELL");
        else if (snap.Ofi1s < -200) codes.Add("OFI1S_SELL");

        if      (snap.Ofi100ms >  150) codes.Add("OFI100MS_BUY");
        else if (snap.Ofi100ms < -150) codes.Add("OFI100MS_SELL");

        return codes.Count > 0 ? codes.ToArray() : new[] { "OFI_NEUTRAL" };
    }

    private static Direction ScoreToDirection(int score)
        => score >  20 ? Direction.Buy
         : score < -20 ? Direction.Sell
         : Direction.Neutral;
}
