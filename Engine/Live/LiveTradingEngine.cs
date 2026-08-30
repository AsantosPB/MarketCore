using MarketCore.Engine.Backtest;
using MarketCore.Engine.Decision;
using MarketCore.Engine.Features;
using MarketCore.Engine.Paper;     // reutiliza PaperPosition e PaperTradingSession
using MarketCore.Engine.Risk;
using MarketCore.Engine.Storage;

namespace MarketCore.Engine.Live;

// ── Fase 15 — Live Trading Engine ────────────────────────────────────────────
/// <summary>
/// Motor de live trading.
/// Conecta ao feed real do ProfitDLL via FeatureEngine e usa
/// <see cref="LiveExecutionProvider"/> para execução real (ou stub documentado).
/// Reutiliza <see cref="PaperPosition"/> e <see cref="PaperTradingSession"/>
/// para rastreamento de posição e persistência.
/// Stop: -340 pts | Target: +500 pts.
/// </summary>
public class LiveTradingEngine : IDisposable
{
    // ── Constantes ─────────────────────────────────────────────────────────
    private const double StopPts   = -340.0;
    private const double TargetPts =  500.0;

    // ── Dependências ───────────────────────────────────────────────────────
    private readonly LiveExecutionProvider _execution   = new();
    private readonly PaperPosition         _position    = new();
    private readonly StorageManager?       _storage;
    private readonly RiskManager           _riskManager;

    // ── Estado de sessão ───────────────────────────────────────────────────
    private PaperTradingSession? _session;
    private bool                 _ativo;

    // Rastreamento de latência separado (TradeRecord não tem LatencyMs)
    private long _totalLatencyMs;
    private int  _latencyCount;

    // ── Eventos ────────────────────────────────────────────────────────────
    public event Action<TradeRecord>?         OnTrade;
    public event Action<double>?              OnPnLUpdate;
    public event Action<string>?              OnError;
    public event Action<PaperTradingSession>? OnSessionEnd;

    // ── Propriedades ───────────────────────────────────────────────────────
    /// <summary>Sessão live ativa (null se não iniciada).</summary>
    public PaperTradingSession? SessaoAtual => _session;

    public LiveTradingEngine(StorageManager? storage, RiskManager riskManager)
    {
        _storage     = storage;
        _riskManager = riskManager;
    }

    // ── Sessão ─────────────────────────────────────────────────────────────
    /// <summary>Inicia uma nova sessão de live trading.</summary>
    public void IniciarSessao()
    {
        _session = new PaperTradingSession
        {
            SessionId = Guid.NewGuid(),
            Date      = DateTime.Today,
            StartTime = DateTime.Now
        };
        _totalLatencyMs = 0;
        _latencyCount   = 0;
        _ativo          = true;
        Log($"[LIVE] Sessão iniciada: {_session.SessionId}");
    }

    /// <summary>Encerra a sessão, persiste resultados e dispara OnSessionEnd.</summary>
    public async Task EncerrarSessaoAsync()
    {
        if (!_ativo || _session == null) return;
        _ativo = false;

        // Fechar posição aberta se houver
        if (!_position.IsFlat)
        {
            var trade = _position.Fechar(
                _execution.CurrentBid,
                DateTime.Now,
                "FimDoPregao");
            _session.Trades.Add(trade);
        }

        await PararAsync();

        _session.EndTime = DateTime.Now;
        CalcularEstatisticas();
        await SalvarSessaoAsync(_session);
        OnSessionEnd?.Invoke(_session);

        Log($"[LIVE] Sessão encerrada. " +
            $"Trades: {_session.TotalTrades} | " +
            $"NetPnL: R${_session.NetPnL:F2} | " +
            $"WinRate: {_session.WinRate:P1}");
    }

    // ── Pipeline principal ─────────────────────────────────────────────────
    /// <summary>
    /// Processa um estado de decisão aprovado pelo RiskManager.
    /// Chamado pelo MarketEngine após verificação de risco bem-sucedida.
    /// </summary>
    public async Task ProcessarDecisaoAsync(
        DecisionState   estado,
        FeatureSnapshot snapshot)
    {
        if (!_ativo || _session == null) return;

        // Atualizar preços e verificar ordens limit pendentes
        _execution.CurrentBid = snapshot.Bid;
        _execution.CurrentAsk = snapshot.Ask;
        _execution.VerificarOrdensPendentes(snapshot.Bid, snapshot.Ask);

        // Atualizar MFE/MAE e checar stop/target
        if (!_position.IsFlat)
        {
            _position.AtualizarMfeMae(snapshot.Price);
            var pnl = _position.UnrealizedPnL(snapshot.Price);
            OnPnLUpdate?.Invoke(pnl);

            var diff   = snapshot.Price - _position.EntryPrice;
            var pontos = _position.Side == PaperPosition.PositionSide.Long ? diff : -diff;

            if (pontos <= StopPts)
            {
                await FecharPosicaoAsync(snapshot, "Stop");
                return;
            }
            if (pontos >= TargetPts)
            {
                await FecharPosicaoAsync(snapshot, "Target");
                return;
            }
        }

        // Processar sinal de entrada
        if (_position.IsFlat)
        {
            if (estado == DecisionState.Buy || estado == DecisionState.StrongBuy)
            {
                OrderFill? fill = null;
                Action<OrderFill> onFill = f => fill = f;
                _execution.OnOrderFilled += onFill;
                await _execution.EnviarCompraAsync(1, OrderType.Market);
                _execution.OnOrderFilled -= onFill;

                if (fill != null)
                {
                    _position.AbrirLong(fill.ExecutionPrice, 1, fill.FilledAt, estado.ToString());
                    _totalLatencyMs += fill.LatencyMs;
                    _latencyCount++;
                    Log($"[LIVE] LONG aberto @ {fill.ExecutionPrice:F3} | latency={fill.LatencyMs}ms");
                }
                else
                {
                    OnError?.Invoke("LIVE: fill de compra nulo — posição não aberta");
                }
            }
            else if (estado == DecisionState.Sell || estado == DecisionState.StrongSell)
            {
                OrderFill? fill = null;
                Action<OrderFill> onFill = f => fill = f;
                _execution.OnOrderFilled += onFill;
                await _execution.EnviarVendaAsync(1, OrderType.Market);
                _execution.OnOrderFilled -= onFill;

                if (fill != null)
                {
                    _position.AbrirShort(fill.ExecutionPrice, 1, fill.FilledAt, estado.ToString());
                    _totalLatencyMs += fill.LatencyMs;
                    _latencyCount++;
                    Log($"[LIVE] SHORT aberto @ {fill.ExecutionPrice:F3} | latency={fill.LatencyMs}ms");
                }
                else
                {
                    OnError?.Invoke("LIVE: fill de venda nulo — posição não aberta");
                }
            }
        }
        else if (estado == DecisionState.Exit)
        {
            await FecharPosicaoAsync(snapshot, "Sinal Exit");
        }
    }

