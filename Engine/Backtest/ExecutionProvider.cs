using System;
using System.Threading.Tasks;

namespace MarketCore.Engine.Backtest;

// ── Fase 10 — Execution Provider ─────────────────────────────────────────

/// <summary>
/// Abstrai o provedor de execução de ordens.
/// Implementado por BacktestExecutionProvider (simulado) e pelo provedor live.
/// A estratégia nunca sabe se está em backtest ou live.
/// </summary>
public interface IExecutionProvider
{
    Task<string> EnviarCompraAsync(
        int quantity, OrderType type, double limitPrice = 0);

    Task<string> EnviarVendaAsync(
        int quantity, OrderType type, double limitPrice = 0);

    Task<bool> CancelarOrdemAsync(string orderId);

    event Action<OrderFill>? OnOrderFilled;
    event Action<string>?    OnOrderRejected;

    bool IsLive { get; }
}

/// <summary>Tipo de ordem suportado pelo sistema.</summary>
public enum OrderType
{
    Market = 0,
    Limit  = 1,
    Stop   = 2
}

/// <summary>Resultado de uma ordem executada (preenchida pelo book ou simulador).</summary>
public class OrderFill
{
    public string   OrderId         { get; set; } = string.Empty;
    public double   ExecutionPrice  { get; set; }
    public int      FilledQty       { get; set; }
    public DateTime FilledAt        { get; set; }

    /// <summary>Diferença entre o preço executado e o preço teórico (bid ou ask).</summary>
    public double   Slippage        { get; set; }

    /// <summary>Latência simulada em ms entre envio e confirmação da ordem.</summary>
    public long     LatencyMs       { get; set; }
}
