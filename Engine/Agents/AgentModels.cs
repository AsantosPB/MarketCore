using System;
using MarketCore.Engine.Detectors;
using MarketCore.Engine.Features;

namespace MarketCore.Engine.Agents;

// ── Fase 11 — Agent Models ────────────────────────────────────────────────

/// <summary>Direção do sinal do agente.</summary>
public enum Direction
{
    Buy     =  1,
    Neutral =  0,
    Sell    = -1
}

/// <summary>
/// Sinal gerado por um agente para um instante de mercado.
/// Score de -100 (venda máxima) a +100 (compra máxima).
/// </summary>
public class AgentSignal
{
    /// <summary>Identificador curto do agente (ex: "FLOW", "BOOK").</summary>
    public string    AgentId     { get; set; } = string.Empty;

    /// <summary>Direção agregada calculada a partir do Score.</summary>
    public Direction Direction   { get; set; }

    /// <summary>Score de convicção: -100 (bear máximo) a +100 (bull máximo).</summary>
    public int       Score       { get; set; }

    /// <summary>Confiança do agente no sinal: 0 a 100.</summary>
    public int       Confidence  { get; set; }

    /// <summary>Até quando o sinal é considerado válido (UTC).</summary>
    public DateTime  ValidUntil  { get; set; }

    /// <summary>Códigos de razão legíveis para diagnóstico e logging.</summary>
    public string[]  ReasonCodes { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Interface comum a todos os agentes especializados.
/// Cada agente recebe o snapshot completo e o regime e retorna um AgentSignal.
/// Os agentes são independentes — não se comunicam entre si.
/// </summary>
public interface IAgent
{
    /// <summary>Identificador curto e único do agente.</summary>
    string AgentId   { get; }

    /// <summary>Nome descritivo do agente.</summary>
    string AgentName { get; }

    /// <summary>
    /// Avalia o snapshot de mercado e retorna o sinal do agente.
    /// Deve ser thread-safe e retornar em menos de 1 ms.
    /// </summary>
    AgentSignal Evaluate(FeatureSnapshot snapshot, RegimeState regime);
}
