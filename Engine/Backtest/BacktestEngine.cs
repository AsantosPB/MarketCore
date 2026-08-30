using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MarketCore.Engine.Dataset;
using MarketCore.Engine.Features;
using MarketCore.Engine.Patterns;
using MarketCore.Engine.Replay;
using MarketCore.Engine.Storage;

namespace MarketCore.Engine.Backtest;

// ── Fase 10 — BacktestEngine ──────────────────────────────────────────────

/// <summary>
/// Motor de backtest que combina ReplayEngine + ExecutionProvider + Position.
/// Usa o mesmo FeatureEngine e PatternRegistry do sistema live para garantir
/// que a estratégia se comporta identicamente em backtest e em produção.
/// Emite OnFinished ao concluir e OnLog para diagnóstico em tempo real.
/// </summary>
public class BacktestEngine
{
    // ── Dependências ──────────────────────────────────────────────────────
    private readonly ReplayEngine              _replay;
    private readonly BacktestExecutionProvider _execution;
    private readonly FeatureEngine             _features;
    private readonly PatternRegistry           _patterns;
    private readonly PatternEvaluator          _evaluator;

    // ── Estado da sessão ──────────────────────────────────────────────────
    private BacktestPosition _position = new();
    private BacktestConfig   _config   = new();

    // ── Rastreamento de latência (TradeRecord não possui o campo) ─────────
    private long _totalLatencyMs;
    private int  _latencyCount;

    // ── Serialização de sinais (evita entradas simultâneas) ───────────────
    private readonly SemaphoreSlim _signalLock = new(1, 1);

    // ── Eventos ───────────────────────────────────────────────────────────
    /// <summary>Disparado quando o backtest termina com o resultado consolidado.</summary>
    public event Action<BacktestResult>? OnFinished;

    /// <summary>Mensagens de log para diagnóstico em tempo real.</summary>
    public event Action<string>?         OnLog;

    // ── Construtor ────────────────────────────────────────────────────────
    public BacktestEngine(
        FeatureEngine   featureEngine,
        PatternRegistry patternRegistry,
        string          rawDataPath)
    {
        _features  = featureEngine;
        _patterns  = patternRegistry;
        _evaluator = new PatternEvaluator();
        _execution = new BacktestExecutionProvider();
        _replay    = new ReplayEngine(featureEngine, rawDataPath);
    }

    // ── Execução principal ────────────────────────────────────────────────
    /// <summary>
    /// Executa o backtest completo para a data informada na config.
    /// Retorna o BacktestResult com todas as métricas ao finalizar.
    /// </summary>
    public async Task<BacktestResult> ExecutarAsync(BacktestConfig config)
    {
        _config           = config;
        _position         = new BacktestPosition();
        _totalLatencyMs   = 0;
        _latencyCount     = 0;
        var trades        = new List<TradeRecord>();
        var started       = DateTime.UtcNow;

        // PASSO 1 — Configurar execution provider
        _execution.MinLatencyMs   = config.MinLatencyMs;
        _execution.MaxLatencyMs   = config.MaxLatencyMs;
        _execution.SlippageFactor = config.SlippageFactor;

        OnLog?.Invoke($"Iniciando backtest {config.Date:dd/MM/yyyy} | " +
                      $"MaxTrades={config.MaxTrades} | Speed={config.Speed}");

        // PASSO 2 — Conectar ao FeatureEngine (async void = fire-and-forget intencional)
        async void OnSnapshot(FeatureSnapshot snap)
        {
            // Atualizar bid/ask no execution provider
            _execution.CurrentBid = snap.Bid;
            _execution.CurrentAsk = snap.Ask;

            // Atualizar MFE/MAE da posição aberta
            if (!_position.IsFlat)
                _position.AtualizarMfeMae(snap.Price);

            // Verificar stop/target antes de procurar novo sinal
            await VerificarStopTargetAsync(snap, trades);

            // Avaliar padrões ativos e processar sinal
            if (_position.IsFlat)
            {
                var padrao = AvaliarPadroesAtivos(snap);
                if (padrao != null)
                    await ProcessarSinalAsync(snap, padrao, trades);
            }
        }

        _features.OnSnapshot += OnSnapshot;

        try
        {
            // PASSO 3 — Executar replay (alimenta FeatureEngine que dispara OnSnapshot)
            await _replay.IniciarAsync(config.Date, config.Speed);
        }
        finally
        {
            _features.OnSnapshot -= OnSnapshot;
        }

        // PASSO 4 — Fechar posição aberta ao fim do pregão
        if (!_position.IsFlat)
        {
            var exitPrice = _execution.CurrentBid > 0 ? _execution.CurrentBid : _position.EntryPrice;
            var trade     = _position.Fechar(exitPrice, DateTime.UtcNow, "FimDoPregao");
            trades.Add(trade);
            OnLog?.Invoke($"Posição fechada no fim do pregão @ {exitPrice:F2}");
        }

        // PASSO 5 — Calcular e emitir resultado
        var result = CalcularResultado(trades, config, DateTime.UtcNow - started);
        OnFinished?.Invoke(result);
        return result;
    }

