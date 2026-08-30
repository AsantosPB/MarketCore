using System;
using System.Threading.Tasks;

namespace MarketCore.Engine.Backtest;

// ── Fase 10 — BacktestExecutionProvider ──────────────────────────────────

/// <summary>
/// Implementação simulada de IExecutionProvider para backtest.
/// Aplica latência aleatória (MinLatencyMs..MaxLatencyMs) e slippage
/// baseado no spread corrente (CurrentSpread * SlippageFactor).
/// Compra executa no Ask + slippage; Venda executa no Bid - slippage.
/// </summary>
public class BacktestExecutionProvider : IExecutionProvider
{
    // ── Interface ─────────────────────────────────────────────────────────
    public bool IsLive => false;

    // ── Configuração de simulação ─────────────────────────────────────────
    /// <summary>Latência mínima simulada em ms.</summary>
    public int    MinLatencyMs   { get; set; } = 50;

    /// <summary>Latência máxima simulada em ms.</summary>
    public int    MaxLatencyMs   { get; set; } = 200;

    /// <summary>Fração do spread aplicada como slippage (0..1).</summary>
    public double SlippageFactor { get; set; } = 0.5;

    // ── Estado de mercado (atualizado pelo BacktestEngine a cada snapshot) ─
    public double CurrentBid    { get; set; }
    public double CurrentAsk    { get; set; }

    /// <summary>Spread corrente calculado como Ask - Bid.</summary>
    public double CurrentSpread => CurrentAsk - CurrentBid;

    // ── Eventos ───────────────────────────────────────────────────────────
    public event Action<OrderFill>? OnOrderFilled;
    public event Action<string>?    OnOrderRejected;

    // ── Compra ────────────────────────────────────────────────────────────
    /// <summary>
    /// Envia ordem de compra simulada.
    /// Market: executa no Ask + slippage.
    /// Limit:  executa em limitPrice se Ask &lt;= limitPrice; rejeita caso contrário.
    /// Stop:   executa no Ask corrente (sem slippage adicional).
    /// </summary>
    public async Task<string> EnviarCompraAsync(
        int quantity, OrderType type, double limitPrice = 0)
    {
        var orderId = Guid.NewGuid().ToString();

        // Simular latência de rede/roteamento
        var latency = Random.Shared.Next(MinLatencyMs, MaxLatencyMs);
        await Task.Delay(latency);

        double execPrice;

        if (type == OrderType.Market)
        {
            // Compra a mercado: pior preço do ask + slippage
            var slippage = CurrentSpread * SlippageFactor;
            execPrice = CurrentAsk + slippage;
        }
        else if (type == OrderType.Limit)
        {
            // Limit só executa se o ask estiver dentro do limite
            if (CurrentAsk > limitPrice)
            {
                OnOrderRejected?.Invoke(orderId);
                return orderId;
            }
            execPrice = limitPrice;
        }
        else // Stop
        {
            execPrice = CurrentAsk;
        }

        var fill = new OrderFill
        {
            OrderId        = orderId,
            ExecutionPrice = execPrice,
            FilledQty      = quantity,
            FilledAt       = DateTime.UtcNow,
            Slippage       = execPrice - CurrentAsk,
            LatencyMs      = latency
        };

        OnOrderFilled?.Invoke(fill);
        return orderId;
    }

    // ── Venda ─────────────────────────────────────────────────────────────
    /// <summary>
    /// Envia ordem de venda simulada.
    /// Market: executa no Bid - slippage (simétrico à compra).
    /// Limit:  executa em limitPrice se Bid &gt;= limitPrice; rejeita caso contrário.
    /// Stop:   executa no Bid corrente (sem slippage adicional).
    /// </summary>
    public async Task<string> EnviarVendaAsync(
        int quantity, OrderType type, double limitPrice = 0)
    {
        var orderId = Guid.NewGuid().ToString();

        var latency = Random.Shared.Next(MinLatencyMs, MaxLatencyMs);
        await Task.Delay(latency);

        double execPrice;

        if (type == OrderType.Market)
        {
            // Venda a mercado: pior preço do bid - slippage
            var slippage = CurrentSpread * SlippageFactor;
            execPrice = CurrentBid - slippage;
        }
        else if (type == OrderType.Limit)
        {
            if (CurrentBid < limitPrice)
            {
                OnOrderRejected?.Invoke(orderId);
                return orderId;
            }
            execPrice = limitPrice;
        }
        else // Stop
        {
            execPrice = CurrentBid;
        }

        var fill = new OrderFill
        {
            OrderId        = orderId,
            ExecutionPrice = execPrice,
            FilledQty      = quantity,
            FilledAt       = DateTime.UtcNow,
            Slippage       = CurrentBid - execPrice,   // positivo = perdeu em relação ao bid
            LatencyMs      = latency
        };

        OnOrderFilled?.Invoke(fill);
        return orderId;
    }

    // ── Cancelamento ──────────────────────────────────────────────────────
    /// <summary>
    /// Cancela uma ordem pendente.
    /// No simulador todas as ordens já foram executadas (Task.Delay), então retorna true.
    /// </summary>
    public Task<bool> CancelarOrdemAsync(string orderId)
        => Task.FromResult(true);
}
