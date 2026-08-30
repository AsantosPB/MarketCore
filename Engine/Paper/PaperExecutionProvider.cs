using MarketCore.Engine.Backtest;

namespace MarketCore.Engine.Paper;

/// <summary>
/// Fase 14 — Execution provider para paper trading.
/// Recebe dados reais do ProfitDLL mas simula a execução com latência e slippage realistas.
/// </summary>
public class SimulatedOrder
{
    public string    OrderId    { get; set; } = string.Empty;
    public string    Side       { get; set; } = string.Empty;  // "BUY" ou "SELL"
    public OrderType Type       { get; set; }
    public int       Quantity   { get; set; }
    public double    LimitPrice { get; set; }
    public DateTime  CreatedAt  { get; set; }
}

public class PaperExecutionProvider : IExecutionProvider
{
    public bool   IsLive         => false;
    public int    MinLatencyMs   { get; set; } = 80;
    public int    MaxLatencyMs   { get; set; } = 300;
    public double SlippageFactor { get; set; } = 0.5;

    public double CurrentBid    { get; set; }
    public double CurrentAsk    { get; set; }
    public double CurrentSpread => CurrentAsk - CurrentBid;

    public event Action<OrderFill>? OnOrderFilled;
    public event Action<string>?    OnOrderRejected;

    private readonly Dictionary<string, SimulatedOrder> _openOrders = new();

    public async Task<string> EnviarCompraAsync(
        int quantity, OrderType type, double limitPrice = 0)
    {
        var orderId = Guid.NewGuid().ToString();
        var latency = Random.Shared.Next(MinLatencyMs, MaxLatencyMs);
        await Task.Delay(latency);

        if (type == OrderType.Market)
        {
            var slippage  = CurrentSpread * SlippageFactor;
            var execPrice = CurrentAsk + slippage;
            OnOrderFilled?.Invoke(new OrderFill
            {
                OrderId        = orderId,
                ExecutionPrice = execPrice,
                FilledQty      = quantity,
                FilledAt       = DateTime.UtcNow,
                Slippage       = slippage,
                LatencyMs      = latency
            });
        }
        else if (type == OrderType.Limit)
        {
            _openOrders[orderId] = new SimulatedOrder
            {
                OrderId    = orderId,
                Side       = "BUY",
                Type       = type,
                Quantity   = quantity,
                LimitPrice = limitPrice,
                CreatedAt  = DateTime.UtcNow
            };
        }

        return orderId;
    }

    public async Task<string> EnviarVendaAsync(
        int quantity, OrderType type, double limitPrice = 0)
    {
        var orderId = Guid.NewGuid().ToString();
        var latency = Random.Shared.Next(MinLatencyMs, MaxLatencyMs);
        await Task.Delay(latency);

        if (type == OrderType.Market)
        {
            var slippage  = CurrentSpread * SlippageFactor;
            var execPrice = CurrentBid - slippage;
            OnOrderFilled?.Invoke(new OrderFill
            {
                OrderId        = orderId,
                ExecutionPrice = execPrice,
                FilledQty      = quantity,
                FilledAt       = DateTime.UtcNow,
                Slippage       = slippage,
                LatencyMs      = latency
            });
        }
        else if (type == OrderType.Limit)
        {
            _openOrders[orderId] = new SimulatedOrder
            {
                OrderId    = orderId,
                Side       = "SELL",
                Type       = type,
                Quantity   = quantity,
                LimitPrice = limitPrice,
                CreatedAt  = DateTime.UtcNow
            };
        }

        return orderId;
    }

    public Task<bool> CancelarOrdemAsync(string orderId)
    {
        var removed = _openOrders.Remove(orderId);
        return Task.FromResult(removed);
    }

    /// <summary>
    /// Verifica e executa ordens limit pendentes a cada atualização de preço.
    /// </summary>
    public void VerificarOrdensPendentes(double bid, double ask)
    {
        CurrentBid = bid;
        CurrentAsk = ask;

        var filled = new List<string>();
        foreach (var (id, order) in _openOrders)
        {
            bool   executada  = false;
            double execPrice  = 0;

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
                OnOrderFilled?.Invoke(new OrderFill
                {
                    OrderId        = id,
                    ExecutionPrice = execPrice,
                    FilledQty      = order.Quantity,
                    FilledAt       = DateTime.UtcNow,
                    Slippage       = 0,
                    LatencyMs      = 0
                });
                filled.Add(id);
            }
        }

        foreach (var id in filled)
            _openOrders.Remove(id);
    }
}