    // ── Avaliação de padrões ──────────────────────────────────────────────
    /// <summary>
    /// Retorna o padrão com maior Expectancy que satisfaz as condições correntes.
    /// Agentes (Fase 11) substituirão esta lógica.
    /// </summary>
    private DiscoveredPattern? AvaliarPadroesAtivos(FeatureSnapshot snap)
    {
        var padroesAtivos = _patterns.PadroesAtivos();
        if (padroesAtivos.Count == 0) return null;

        foreach (var padrao in padroesAtivos
            .OrderByDescending(p => p.TrainingStats.Expectancy))
        {
            // Criar DatasetRecord com apenas Features (Labels=null é OK para Satisfaz)
            var record = new DatasetRecord { Features = snap.ToMarketSnapshot() };
            if (_evaluator.Satisfaz(record, padrao.Conditions))
                return padrao;
        }

        return null;
    }

    // ── Processamento de sinal ────────────────────────────────────────────
    /// <summary>
    /// Entra na posição quando um padrão válido é encontrado.
    /// Direção padrão: Long (agentes em Fase 11 definem direção por contexto).
    /// Respeita MaxTrades e MaxDailyLoss.
    /// </summary>
    private async Task ProcessarSinalAsync(
        FeatureSnapshot snap, DiscoveredPattern padrao, List<TradeRecord> trades)
    {
        await _signalLock.WaitAsync();
        try
        {
            // Revalidar estado dentro do lock
            if (!_position.IsFlat || trades.Count >= _config.MaxTrades) return;

            var currentPnl = trades.Sum(t => t.NetPnl);
            if (currentPnl < -_config.MaxDailyLoss)
            {
                OnLog?.Invoke($"MaxDailyLoss atingido ({currentPnl:F2}). Backtest interrompido.");
                return;
            }

            // Capturar fill via evento (EnviarCompraAsync dispara OnOrderFilled antes de retornar)
            OrderFill? entryFill = null;
            void OnFill(OrderFill f) => entryFill = f;
            _execution.OnOrderFilled += OnFill;
            try
            {
                await _execution.EnviarCompraAsync(1, OrderType.Market);
            }
            finally
            {
                _execution.OnOrderFilled -= OnFill;
            }

            if (entryFill == null) return;

            _position.AbrirLong(entryFill.ExecutionPrice, 1, DateTime.UtcNow);
            _totalLatencyMs += entryFill.LatencyMs;
            _latencyCount++;

            OnLog?.Invoke($"ENTRADA Long @ {entryFill.ExecutionPrice:F2} " +
                          $"slippage={entryFill.Slippage:F2} lat={entryFill.LatencyMs}ms " +
                          $"padrão=#{padrao.PatternId}");
        }
        finally
        {
            _signalLock.Release();
        }
    }

    // ── Stop / Target ─────────────────────────────────────────────────────
    // Níveis alinhados com o DatasetBuilder: target=+20 pts, stop=-15 pts
    private const double TargetPts = 20.0;
    private const double StopPts   = 15.0;

