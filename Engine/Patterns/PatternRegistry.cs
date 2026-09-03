using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MarketCore.Engine.Dataset;
using MarketCore.Engine.Storage;

namespace MarketCore.Engine.Patterns;

/// <summary>
/// Catálogo central de padrões com lifecycle completo.
/// Persiste padrões aprovados no SQLite e monitora decay em tempo real.
/// </summary>
public class PatternRegistry
{
    private readonly StorageManager  _storage;
    private readonly PatternEvaluator _evaluator = new();
    private List<DiscoveredPattern>   _approved  = new();

    public PatternRegistry(StorageManager storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    // ── Evento ───────────────────────────────────────────────────────────

    /// <summary>Disparado quando um padrão ativo entra em estado de decay.</summary>
    public event Action<DiscoveredPattern>? OnPatternDecay;

    // ── API pública ───────────────────────────────────────────────────────

    /// <summary>Carrega padrões aprovados (Approved ou Live) do SQLite.</summary>
    public async Task InicializarAsync()
    {
        var todos = await _storage.CarregarPadroesAsync(null);
        var limiteIntraday = DateTime.Today.AddDays(-2);  // [FASE 3] Paper só últimos 2 dias
        _approved = todos
            .Where(p => p.Status == PatternStatus.Approved
                     || p.Status == PatternStatus.Live
                     || p.Status == PatternStatus.Monitoring
                     || (p.Status == PatternStatus.Paper && p.CreatedAt >= limiteIntraday))
            .ToList();

        Console.WriteLine(
            $"[PatternRegistry] Inicializado: {_approved.Count} padrão(s) ativo(s) carregado(s).");
    }

    /// <summary>Adiciona e persiste um novo padrão descoberto.</summary>
    public async Task AdicionarAsync(DiscoveredPattern pattern)
    {
        await _storage.SalvarPadraoAsync(pattern);
        _approved.Add(pattern);

        Console.WriteLine(
            $"[PatternRegistry] Padrão #{pattern.PatternId} adicionado. " +
            $"Condições: {pattern.Conditions.Count} | " +
            $"WinRate: {pattern.TrainingStats.WinRate:P1} | " +
            $"Expectancy: {pattern.TrainingStats.Expectancy:F2}");
    }

    /// <summary>Atualiza o status de um padrão na memória e no SQLite.</summary>
    public async Task AtualizarStatusAsync(int patternId, PatternStatus novoStatus)
    {
        var pat = _approved.FirstOrDefault(p => p.PatternId == patternId);
        if (pat != null)
            pat.Status = novoStatus;

        await _storage.AtualizarStatusPadraoAsync(patternId, novoStatus);
    }

    /// <summary>Retorna padrões com status Approved ou Live.</summary>
    public List<DiscoveredPattern> PadroesAtivos()
        => _approved
            .Where(p => p.Status == PatternStatus.Approved
                     || p.Status == PatternStatus.Live
                     || p.Status == PatternStatus.Paper)   // [FASE 3] inclui intraday
            .ToList();

    /// <summary>Remove padrões intraday (Paper) descobertos hoje — chamado antes de novo ciclo.</summary>
    public void LimparPadroesIntraday()
    {
        _approved.RemoveAll(p =>
            p.Status == PatternStatus.Paper
            && p.CreatedAt.Date == DateTime.Today);
    }

    /// <summary>Adiciona padrão intraday com status Paper (somente memória).</summary>
    public void AdicionarIntraday(DiscoveredPattern pattern)
    {
        pattern.Status    = PatternStatus.Paper;
        pattern.CreatedAt = DateTime.Now;
        _approved.Add(pattern);
    }

    /// <summary>Adiciona padrão intraday com status Paper e persiste no SQLite. [FASE 3]</summary>
    public async Task AdicionarIntradayAsync(DiscoveredPattern pattern)
    {
        pattern.Status    = PatternStatus.Paper;
        pattern.CreatedAt = DateTime.Now;
        await _storage.SalvarPadraoAsync(pattern);
        _approved.Add(pattern);
        Console.WriteLine(
            $"[PatternRegistry] Paper #{pattern.PatternId} salvo. " +
            $"WinRate={pattern.TrainingStats.WinRate:P1} Expectancy={pattern.TrainingStats.Expectancy:F2}");
    }

    /// <summary>Remove Paper patterns com mais de 2 dias do SQLite e da memória. [FASE 3]</summary>
    public async Task LimparPadroesAntigosAsync()
    {
        var limite  = DateTime.Today.AddDays(-2);
        var antigos = _approved
            .Where(p => p.Status == PatternStatus.Paper && p.CreatedAt < limite)
            .ToList();

        foreach (var p in antigos)
        {
            _approved.Remove(p);
            await _storage.AtualizarStatusPadraoAsync(p.PatternId, PatternStatus.Deprecated);
        }

        if (antigos.Count > 0)
            Console.WriteLine(
                $"[PatternRegistry] {antigos.Count} Paper pattern(s) > 2 dias marcados como Deprecated.");
    }

    /// <summary>
    /// Monitora decay: verifica se win rate recente caiu > 15pp em relação à descoberta.
    /// Padrões em decay são movidos para status Decay e OnPatternDecay é disparado.
    /// </summary>
    public async Task MonitorarDecayAsync(List<DatasetRecord> dadosRecentes)
    {
        foreach (var padrao in _approved.ToList())
        {
            // Paper: remover por decay sem persistir no SQLite
            if (padrao.Status == PatternStatus.Paper)
            {
                var statsPaper = _evaluator.AvaliarPadrao(padrao, dadosRecentes);
                if (statsPaper.SampleCount >= 10 && statsPaper.WinRate < 0.35)
                {
                    _approved.Remove(padrao);
                    Console.WriteLine($"[PatternRegistry] Paper #{padrao.PatternId} removido por decay (wr={statsPaper.WinRate:P1})");
                }
                continue;
            }

            if (padrao.Status != PatternStatus.Approved
             && padrao.Status != PatternStatus.Live)
                continue;

            var stats = _evaluator.AvaliarPadrao(padrao, dadosRecentes);
            if (stats.SampleCount < 10) continue;

            padrao.RecentWinRate = stats.WinRate;

            if (padrao.InDecay)
            {
                Console.WriteLine(
                    $"[PatternRegistry] Padrão #{padrao.PatternId} em decay: " +
                    $"discovery={padrao.DiscoveryWinRate:P1} " +
                    $"recent={padrao.RecentWinRate:P1}");

                await AtualizarStatusAsync(padrao.PatternId, PatternStatus.Decay);
                OnPatternDecay?.Invoke(padrao);
            }
        }
    }
}
