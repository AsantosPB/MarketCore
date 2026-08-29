using System;
using System.Collections.Generic;
using System.Linq;
using MarketCore.Engine.Features;

namespace MarketCore.Engine.Detectors;

/// <summary>
/// Detecta eventos relevantes de microestrutura a partir do FeatureSnapshot.
/// Chamado a cada 100 ms pelo FeatureEngine.CalcularSnapshot().
/// </summary>
public class EventDetector
{
    // ── Evento (uso standalone) ───────────────────────────────────────────
    public event Action<MarketEvent>? OnEvent;

    // ── Limiares configuráveis ────────────────────────────────────────────
    public double AggressionSpikePercentile { get; set; } = 90.0;
    public double BookImbalanceThreshold    { get; set; } = 0.60;
    public double AbsorptionThreshold       { get; set; } = 60.0;
    public int    LargeTradeContracts       { get; set; } = 50;

    // ── Histórico para cálculo de percentis ───────────────────────────────
    private readonly RingBuffer<double> _tradeRateHistory;
    private readonly RingBuffer<double> _volumeRateHistory;

    // ── Contador de streak de alta taxa de agressão ───────────────────────
    private int _consecutiveHighTradeRate;

    public EventDetector()
    {
        // 300 amostras = 30 s de histórico (snapshot a cada 100 ms)
        _tradeRateHistory  = new RingBuffer<double>(300);
        _volumeRateHistory = new RingBuffer<double>(300);
    }

    // ── API pública ───────────────────────────────────────────────────────

    /// <summary>
    /// Avalia o snapshot e retorna a lista de eventos detectados (pode ser vazia).
    /// Atualiza o histórico interno a cada chamada.
    /// </summary>
    public List<MarketEvent> Avaliar(FeatureSnapshot snap)
    {
        _tradeRateHistory.Push(snap.TradeRate);
        _volumeRateHistory.Push(snap.VolumeRate);

        var eventos = new List<MarketEvent>();
        Adicionar(eventos, DetectarAggressionSpike(snap));
        Adicionar(eventos, DetectarBookImbalance(snap));
        Adicionar(eventos, DetectarAbsorption(snap));
        Adicionar(eventos, DetectarPriceAcceleration(snap));
        Adicionar(eventos, DetectarVolumeSpike(snap));
        Adicionar(eventos, DetectarDeltaDivergence(snap));
        Adicionar(eventos, DetectarTradeRateSpike(snap));


        return eventos;
    }

    // ── Auxiliar ──────────────────────────────────────────────────────────

    private static void Adicionar(List<MarketEvent> lista, MarketEvent? ev)
    {
        if (ev != null) lista.Add(ev);
    }

    // ── Detectores individuais ────────────────────────────────────────────

    /// <summary>TradeRate > p90 histórico mantido por 5+ snapshots consecutivos (~500 ms).</summary>
    private MarketEvent? DetectarAggressionSpike(FeatureSnapshot snap)
    {
        var hist = _tradeRateHistory.GetAll();
        if (hist.Length < 10) return null;

        double p90 = Percentil(hist, AggressionSpikePercentile);
        if (snap.TradeRate > p90)
            _consecutiveHighTradeRate++;
        else
        {
            _consecutiveHighTradeRate = 0;
            return null;
        }

        if (_consecutiveHighTradeRate < 5) return null;

        double magnitude = Math.Min(100, (snap.TradeRate / Math.Max(p90, 1)) * 50);
        return new MarketEvent
        {
            Type      = MarketEventType.AggressionSpike,
            Timestamp = DateTime.Now,
            Magnitude = magnitude,
            Detail    = $"TradeRate={snap.TradeRate:F1} trades/s (p90={p90:F1}, streak={_consecutiveHighTradeRate})"
        };
    }

    /// <summary>|BookImbalance| > limiar configurável (default 0.60).</summary>
    private MarketEvent? DetectarBookImbalance(FeatureSnapshot snap)
    {
        if (Math.Abs(snap.BookImbalance) <= BookImbalanceThreshold) return null;
        string lado = snap.BookImbalance > 0 ? "BID" : "ASK";
        double magnitude = Math.Min(100, Math.Abs(snap.BookImbalance) * 100);
        return new MarketEvent
        {
            Type      = MarketEventType.BookImbalance,
            Timestamp = DateTime.Now,
            Magnitude = magnitude,
            Detail    = $"BookImbalance={snap.BookImbalance:F3} (pressão={lado})"
        };
    }