    /// <summary>
    /// Verifica se a posição atingiu o alvo (+20 pts) ou o stop (-15 pts).
    /// Fecha via ordem de venda a mercado e registra o TradeRecord.
    /// </summary>
    private async Task VerificarStopTargetAsync(
        FeatureSnapshot snap, List<TradeRecord> trades)
    {
        if (_position.IsFlat) return;

        var unrealized = _position.UnrealizedPnL(snap.Price);
        string? reason  = null;

        if (_position.Side == BacktestPosition.PositionSide.Long)
        {
            if (snap.Price >= _position.EntryPrice + TargetPts) reason = "Target";
            else if (snap.Price <= _position.EntryPrice - StopPts)  reason = "Stop";
        }
        else // Short
        {
            if (snap.Price <= _position.EntryPrice - TargetPts) reason = "Target";
            else if (snap.Price >= _position.EntryPrice + StopPts)  reason = "Stop";
        }

        if (reason == null) return;

        await _signalLock.WaitAsync();
        try
        {
            if (_position.IsFlat) return; // verificação dupla dentro do lock

            OrderFill? exitFill = null;
            void OnFill(OrderFill f) => exitFill = f;
            _execution.OnOrderFilled += OnFill;
            try
            {
                await _execution.EnviarVendaAsync(_position.Quantity, OrderType.Market);
            }
            finally
            {
                _execution.OnOrderFilled -= OnFill;
            }

            double exitPrice = exitFill?.ExecutionPrice ?? snap.Bid;
            double slippage  = exitFill?.Slippage ?? 0;
            long   latency   = exitFill?.LatencyMs ?? 0;

            var trade = _position.Fechar(exitPrice, DateTime.UtcNow, reason);
            trade.Slippage = slippage;
            trade.NetPnl   = trade.GrossPnl - slippage;

            trades.Add(trade);
            _totalLatencyMs += latency;
            _latencyCount++;

            OnLog?.Invoke($"SAÍDA {reason} @ {exitPrice:F2} | " +
                          $"GrossPnL={trade.GrossPnl:F2} NetPnL={trade.NetPnl:F2} " +
                          $"slippage={slippage:F2}");
        }
        finally
        {
            _signalLock.Release();
        }
    }

    // ── Cálculo de resultado ──────────────────────────────────────────────
    /// <summary>
    /// Consolida as métricas de todas as operações da sessão.
    /// Separa StrategyAlpha (GrossPnL) de ExecutionAlpha (custo da execução).
    /// </summary>
    private BacktestResult CalcularResultado(
        List<TradeRecord> trades, BacktestConfig config, TimeSpan duration)
    {
        if (trades.Count == 0)
        {
            return new BacktestResult
            {
                Date     = config.Date,
                Duration = duration,
                Trades   = trades
            };
        }

        var wins   = trades.Where(t => t.NetPnl > 0).ToList();
        var losses = trades.Where(t => t.NetPnl <= 0).ToList();

        double grossWins   = wins.Sum(t => t.GrossPnl);
        double grossLosses = Math.Abs(losses.Sum(t => t.GrossPnl));

        var result = new BacktestResult
        {
            Date          = config.Date,
            Duration      = duration,
            TotalTrades   = trades.Count,
            WinTrades     = wins.Count,
            LossTrades    = losses.Count,
            WinRate       = trades.Count > 0
                                ? (double)wins.Count / trades.Count : 0,
            GrossPnL      = trades.Sum(t => t.GrossPnl),
            TotalSlippage = trades.Sum(t => t.Slippage),
            NetPnL        = trades.Sum(t => t.NetPnl),
            Expectancy    = trades.Count > 0
                                ? trades.Sum(t => t.NetPnl) / trades.Count : 0,
            ProfitFactor  = grossLosses > 0
                                ? grossWins / grossLosses : grossWins > 0 ? double.MaxValue : 0,
            MaxDrawdown   = CalcularMaxDrawdown(trades),
            AvgLatencyMs  = _latencyCount > 0
                                ? (double)_totalLatencyMs / _latencyCount : 0,
            AvgSlippage   = trades.Count > 0
                                ? trades.Average(t => t.Slippage) : 0,
            // Alpha decomposition
            StrategyAlpha  = trades.Sum(t => t.GrossPnl),
            ExecutionAlpha = trades.Sum(t => t.NetPnl) - trades.Sum(t => t.GrossPnl),
            Trades         = trades
        };

        // Sharpe intradiário: Expectancy / std(returns)
        if (trades.Count > 1)
        {
            var returns = trades.Select(t => t.NetPnl).ToList();
            var mean    = returns.Average();
            var std     = Math.Sqrt(returns.Average(r => Math.Pow(r - mean, 2)));
            result.Sharpe = std > 0 ? mean / std : 0;
        }

        return result;
    }

    private static double CalcularMaxDrawdown(List<TradeRecord> trades)
    {
        double peak     = 0;
        double cumPnl   = 0;
        double maxDD    = 0;

        foreach (var t in trades)
        {
            cumPnl += t.NetPnl;
            if (cumPnl > peak) peak = cumPnl;
            var dd = peak - cumPnl;
            if (dd > maxDD) maxDD = dd;
        }

        return maxDD;
    }
}
