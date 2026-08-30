using MarketCore.Engine.Detectors;

namespace MarketCore.Engine.Decision;

/// <summary>
/// Estado de decisão do Decision Core — Fase 12.
/// </summary>
public enum DecisionState
{
    Wait        = 0,
    PrepareBuy  = 1,
    Buy         = 2,
    StrongBuy   = 3,
    PrepareSell = 4,
    Sell        = 5,
    StrongSell  = 6,
    Exit        = 7
}

/// <summary>
/// Modo de confirmação: Instant dispara imediatamente; Confirmed exige 600ms de consistência.
/// </summary>
public enum DecisionMode
{
    Instant   = 0,
    Confirmed = 1
}

/// <summary>
/// Pesos por agente, calibrados por regime de mercado.
/// </summary>
public class WeightSet
{
    public double Flow       { get; init; } = 1.0;
    public double Book       { get; init; } = 1.0;
    public double Absorption { get; init; } = 1.0;
    public double Ofi        { get; init; } = 1.0;
    public double Pattern    { get; init; } = 1.0;
    public double Regime     { get; init; } = 1.0;

    public static WeightSet ForRegime(MarketRegime regime) => regime switch
    {
        MarketRegime.TrendUp    => new WeightSet { Flow=1.4, Book=0.8, Absorption=0.9, Ofi=1.3, Pattern=1.2, Regime=1.1 },
        MarketRegime.TrendDown  => new WeightSet { Flow=1.4, Book=0.8, Absorption=0.9, Ofi=1.3, Pattern=1.2, Regime=1.1 },
        MarketRegime.Range      => new WeightSet { Flow=0.7, Book=1.3, Absorption=1.4, Ofi=0.8, Pattern=1.0, Regime=0.8 },
        MarketRegime.HighVol    => new WeightSet { Flow=0.6, Book=0.9, Absorption=1.5, Ofi=0.7, Pattern=0.8, Regime=1.2 },
        MarketRegime.LowVol     => new WeightSet { Flow=1.0, Book=1.2, Absorption=1.0, Ofi=1.0, Pattern=1.3, Regime=0.9 },
        MarketRegime.Breakout   => new WeightSet { Flow=1.3, Book=0.7, Absorption=0.8, Ofi=1.4, Pattern=1.1, Regime=1.3 },
        MarketRegime.Transition => new WeightSet { Flow=0.8, Book=1.0, Absorption=1.1, Ofi=0.9, Pattern=0.7, Regime=1.4 },
        _                       => new WeightSet()  // Unknown → pesos neutros 1.0
    };
}
