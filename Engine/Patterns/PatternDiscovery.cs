using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MarketCore.Engine.Dataset;

namespace MarketCore.Engine.Patterns;

/// <summary>
/// Motor de descoberta estatística de padrões.
/// Roda OFFLINE sobre o dataset gerado pelo DatasetBuilder (Fase 7).
/// Usa divisão 70/15/15 (treino / validação / out-of-sample).
/// Não usa IA — apenas combinações de thresholds e métricas estatísticas.
/// </summary>
public class PatternDiscovery
{
    private readonly PatternEvaluator _evaluator = new();

    // ── Features candidatas ───────────────────────────────────────────────

    private readonly List<string> _candidateFeatures = new()
    {
        "BookImbalance",
        "Delta1s",
        "Delta5s",
        "Ofi1s",
        "TradeRate",
        "VolumeRate",
        "AbsorptionScore",
        "Velocity",
        "Volatility30s",
        "DistanceVwap",
        "AggressionRatio",
        "StackingScore",
        "PullingScore"
    };

    private readonly Dictionary<string, double[]> _candidateThresholds = new()
    {
        ["BookImbalance"]   = new[] { 0.3, 0.5, 0.6, 0.7 },
        ["Delta1s"]         = new[] { 200.0, 400.0, 600.0, -200.0, -400.0, -600.0 },
        ["Delta5s"]         = new[] { 500.0, 1000.0, -500.0, -1000.0 },
        ["Ofi1s"]           = new[] { 100.0, 300.0, 500.0, -100.0, -300.0, -500.0 },
        ["TradeRate"]       = new[] { 150.0, 200.0, 250.0, 300.0 },
        ["VolumeRate"]      = new[] { 1.0, 1.5, 2.0 },
        ["AbsorptionScore"] = new[] { 40.0, 60.0, 80.0, -40.0, -60.0, -80.0 },
        ["Velocity"]        = new[] { 2.0, 5.0, -2.0, -5.0 },
        ["Volatility30s"]   = new[] { 0.1, 0.2, 0.3 },
        ["DistanceVwap"]    = new[] { 20.0, 50.0, 100.0 },
        ["AggressionRatio"] = new[] { 0.55, 0.65, 0.75 },
        ["StackingScore"]   = new[] { 30.0, 50.0 },
        ["PullingScore"]    = new[] { -30.0, -50.0 }
    };

    // ── Critérios mínimos de qualidade ───────────────────────────────────

    public int    MinSamples      { get; set; } = 200;
    public double MinExpectancy   { get; set; } = 2.0;
    public double MinProfitFactor { get; set; } = 1.5;
    public double MinWinRate      { get; set; } = 0.55;

    // ── Evento ───────────────────────────────────────────────────────────

    /// <summary>Disparado quando um padrão passa em todos os critérios e conjuntos.</summary>
    public event Action<DiscoveredPattern>? OnPatternFound;

    // ── API pública ───────────────────────────────────────────────────────

