using System;
using System.Collections.Generic;
using System.Linq;
using MarketCore.Engine.Detectors;
using MarketCore.Engine.Features;
using MarketCore.Engine.Patterns;

namespace MarketCore.Engine.Agents;

// ── Fase 11 — AgentEngine ─────────────────────────────────────────────────

/// <summary>
/// Orquestra os 6 agentes especializados e consolida seus sinais.
/// Cada agente é avaliado de forma independente a cada chamada de Avaliar().
/// O Decision Core (Fase 12) combina os scores ponderados para a decisão final.
/// </summary>
public class AgentEngine
{
    private readonly List<IAgent> _agents;

    /// <summary>
    /// Disparado após cada avaliação completa, com a lista de todos os sinais.
    /// </summary>
    public event Action<List<AgentSignal>>? OnSignals;

    /// <summary>Construtor lite — sem PatternAgent. Usado quando StorageManager não está disponível.</summary>
    public AgentEngine()  // [FASE 16]
    {
        _agents = new List<IAgent>
        {
            new FlowAgent(),
            new BookAgent(),
            new AbsorptionAgent(),
            new OFIAgent(),
            new PatternAgent(),    // [FASE 16] lite — sem registry, retorna Neutral
            new RegimeAgent()
        };
    }

    public AgentEngine(PatternRegistry patternRegistry)
    {
        _agents = new List<IAgent>
        {
            new FlowAgent(),
            new BookAgent(),
            new AbsorptionAgent(),
            new OFIAgent(),
            new PatternAgent(patternRegistry),
            new RegimeAgent()
        };
    }

    /// <summary>
    /// Avalia todos os agentes e retorna a lista de sinais.
    /// Dispara OnSignals após a avaliação.
    /// </summary>
    public List<AgentSignal> Avaliar(FeatureSnapshot snap, RegimeState regime)
    {
        var signals = _agents
            .Select(a => a.Evaluate(snap, regime))
            .ToList();

        OnSignals?.Invoke(signals);
        return signals;
    }

    /// <summary>Retorna um agente pelo seu AgentId (ex: "FLOW", "BOOK").</summary>
    public IAgent? GetAgent(string agentId)
        => _agents.FirstOrDefault(a => a.AgentId == agentId);

    /// <summary>Lista de todos os agentes registrados.</summary>
    public IReadOnlyList<IAgent> Agents => _agents.AsReadOnly();
}