    /// <summary>|AbsorptionScore| > limiar configurável (default 60).</summary>
    private MarketEvent? DetectarAbsorption(FeatureSnapshot snap)
    {
        if (Math.Abs(snap.AbsorptionScore) <= AbsorptionThreshold) return null;
        string tipo = snap.AbsorptionScore < 0 ? "vendedora" : "compradora";
        return new MarketEvent
        {
            Type      = MarketEventType.Absorption,
            Timestamp = DateTime.Now,
            Magnitude = Math.Abs(snap.AbsorptionScore),
            Detail    = $"Absorção {tipo} score={snap.AbsorptionScore:F1}"
        };
    }

    /// <summary>|Acceleration| > 5 pontos/s².</summary>
    private static MarketEvent? DetectarPriceAcceleration(FeatureSnapshot snap)
    {
        const double limiar = 5.0;
        if (Math.Abs(snap.Acceleration) <= limiar) return null;
        string dir = snap.Acceleration > 0 ? "alta" : "queda";
        double magnitude = Math.Min(100, Math.Abs(snap.Acceleration) * 10);
        return new MarketEvent
        {
            Type      = MarketEventType.PriceAcceleration,
            Timestamp = DateTime.Now,
            Magnitude = magnitude,
            Detail    = $"Aceleração de {dir}={snap.Acceleration:F2} pts/s²"
        };
    }

    /// <summary>VolumeRate > p90 do histórico.</summary>
    private MarketEvent? DetectarVolumeSpike(FeatureSnapshot snap)
    {
        var hist = _volumeRateHistory.GetAll();
        if (hist.Length < 10) return null;

        double p90 = Percentil(hist, 90.0);
        if (snap.VolumeRate <= p90) return null;

        double magnitude = Math.Min(100, (snap.VolumeRate / Math.Max(p90, 1)) * 50);
        return new MarketEvent
        {
            Type      = MarketEventType.VolumeSpike,
            Timestamp = DateTime.Now,
            Magnitude = magnitude,
            Detail    = $"VolumeRate={snap.VolumeRate:F0} cts/s (p90={p90:F0})"
        };
    }

    /// <summary>Delta1s fortemente positivo + Velocity negativa (bearish) ou inverso (bullish).</summary>
    private static MarketEvent? DetectarDeltaDivergence(FeatureSnapshot snap)
    {
        const long limiar = 20;
        bool bearish = snap.Delta1s >  limiar && snap.Velocity < 0;
        bool bullish = snap.Delta1s < -limiar && snap.Velocity > 0;
        if (!bearish && !bullish) return null;

        string tipo = bearish
            ? "bearish (compras sem alta)"
            : "bullish (vendas sem queda)";
        double magnitude = Math.Min(100, Math.Abs(snap.Delta1s) / 100.0 * 100);
        return new MarketEvent
        {
            Type      = MarketEventType.DeltaDivergence,
            Timestamp = DateTime.Now,
            Magnitude = magnitude,
            Detail    = $"Divergência {tipo} — Delta1s={snap.Delta1s} Velocity={snap.Velocity:F2}"
        };
    }

    /// <summary>TradeRate > 2× a média histórica.</summary>
    private MarketEvent? DetectarTradeRateSpike(FeatureSnapshot snap)
    {
        var hist = _tradeRateHistory.GetAll();
        if (hist.Length < 10) return null;

        double media = hist.Average();
        if (snap.TradeRate <= 2.0 * media) return null;

        double magnitude = Math.Min(100, (snap.TradeRate / Math.Max(media, 1)) * 25);
        return new MarketEvent
        {
            Type      = MarketEventType.TradeRateSpike,
            Timestamp = DateTime.Now,
            Magnitude = magnitude,
            Detail    = $"TradeRate={snap.TradeRate:F1} (2×média={2 * media:F1})"
        };
    }

    // ── Utilitários ───────────────────────────────────────────────────────

    private static double Percentil(double[] data, double percentil)
    {
        if (data.Length == 0) return 0;
        var sorted = data.OrderBy(x => x).ToArray();
        int idx = (int)Math.Ceiling(percentil / 100.0 * sorted.Length) - 1;
        idx = Math.Max(0, Math.Min(idx, sorted.Length - 1));
        return sorted[idx];
    }
}