    /// <summary>
    /// Descobre padrões no dataset usando divisão 70/15/15.
    /// Aprovação exige passar no treino, validação E out-of-sample.
    /// </summary>
    public async Task<List<DiscoveredPattern>> DescubrirAsync(List<DatasetRecord> dataset)
    {
        if (dataset.Count < 100)
            return new List<DiscoveredPattern>();

        return await Task.Run(() =>
        {
            // PASSO 1 — Dividir dataset: 70 / 15 / 15
            int n   = dataset.Count;
            int n70 = (int)(n * 0.70);
            int n85 = (int)(n * 0.85);

            var treino      = dataset.Take(n70).ToList();
            var validacao   = dataset.Skip(n70).Take(n85 - n70).ToList();
            var outOfSample = dataset.Skip(n85).ToList();

            string periodoTreino = PeriodoLabel(treino);
            string periodoVal    = PeriodoLabel(validacao);
            string periodoOOS    = PeriodoLabel(outOfSample);

            var aprovados = new List<DiscoveredPattern>();
            int nextId    = 1;

            // PASSO 2 — Gerar combinações de 2-3 condições
            foreach (var featA in _candidateFeatures)
            {
                if (!_candidateThresholds.TryGetValue(featA, out var thresholdsA)) continue;

                foreach (double ta in thresholdsA)
                {
                    foreach (string opA in new[] { ">", "<" })
                    {
                        var condA = new PatternCondition { Feature = featA, Operator = opA, Threshold = ta };
                        var conds1 = new List<PatternCondition> { condA };

                        // Testar 1 condição no treino (pré-filtro rápido)
                        var stats1 = _evaluator.CalcularStats(treino, conds1);
                        if (stats1.SampleCount < MinSamples) continue;

                        // Combinar com 2ª condição
                        foreach (var featB in _candidateFeatures)
                        {
                            if (featB == featA) continue;
                            if (!_candidateThresholds.TryGetValue(featB, out var thresholdsB)) continue;

                            foreach (double tb in thresholdsB)
                            {
                                foreach (string opB in new[] { ">", "<" })
                                {
                                    var condB  = new PatternCondition { Feature = featB, Operator = opB, Threshold = tb };
                                    var conds2 = new List<PatternCondition> { condA, condB };

                                    var stats2 = _evaluator.CalcularStats(treino, conds2);
                                    if (!PassaNoCriterio(stats2)) continue;

                                    // Tentar 3ª condição
                                    bool found3 = false;
                                    foreach (var featC in _candidateFeatures)
                                    {
                                        if (featC == featA || featC == featB) continue;
                                        if (!_candidateThresholds.TryGetValue(featC, out var thresholdsC)) continue;

                                        foreach (double tc in thresholdsC)
                                        {
                                            foreach (string opC in new[] { ">", "<" })
                                            {
                                                var condC  = new PatternCondition { Feature = featC, Operator = opC, Threshold = tc };
                                                var conds3 = new List<PatternCondition> { condA, condB, condC };

                                                var stats3 = _evaluator.CalcularStats(treino, conds3);
                                                if (!PassaNoCriterio(stats3)) continue;

                                                // PASSO 3 — Validar em validacao (>= 70% da performance)
                                                var statsVal = _evaluator.CalcularStats(validacao, conds3);
                                                if (statsVal.Expectancy < stats3.Expectancy * 0.70) continue;

                                                // Validar em out-of-sample
                                                var statsOOS = _evaluator.CalcularStats(outOfSample, conds3);
                                                if (statsOOS.Expectancy < stats3.Expectancy * 0.70) continue;

                                                var pattern = new DiscoveredPattern
                                                {
                                                    PatternId        = nextId++,
                                                    Version          = 1,
                                                    CreatedAt        = DateTime.Now,
                                                    Conditions       = conds3,
                                                    TrainingStats    = stats3,
                                                    ValidationStats  = statsVal,
                                                    OutOfSampleStats = statsOOS,
                                                    TrainingPeriod   = periodoTreino,
                                                    ValidationPeriod = periodoVal,
                                                    TestPeriod       = periodoOOS,
                                                    Status           = PatternStatus.Approved,
                                                    PrimaryRegime    = RegimeDominante(treino, conds3),
                                                    DiscoveryWinRate = stats3.WinRate,
                                                    RecentWinRate    = stats3.WinRate
                                                };
                                                aprovados.Add(pattern);
                                                OnPatternFound?.Invoke(pattern);
                                                found3 = true;
                                            }
                                            if (found3) break;
                                        }
                                        if (found3) break;
                                    }

                                    // Se não encontrou 3ª condição, registrar padrão de 2 condições
                                    if (!found3)
                                    {
                                        var statsVal2 = _evaluator.CalcularStats(validacao, conds2);
                                        if (statsVal2.Expectancy < stats2.Expectancy * 0.70) continue;

                                        var statsOOS2 = _evaluator.CalcularStats(outOfSample, conds2);
                                        if (statsOOS2.Expectancy < stats2.Expectancy * 0.70) continue;

                                        var pattern = new DiscoveredPattern
                                        {
                                            PatternId        = nextId++,
                                            Version          = 1,
                                            CreatedAt        = DateTime.Now,
                                            Conditions       = conds2,
                                            TrainingStats    = stats2,
                                            ValidationStats  = statsVal2,
                                            OutOfSampleStats = statsOOS2,
                                            TrainingPeriod   = periodoTreino,
                                            ValidationPeriod = periodoVal,
                                            TestPeriod       = periodoOOS,
                                            Status           = PatternStatus.Approved,
                                            PrimaryRegime    = RegimeDominante(treino, conds2),
                                            DiscoveryWinRate = stats2.WinRate,
                                            RecentWinRate    = stats2.WinRate
                                        };
                                        aprovados.Add(pattern);
                                        OnPatternFound?.Invoke(pattern);
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // PASSO 4 — Ordenar por Expectancy DESC
            aprovados.Sort((a, b) =>
                b.TrainingStats.Expectancy.CompareTo(a.TrainingStats.Expectancy));

            // PASSO 5 — Eliminar padrões redundantes (correlação > 0.85)
            return EliminarRedundantes(aprovados, dataset);
        });
    }

    /// <summary>Valida um padrão já descoberto em novos dados.</summary>
    public PatternStats ValidarAsync(DiscoveredPattern pattern, List<DatasetRecord> novosData)
        => _evaluator.AvaliarPadrao(pattern, novosData);

    // ── Helpers ───────────────────────────────────────────────────────────

    private bool PassaNoCriterio(PatternStats stats)
        => stats.SampleCount  >= MinSamples
        && stats.Expectancy   >= MinExpectancy
        && stats.ProfitFactor >= MinProfitFactor
        && stats.WinRate      >= MinWinRate;

    /// <summary>
    /// Elimina padrões redundantes: se dois padrões classificam >85% dos mesmos records,
    /// mantém apenas o de maior Expectancy.
    /// </summary>
    private List<DiscoveredPattern> EliminarRedundantes(
        List<DiscoveredPattern> padroes, List<DatasetRecord> dataset)
    {
        var resultado  = new List<DiscoveredPattern>();
        var sinais     = new Dictionary<int, HashSet<int>>();

        // Pré-computar índices de records para cada padrão
        for (int i = 0; i < padroes.Count; i++)
        {
            var idx = new HashSet<int>();
            for (int j = 0; j < dataset.Count; j++)
                if (_evaluator.Satisfaz(dataset[j], padroes[i].Conditions))
                    idx.Add(j);
            sinais[i] = idx;
        }

        var removidos = new HashSet<int>();
        for (int i = 0; i < padroes.Count; i++)
        {
            if (removidos.Contains(i)) continue;
            resultado.Add(padroes[i]);

            for (int j = i + 1; j < padroes.Count; j++)
            {
                if (removidos.Contains(j)) continue;
                var setI  = sinais[i];
                var setJ  = sinais[j];
                if (setI.Count == 0 || setJ.Count == 0) continue;

                int intersec = setI.Intersect(setJ).Count();
                double corr  = intersec / (double)Math.Min(setI.Count, setJ.Count);
                if (corr > 0.85) removidos.Add(j);
            }
        }
        return resultado;
    }

    private static string PeriodoLabel(List<DatasetRecord> records)
    {
        if (records.Count == 0) return string.Empty;
        var ini = new DateTime(records.First().Features.Timestamp);
        var fim = new DateTime(records.Last().Features.Timestamp);
        return $"{ini:yyyy-MM-dd} a {fim:yyyy-MM-dd} ({records.Count} records)";
    }

    private string RegimeDominante(List<DatasetRecord> treino,
                                   List<PatternCondition> conditions)
    {
        var filtrados = treino.Where(r => _evaluator.Satisfaz(r, conditions));
        var regime    = filtrados
            .GroupBy(r => r.Features.Regime ?? "Unknown")
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        return regime?.Key ?? "Unknown";
    }
}
