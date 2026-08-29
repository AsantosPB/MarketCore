using System;
using System.Collections.Generic;
using System.Linq;
using MarketCore.Engine.Features;

namespace MarketCore.Engine.Detectors;

/// <summary>
/// Classifica o regime atual do mercado a partir do FeatureSnapshot.
/// Mantém 600 snapshots de histórico (~60 s) para análise de tendência.
/// Chamado a cada 100 ms pelo FeatureEngine.CalcularSnapshot().
/// </summary>
public class RegimeDetector
{
    // ── Evento ────────────────────────────────────────────────────────────
    public event Action<RegimeState>? OnRegimeChange;

    // ── Estado atual ──────────────────────────────────────────────────────
    private RegimeState _current;

    // ── Histórico ─────────────────────────────────────────────────────────
    private readonly RingBuffer<FeatureSnapshot> _history;
    private readonly RingBuffer<double>          _volatilityHistory;

    // ── Streaks de velocidade ─────────────────────────────────────────────
    private int _positiveVelocityStreak;  // snapshots consecutivos com Velocity > limiar
    private int _negativeVelocityStreak;  // snapshots consecutivos com Velocity < -limiar
    private int _lowVelocityStreak;       // snapshots consecutivos com |Velocity| <= limiar

    // Limiar de velocidade para distinguir tendência de range (pontos/s)
    private const double VelocityLimiar = 0.5;

    public RegimeDetector()
    {
        // 600 snapshots = 60 s de histórico a 100 ms por snapshot
        _history           = new RingBuffer<FeatureSnapshot>(600);
        _volatilityHistory = new RingBuffer<double>(600);
        _current           = new RegimeState
        {
            Regime     = MarketRegime.Unknown,
            Confidence = 0,
            Since      = DateTime.Now,
            Previous   = MarketRegime.Unknown
        };
    }

    // ── API pública ───────────────────────────────────────────────────────

    public RegimeState Estado => _current;

    /// <summary>
    /// Avalia o snapshot, classifica o regime atual e atualiza o estado interno.
    /// Dispara OnRegimeChange quando o regime muda.
    /// </summary>
    public RegimeState Avaliar(FeatureSnapshot snap)
    {
        _history.Push(snap);
        _volatilityHistory.Push(snap.Volatility30s);

        // Atualizar streaks de velocidade
        if (snap.Velocity > VelocityLimiar)
        {
            _positiveVelocityStreak++;
            _negativeVelocityStreak = 0;
            _lowVelocityStreak      = 0;
        }
        else if (snap.Velocity < -VelocityLimiar)
        {
            _negativeVelocityStreak++;
            _positiveVelocityStreak = 0;
            _lowVelocityStreak      = 0;
        }
        else
        {
            _lowVelocityStreak++;
            _positiveVelocityStreak = 0;
            _negativeVelocityStreak = 0;
        }

        // Coletar candidatos
        var candidatos = new List<(MarketRegime, double)>();
        Adicionar(candidatos, ClassificarTrendUp(snap));
        Adicionar(candidatos, ClassificarTrendDown(snap));
        Adicionar(candidatos, ClassificarRange(snap));
        Adicionar(candidatos, ClassificarHighVol(snap));
        Adicionar(candidatos, ClassificarLowVol(snap));
        Adicionar(candidatos, ClassificarBreakout(snap));

        var novoRegime = ElegerRegime(candidatos);
        double confianca = candidatos.Count > 0
            ? candidatos.Max(c => c.Item2)
            : 0;

        // Sobrepor com Transition se estiver em transição recente
        if (EmTransicao() && novoRegime != _current.Regime
                          && novoRegime != MarketRegime.Unknown
                          && _current.Regime != MarketRegime.Unknown)
        {
            novoRegime = MarketRegime.Transition;
            confianca  = Math.Min(confianca, 50);
        }

        // Persistir estado
        if (novoRegime != _current.Regime)
        {
            var anterior = _current;
            _current = new RegimeState
            {
                Regime     = novoRegime,
                Confidence = confianca,
                Since      = DateTime.Now,
                Previous   = anterior.Regime
            };
            OnRegimeChange?.Invoke(_current);
        }
        else
        {
            // Atualiza confiança sem mudar o Regime
            _current = new RegimeState
            {
                Regime     = _current.Regime,
                Confidence = confianca,
                Since      = _current.Since,
                Previous   = _current.Previous
            };
        }

        return _current;
    }

    // ── Classificadores individuais ───────────────────────────────────────

    /// <summary>Velocity > 0 por 10+ snapshots, Delta5s > +300, Price > Vwap.</summary>
    private (MarketRegime, double)? ClassificarTrendUp(FeatureSnapshot snap)
    {
        if (_positiveVelocityStreak < 10) return null;
        if (snap.Delta5s            <= 300) return null;
        if (snap.Vwap > 0 && snap.Price <= snap.Vwap) return null;

        double confianca = Math.Min(100,
            30 + (_positiveVelocityStreak - 10) * 2.0 +
            Math.Min(40, snap.Delta5s / 50.0));
        return (MarketRegime.TrendUp, confianca);
    }

