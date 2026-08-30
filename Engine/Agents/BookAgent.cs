using System;
using System.Collections.Generic;
using MarketCore.Engine.Detectors;
using MarketCore.Engine.Features;

namespace MarketCore.Engine.Agents;

// ── Fase 11 — BookAgent ───────────────────────────────────────────────────

/// <summary>
/// Agente especializado em pressão do book de ofertas.
/// Analisa BookImbalance, DepthImbalance, MicropriceDelta,
/// StackingScore e PullingScore para detectar pressão estrutural.
/// </summary>
public class BookAgent : IAgent
{
    public string AgentId   => "BOOK";
    public string AgentName => "Book Agent";

    public AgentSignal Evaluate(FeatureSnapshot snap, RegimeState regime)
    {
        int score = 0;

        // ── Book imbalance — sinal principal ───────────────────────────
        if      (snap.BookImbalance >  0.60) score += 35;
        else if (snap.BookImbalance >  0.40) score += 20;
        else if (snap.BookImbalance >  0.20) score += 10;
        else if (snap.BookImbalance < -0.60) score -= 35;
        else if (snap.BookImbalance < -0.40) score -= 20;
        else if (snap.BookImbalance < -0.20) score -= 10;

        // ── Depth imbalance ─────────────────────────────────────────────
        if      (snap.DepthImbalance >  0.50) score += 20;
        else if (snap.DepthImbalance >  0.25) score += 10;
        else if (snap.DepthImbalance < -0.50) score -= 20;
        else if (snap.DepthImbalance < -0.25) score -= 10;

        // ── Microprice delta vs último preço ────────────────────────────
        var microDiff = snap.MicropriceDelta;
        if      (microDiff >  3) score += 20;
        else if (microDiff >  1) score += 10;
        else if (microDiff < -3) score -= 20;
        else if (microDiff < -1) score -= 10;

        // ── Stacking e pulling (manipulação de book) ────────────────────
        if (snap.StackingScore >  40) score += 15;  // ofertas sendo empilhadas no ask (pressão vendedora comprando)
        if (snap.PullingScore  < -40) score -= 15;  // bids sendo removidos (pressão vendedora)

        score = Math.Clamp(score, -100, 100);

        return new AgentSignal
        {
            AgentId     = AgentId,
            Direction   = ScoreToDirection(score),
            Score       = score,
            Confidence  = CalcularConfianca(snap),
            ValidUntil  = DateTime.UtcNow.AddMilliseconds(500),
            ReasonCodes = GerarReasonCodes(snap)
        };
    }

    // ── Confiança maior quando imbalance e microprice convergem ─────────
    private int CalcularConfianca(FeatureSnapshot snap)
    {
        bool bookBull  = snap.BookImbalance  > 0;
        bool depthBull = snap.DepthImbalance > 0;
        bool microBull = snap.MicropriceDelta > 0;

        int acordo = 0;
        if (bookBull == depthBull) acordo++;
        if (bookBull == microBull) acordo++;

        return acordo switch
        {
            2 => 85,
            1 => 60,
            _ => 30
        };
    }

    private string[] GerarReasonCodes(FeatureSnapshot snap)
    {
        var codes = new List<string>();

        if      (snap.BookImbalance >  0.40) codes.Add("BOOK_IMBALANCE_BUY");
        else if (snap.BookImbalance < -0.40) codes.Add("BOOK_IMBALANCE_SELL");

        if      (snap.DepthImbalance >  0.30) codes.Add("DEPTH_BUY");
        else if (snap.DepthImbalance < -0.30) codes.Add("DEPTH_SELL");

        if      (snap.MicropriceDelta >  2) codes.Add("MICROPRICE_UP");
        else if (snap.MicropriceDelta < -2) codes.Add("MICROPRICE_DOWN");

        if (snap.StackingScore >  40) codes.Add("STACKING");
        if (snap.PullingScore  < -40) codes.Add("PULLING");

        return codes.Count > 0 ? codes.ToArray() : new[] { "BOOK_NEUTRAL" };
    }

    private static Direction ScoreToDirection(int score)
        => score >  20 ? Direction.Buy
         : score < -20 ? Direction.Sell
         : Direction.Neutral;
}
