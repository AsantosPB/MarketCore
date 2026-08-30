using System.Collections.Concurrent;
using MarketCore.Engine.Backtest;

namespace MarketCore.Engine.Live;

// ── Fase 15 — Live Execution Provider ────────────────────────────────────────
//
// NOTA DE INTEGRAÇÃO FUTURA:
//   ProfitDLLProvider (Providers/Nelogica/ProfitDLLProvider.cs) implementa apenas
//   IMarketDataProvider — não possui métodos de envio de ordens.
//   ProfitDLL.cs (P/Invoke) também não mapeia funções de roteamento.
//
//   Quando a API de roteamento Nelogica for mapeada em ProfitDLL.cs, substituir
//   os stubs de fill imediato abaixo pelas chamadas reais:
//     DLLInitializeLogin → registrar orderChangeCallback
//     DLL.SendBuyOrder(account, broker, asset, qty, price, type, validity)
//     DLL.SendSellOrder(account, broker, asset, qty, price, type, validity)
//     DLL.CancelOrder(account, broker, orderId)
//   e acionar OnOrderFilled/OnOrderRejected via os callbacks registrados.

/// <summary>
/// Ordem live pendente aguardando confirmação de execução pelo broker.
/// </summary>
public class LiveOrder
{
    public string   OrderId    { get; set; } = string.Empty;
    public string   Side       { get; set; } = string.Empty;  // "BUY" | "SELL"
    public int      Quantity   { get; set; }
    public double   LimitPrice { get; set; }
    public DateTime SentAt     { get; set; }
}

/// <summary>
/// Implementação live de <see cref="IExecutionProvider"/> (<c>IsLive = true</c>).
///
/// ESTADO ATUAL — stub de fase de desenvolvimento:
/// Market orders disparam fill imediato ao preço corrente (bid/ask).
/// Limit orders ficam pendentes em <c>_pending</c> e são executadas por
/// <see cref="VerificarOrdensPendentes"/> a cada tick de preço.
/// Isso permite testar o pipeline end-to-end enquanto a integração DLL de
/// roteamento de ordens ainda não está mapeada em ProfitDLL.cs.
/// </summary>
public class LiveExecutionProvider : IExecutionProvider
{
    // ── Interface ──────────────────────────────────────────────────────────
    public bool IsLive => true;

    // ── Preços de mercado (atualizados pelo LiveTradingEngine a cada tick) ─
    public double CurrentBid { get; set; }
    public double CurrentAsk { get; set; }

    // ── Eventos ────────────────────────────────────────────────────────────
    public event Action<OrderFill>? OnOrderFilled;
    public event Action<string>?    OnOrderRejected;

    // ── Ordens abertas ─────────────────────────────────────────────────────
    private readonly ConcurrentDictionary<string, LiveOrder> _pending = new();

    // ── Envio de compra ────────────────────────────────────────────────────
    /// <summary>
    /// Market: fill imediato ao Ask corrente.<br/>
    /// Limit: enfileira em <see cref="VerificarOrdensPendentes"/>.
    /// </summary>
    public Task<string> EnviarCompraAsync(
        int quantity, OrderType type, double limitPrice = 0)
    {
        var orderId = Guid.NewGuid().ToString();
        var sentAt  = DateTime.UtcNow;

        if (type == OrderType.Limit)
        {
            _pending[orderId] = new LiveOrder
            {
                OrderId    = orderId,
                Side       = "BUY",
                Quantity   = quantity,
                LimitPrice = limitPrice,
                SentAt     = sentAt
            };
            Console.WriteLine(
                $"[{DateTime.Now:HH:mm:ss}] [LIVE] BUY LIMIT qty={quantity} lim={limitPrice:F3}");
        }
        else
        {
            // TODO: substituir por DLL.SendBuyOrder(...) quando API de roteamento for mapeada
            var execPrice = CurrentAsk;
            var latency   = (long)(DateTime.UtcNow - sentAt).TotalMilliseconds;
            Console.WriteLine(
                $"[{DateTime.Now:HH:mm:ss}] [LIVE] BUY MARKET qty={quantity} @ {execPrice:F3}");
            OnOrderFilled?.Invoke(new OrderFill
            {
                OrderId        = orderId,
                ExecutionPrice = execPrice,
                FilledQty      = quantity,
                FilledAt       = DateTime.UtcNow,
                Slippage       = 0,
                LatencyMs      = latency
            });
        }

        return Task.FromResult(orderId);
    }