    /// <summary>Velocity < 0 por 10+ snapshots, Delta5s < -300, Price < Vwap.</summary>
    private (MarketRegime, double)? ClassificarTrendDown(FeatureSnapshot snap)
    {
        if (_negativeVelocityStreak < 10) return null;
        if (snap.Delta5s            >= -300) return null;
        if (snap.Vwap > 0 && snap.Price >= snap.Vwap) return null;

        double confianca = Math.Min(100,
            30 + (_negativeVelocityStreak - 10) * 2.0 +
            Math.Min(40, -snap.Delta5s / 50.0));
        return (MarketRegime.TrendDown, confianca);
    }

    /// <summary>|Velocity| pequena por 20+ snapshots, |Delta5s| < 200, Price oscilando no Vwap.</summary>
    private (MarketRegime, double)? ClassificarRange(FeatureSnapshot snap)
    {
        if (_lowVelocityStreak < 20) return null;
        if (Math.Abs(snap.Delta5s) >= 200) return null;

        double distVwap  = snap.Vwap > 0 ? Math.Abs(snap.DistanceVwap) : 0;
        double confianca = Math.Min(100,
            40 + (_lowVelocityStreak - 20) * 1.0 - distVwap * 2.0);
        confianca = Math.Max(0, confianca);
        if (confianca < 30) return null;
        return (MarketRegime.Range, confianca);
    }

    /// <summary>Volatility30s > p90 histórico e TradeRate acima da média.</summary>
    private (MarketRegime, double)? ClassificarHighVol(FeatureSnapshot snap)
    {
        var hist = _volatilityHistory.GetAll();
        if (hist.Length < 10) return null;

        double p90 = Percentil(hist, 90.0);
        if (p90 <= 0 || snap.Volatility30s <= p90) return null;

        var histSnaps = _history.GetAll();
        double mediaRate = histSnaps.Length > 0
            ? histSnaps.Average(s => s.TradeRate)
            : 0;
        if (snap.TradeRate <= mediaRate * 1.2) return null;

        double confianca = Math.Min(100,
            50 + (snap.Volatility30s / p90 - 1.0) * 30);
        return (MarketRegime.HighVol, confianca);
    }

    /// <summary>Volatility30s < p10 histórico e TradeRate abaixo da média.</summary>
    private (MarketRegime, double)? ClassificarLowVol(FeatureSnapshot snap)
    {
        var hist = _volatilityHistory.GetAll();
        if (hist.Length < 10) return null;

        double p10 = Percentil(hist, 10.0);
        if (snap.Volatility30s > p10 && p10 > 0) return null;

        var histSnaps = _history.GetAll();
        double mediaRate = histSnaps.Length > 0
            ? histSnaps.Average(s => s.TradeRate)
            : 1;
        if (snap.TradeRate >= mediaRate) return null;

        double ratio     = mediaRate > 0 ? 1.0 - snap.TradeRate / mediaRate : 0;
        double confianca = Math.Min(100, 50 + ratio * 30);
        return (MarketRegime.LowVol, confianca);
    }

    /// <summary>Price próximo do high ou low da sessão com VolumeRate > p80.</summary>
    private (MarketRegime, double)? ClassificarBreakout(FeatureSnapshot snap)
    {
        var hist = _history.GetAll();
        if (hist.Length < 10) return null;

        // DistanceHigh = sessionHigh - price; DistanceLow = price - sessionLow
        bool nearHigh = snap.DistanceHigh >= 0 && snap.DistanceHigh < 5.0;
        bool nearLow  = snap.DistanceLow  >= 0 && snap.DistanceLow  < 5.0;
        if (!nearHigh && !nearLow) return null;

        var   volHist = hist.Select(s => s.VolumeRate).ToArray();
        double p80    = Percentil(volHist, 80.0);
        if (snap.VolumeRate <= p80) return null;

        double dist      = nearHigh ? snap.DistanceHigh : snap.DistanceLow;
        double confianca = Math.Min(100,
            50 + (snap.VolumeRate / Math.Max(p80, 1) - 1.0) * 20 +
            (5.0 - dist) * 2.0);
        return (MarketRegime.Breakout, confianca);
    }

    // ── Utilitários ───────────────────────────────────────────────────────

    /// <summary>Elege o regime com maior confiança entre os candidatos.</summary>
    private static MarketRegime ElegerRegime(List<(MarketRegime, double)> candidatos)
    {
        if (candidatos.Count == 0) return MarketRegime.Unknown;
        return candidatos.OrderByDescending(c => c.Item2).First().Item1;
    }

    /// <summary>Retorna true se o regime atual tem menos de 30 s (transição recente).</summary>
    private bool EmTransicao()
        => _current.Regime != MarketRegime.Unknown
        && (DateTime.Now - _current.Since).TotalSeconds < 30;

    private static void Adicionar(List<(MarketRegime, double)> lista,
                                   (MarketRegime, double)? item)
    {
        if (item.HasValue) lista.Add(item.Value);
    }

    private static double Percentil(double[] data, double percentil)
    {
        if (data.Length == 0) return 0;
        var sorted = data.OrderBy(x => x).ToArray();
        int idx = (int)Math.Ceiling(percentil / 100.0 * sorted.Length) - 1;
        idx = Math.Max(0, Math.Min(idx, sorted.Length - 1));
        return sorted[idx];
    }
}
