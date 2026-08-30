using System.Text.Json;
using MarketCore.Engine.Agents;
using MarketCore.Engine.Detectors;
using MarketCore.Engine.Features;
using MarketCore.Engine.Storage;

namespace MarketCore.Engine.Decision;

/// <summary>
/// Fase 12 — Decision Core.
/// Combina sinais dos 6 agentes com pesos por regime, aplica modo confirmado (600ms),
/// persiste TODAS as decisões no SQLite e dispara OnDecision.
/// </summary>
public class DecisionCore
{
    private readonly StorageManager _storage;

    // Modo confirmado: guardar estado pendente até 600ms de consistência
    private DecisionState _estadoPendente    = DecisionState.Wait;
    private DateTime      _confirmacaoInicio = DateTime.MinValue;
    private const int     ConfirmacaoMs      = 600;

    public DecisionMode  Modo        { get; set; } = DecisionMode.Confirmed;
    public DecisionState UltimoEstado { get; private set; } = DecisionState.Wait;

    public event Action<DecisionState>? OnDecision;

    public DecisionCore(StorageManager storage)
    {
        _storage = storage;
    }

    public async Task AvaliarAsync(FeatureSnapshot snap, RegimeState regime, List<AgentSignal> signals)
    {
        var pesos       = WeightSet.ForRegime(regime.Regime);
        var scoreTotal  = CalcularScoreTotal(signals, pesos);
        var estado      = ClassificarEstado(scoreTotal);
        var estadoFinal = Modo == DecisionMode.Confirmed
                          ? AplicarConfirmacao(estado)
                          : estado;

        UltimoEstado = estadoFinal;
        OnDecision?.Invoke(estadoFinal);

        // Persiste TODAS as decisões (inclusive Wait)
        var record = new DecisionRecord
        {
            Timestamp     = snap.Timestamp,
            FinalScore    = scoreTotal,
            Direction     = DirecaoDoEstado(estadoFinal),
            DecisionState = estadoFinal.ToString(),
            AgentScores   = SerializarScores(signals),
            Regime        = regime.Regime.ToString(),
            TimeWindow    = snap.TimeWindow ?? string.Empty,
            RiskApproved  = estadoFinal != DecisionState.Wait,
            EntryTaken    = false,
            BlockReason   = string.Empty
        };
        await _storage.GravarDecisionAsync(record);
    }

    // ── privados ────────────────────────────────────────────────────────────

    private static double CalcularScoreTotal(List<AgentSignal> signals, WeightSet pesos)
    {
        double soma  = 0;
        double total = 0;

        foreach (var sig in signals)
        {
            var peso = sig.AgentId switch
            {
                "FLOW"       => pesos.Flow,
                "BOOK"       => pesos.Book,
                "ABSORPTION" => pesos.Absorption,
                "OFI"        => pesos.Ofi,
                "PATTERN"    => pesos.Pattern,
                "REGIME"     => pesos.Regime,
                _            => 1.0
            };
            soma  += sig.Score * peso;
            total += 100.0 * peso;
        }

        return total > 0 ? (soma / total) * 100.0 : 0;
    }

    private static DecisionState ClassificarEstado(double score) => score switch
    {
        >= 70  => DecisionState.StrongBuy,
        >= 40  => DecisionState.Buy,
        >= 15  => DecisionState.PrepareBuy,
        <= -70 => DecisionState.StrongSell,
        <= -40 => DecisionState.Sell,
        <= -15 => DecisionState.PrepareSell,
        _      => DecisionState.Wait
    };

    private DecisionState AplicarConfirmacao(DecisionState novoEstado)
    {
        var agora = DateTime.UtcNow;

        if (novoEstado == _estadoPendente)
        {
            // Mesmo estado: verificar se já passou 600ms
            if ((agora - _confirmacaoInicio).TotalMilliseconds >= ConfirmacaoMs)
                return novoEstado;   // confirmado
            return UltimoEstado;     // ainda aguardando
        }
        else
        {
            // Estado mudou: reiniciar cronômetro
            _estadoPendente    = novoEstado;
            _confirmacaoInicio = agora;
            return UltimoEstado;     // mantém estado anterior enquanto confirma
        }
    }

    private static string DirecaoDoEstado(DecisionState estado) => estado switch
    {
        DecisionState.Buy      or
        DecisionState.StrongBuy  or
        DecisionState.PrepareBuy  => "Buy",
        DecisionState.Sell     or
        DecisionState.StrongSell or
        DecisionState.PrepareSell => "Sell",
        DecisionState.Exit        => "Exit",
        _                         => "Neutral"
    };

    private static string SerializarScores(List<AgentSignal> signals)
    {
        var dict = signals.ToDictionary(
            s => s.AgentId,
            s => new { s.Score, Direction = s.Direction.ToString(), s.Confidence }
        );
        return JsonSerializer.Serialize(dict);
    }
}