    // ── Fechamento de posição ──────────────────────────────────────────────
    private async Task FecharPosicaoAsync(FeatureSnapshot snapshot, string reason)
    {
        if (_session == null || _position.IsFlat) return;

        OrderFill? fill = null;
        Action<OrderFill> onFill = f => fill = f;
        _execution.OnOrderFilled += onFill;

        if (_position.Side == PaperPosition.PositionSide.Long)
            await _execution.EnviarVendaAsync(1, OrderType.Market);
        else
            await _execution.EnviarCompraAsync(1, OrderType.Market);

        _execution.OnOrderFilled -= onFill;

        if (fill != null)
        {
            var trade = _position.Fechar(fill.ExecutionPrice, fill.FilledAt, reason);
            trade.Slippage = fill.Slippage;
            trade.NetPnl   = trade.GrossPnl - fill.Slippage;
            _totalLatencyMs += fill.LatencyMs;
            _latencyCount++;

            _riskManager.RegistrarResultadoTrade(trade.NetPnl, fill.FilledAt);
            _session.Trades.Add(trade);
            OnTrade?.Invoke(trade);

            Log($"[LIVE] Posição fechada @ {fill.ExecutionPrice:F3} | {reason} | PnL=R${trade.NetPnl:F2}");
        }
        else
        {
            OnError?.Invoke($"LIVE: fill de fechamento nulo — razão: {reason}");
        }
    }

    // ── Parar — cancela todas as ordens abertas ────────────────────────────
    /// <summary>Cancela todas as ordens pendentes. Chamar no Kill Switch e shutdown.</summary>
    public async Task PararAsync()
    {
        await _execution.CancelarTodasAsync();
    }

    // ── Estatísticas ───────────────────────────────────────────────────────
    private void CalcularEstatisticas()
    {
        if (_session == null) return;
        var trades = _session.Trades;
        if (!trades.Any()) return;

        var wins   = trades.Where(t => t.NetPnl > 0).ToList();
        var losses = trades.Where(t => t.NetPnl <= 0).ToList();

        _session.TotalTrades   = trades.Count;
        _session.WinTrades     = wins.Count;
        _session.LossTrades    = losses.Count;
        _session.WinRate       = (double)wins.Count / trades.Count;
        _session.GrossPnL      = trades.Sum(t => t.GrossPnl);
        _session.TotalSlippage = trades.Sum(t => t.Slippage);
        _session.NetPnL        = trades.Sum(t => t.NetPnl);
        _session.Expectancy    = _session.NetPnL / trades.Count;
        _session.AvgSlippage   = trades.Average(t => t.Slippage);
        _session.AvgLatencyMs  = _latencyCount > 0
            ? (double)_totalLatencyMs / _latencyCount : 0;

        if (wins.Any() && losses.Any())
            _session.ProfitFactor =
                wins.Sum(t => t.GrossPnl) /
                Math.Abs(losses.Sum(t => t.GrossPnl));

        double peak = 0, pnlAcc = 0, maxDD = 0;
        foreach (var t in trades)
        {
            pnlAcc += t.NetPnl;
            if (pnlAcc > peak) peak = pnlAcc;
            var dd = peak - pnlAcc;
            if (dd > maxDD) maxDD = dd;
        }
        _session.MaxDrawdown = maxDD;
    }

    // ── Persistência ───────────────────────────────────────────────────────
    private async Task SalvarSessaoAsync(PaperTradingSession session)
    {
        if (_storage == null) return;
        foreach (var trade in session.Trades)
            await _storage.GravarTradeOperacionalAsync(trade);
        await _storage.SalvarPaperSessionAsync(session);
    }

    // ── Auxiliar ───────────────────────────────────────────────────────────
    private void Log(string msg)
        => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");

    // ── IDisposable ────────────────────────────────────────────────────────
    private bool _disposed;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ = PararAsync();
    }
}
