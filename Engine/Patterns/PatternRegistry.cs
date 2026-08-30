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
        _approved = todos
            .Where(p => p.Status == PatternStatus.Approved
                     || p.Status == PatternStatus.Live
                     || p.Status == PatternStatus.Paper
                     || p.Status == PatternStatus.Monitoring)
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
                     || p.Status == PatternStatus.Live)
            .ToList();

    /// <summary>
    /// Monitora decay: verifica se win rate recente caiu > 15pp em relação à descoberta.
    /// Padrões em decay são movidos para status Decay e OnPatternDecay é disparado.
    /// </summary>
    public async Task MonitorarDecayAsync(List<DatasetRecord> dadosRecentes)
    {
        foreach (var padrao in _approved.ToList())
        {
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