    // ── Envio de venda ─────────────────────────────────────────────────────
    /// <summary>
    /// Market: fill imediato ao Bid corrente.<br/>
    /// Limit: enfileira em <see cref="VerificarOrdensPendentes"/>.
    /// </summary>
    public Task<string> EnviarVendaAsync(
        int quantity, OrderType type, double limitPrice = 0)
    {
        var orderId = Guid.NewGuid().ToString();
        var sentAt  = DateTime.UtcNow;

        if (type == OrderType.Limit)
        {
            _pending[orderId] = new LiveOrder
            {
                OrderId    = orderId,
                Side       = "SELL",
                Quantity   = quantity,
                LimitPrice = limitPrice,
                SentAt     = sentAt
            };
            Console.WriteLine(
                $"[{DateTime.Now:HH:mm:ss}] [LIVE] SELL LIMIT qty={quantity} lim={limitPrice:F3}");
        }
        else
        {
            // TODO: substituir por DLL.SendSellOrder(...) quando API de roteamento for mapeada
            var execPrice = CurrentBid;
            var latency   = (long)(DateTime.UtcNow - sentAt).TotalMilliseconds;
            Console.WriteLine(
                $"[{DateTime.Now:HH:mm:ss}] [LIVE] SELL MARKET qty={quantity} @ {execPrice:F3}");
            OnOrderFilled?.Invoke(new OrderFill
            {
                OrderId        = orderId,
                ExecutionPrice = execPrice,
                FilledQty      = quantity,
                FilledAt       = DateTime.UtcNow,
                Slippage       = 0,
                LatencyMs      = latency
            });
        }

        return Task.FromResult(orderId);
    }

    // ── Cancelamento ───────────────────────────────────────────────────────
    public Task<bool> CancelarOrdemAsync(string orderId)
    {
        var removed = _pending.TryRemove(orderId, out _);
        // TODO: DLL.CancelOrder(account, broker, orderId) quando API for mapeada
        if (removed)
            Console.WriteLine(
                $"[{DateTime.Now:HH:mm:ss}] [LIVE] CANCEL id={orderId[..8]}");
        return Task.FromResult(removed);
    }

    /// <summary>Cancela todas as ordens pendentes (Kill Switch e shutdown).</summary>
    public async Task CancelarTodasAsync()
    {
        foreach (var id in _pending.Keys.ToList())
            await CancelarOrdemAsync(id);
    }

    /// <summary>
    /// Verifica ordens limit pendentes e executa as que atingiram o preço.
    /// Chamar a cada tick de bid/ask.
    /// </summary>
    public void VerificarOrdensPendentes(double bid, double ask)
    {
        CurrentBid = bid;
        CurrentAsk = ask;

        var filled = new List<string>();
        foreach (var (id, order) in _pending)
        {
            bool   executada = false;
            double execPrice = 0;

            if (order.Side == "BUY"  && ask <= order.LimitPrice)
            {
                execPrice = order.LimitPrice;
                executada = true;
            }
            else if (order.Side == "SELL" && bid >= order.LimitPrice)
            {
                execPrice = order.LimitPrice;
                executada = true;
            }

            if (executada)
            {
                var latency = (long)(DateTime.UtcNow - order.SentAt).TotalMilliseconds;
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] [LIVE] FILL {order.Side} qty={order.Quantity} @ {execPrice:F3}");
                OnOrderFilled?.Invoke(new OrderFill
                {
                    OrderId        = id,
                    ExecutionPrice = execPrice,
                    FilledQty      = order.Quantity,
                    FilledAt       = DateTime.UtcNow,
                    Slippage       = 0,
                    LatencyMs      = latency
                });
                filled.Add(id);
            }
        }

        foreach (var id in filled)
            _pending.TryRemove(id, out _);
    }
}
