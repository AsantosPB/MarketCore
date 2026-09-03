using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MarketCore.Engine.Dataset;
using MarketCore.Engine.Features;

namespace MarketCore.Engine.Patterns;

public class LivePatternDiscovery : IDisposable
{
    private readonly FeatureEngine   _featureEngine;
    private readonly PatternRegistry _registry;
    private Timer?                   _timer;
    private volatile bool            _rodando;

    private static readonly string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        "mcie_patterns.log");
    private int  _logCount  = 0;                  // [FASE 3] controle de tamanho do log
    private const long MaxLogBytes = 1 * 1024 * 1024;  // 1 MB
    private const int  MaxLogLines = 500;

    public int IntervalMinutos   { get; set; } = 1;  // [TEMP-TEST] era 3
    public int WarmupMinutos     { get; set; } = 2;  // [TEMP-TEST] era 30
    public int HorizonteMaximoMs { get; set; } = 10000;

    public event Action<int>? OnPadroesAtualizados;

    public LivePatternDiscovery(FeatureEngine featureEngine, PatternRegistry registry)
    {
        _featureEngine = featureEngine ?? throw new ArgumentNullException(nameof(featureEngine));
        _registry      = registry      ?? throw new ArgumentNullException(nameof(registry));
    }

    private void Log(string msg)
    {
        var linha = $"{DateTime.Now:HH:mm:ss} {msg}";
        Console.WriteLine(linha);
        try
        {
            _logCount++;
            if (_logCount % 100 == 0)                   // [FASE 3] verificar tamanho a cada 100 linhas
            {
                var fi = new FileInfo(_logPath);
                if (fi.Exists && fi.Length > MaxLogBytes)
                {
                    var linhas = File.ReadAllLines(_logPath);
                    var manter = linhas.Skip(Math.Max(0, linhas.Length - MaxLogLines)).ToArray();
                    File.WriteAllLines(_logPath, manter);
                }
            }
            File.AppendAllText(_logPath, linha + Environment.NewLine);
        }
        catch { }
    }

    public void Iniciar()
    {
        _timer = new Timer(
            _ => ExecutarCiclo(),
            null,
            TimeSpan.FromMinutes(WarmupMinutos),
            TimeSpan.FromMinutes(IntervalMinutos));
        _rodando = true;
        Log($"[LIVE-PATTERN] Timer iniciado. Warm-up: {WarmupMinutos}min Intervalo: {IntervalMinutos}min");
    }

    public void AlterarIntervalo(int minutos)
    {
        if (minutos < 1 || minutos > 30) return;
        IntervalMinutos = minutos;
        _timer?.Change(TimeSpan.Zero, TimeSpan.FromMinutes(minutos));
        Log($"[LIVE-PATTERN] Intervalo alterado para {minutos}min");
    }

    public void Parar()
    {
        _rodando = false;
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void ExecutarCiclo()
    {
        if (!_rodando) return;
        try
        {
            var snapshots = _featureEngine.SnapshotsHoje;
            Log($"[LIVE-PATTERN] Ciclo executado: {DateTime.Now:HH:mm:ss} snapshots={snapshots?.Count ?? 0}");

            if (snapshots == null || snapshots.Count < 1000)
            {
                Log($"[LIVE-PATTERN] Ciclo ignorado — {snapshots?.Count ?? 0} snapshots (min 1000)");
                return;
            }

            var agora     = DateTime.Now.Ticks;                              // [FIX] era UtcNow
            var elegiveis = snapshots
                .Where(s => agora - s.Timestamp > (long)HorizonteMaximoMs * 10000L)
                .ToList();

            if (elegiveis.Count < 500)
            {
                Log($"[LIVE-PATTERN] Ciclo ignorado — {elegiveis.Count} elegíveis (min 500)");
                return;
            }

            var dataset = CalcularLabels(elegiveis);
            Log($"[LIVE-PATTERN] Elegiveis: {elegiveis.Count} Dataset: {dataset?.Count ?? 0}");

            if (dataset.Count < 500)
            {
                Log($"[LIVE-PATTERN] Ciclo ignorado — {dataset.Count} labels (min 500)");
                return;
            }

            // ── Diagnóstico distribuição labels/features ─────────── [DIAG]
            var retornos1s = dataset.Select(d => d.Labels.FutureReturn1s).ToList();
            Log($"[LABELS-1s] count={retornos1s.Count} " +
                $"zeros={retornos1s.Count(r => r == 0)} " +
                $"min={retornos1s.Min():F1} " +
                $"max={retornos1s.Max():F1} " +
                $"avg={retornos1s.Average():F1} " +
                $"positivos={retornos1s.Count(r => r > 5)} " +
                $"negativos={retornos1s.Count(r => r < -5)}");

            var retornos2s = dataset.Select(d => d.Labels.FutureReturn2s).ToList();
            Log($"[LABELS-2s] count={retornos2s.Count} " +
                $"zeros={retornos2s.Count(r => r == 0)} " +
                $"min={retornos2s.Min():F1} max={retornos2s.Max():F1} avg={retornos2s.Average():F1} " +
                $"positivos={retornos2s.Count(r => r > 5)} negativos={retornos2s.Count(r => r < -5)}");

            var retornos5s = dataset.Select(d => d.Labels.FutureReturn5s).ToList();
            Log($"[LABELS-5s] count={retornos5s.Count} " +
                $"zeros={retornos5s.Count(r => r == 0)} " +
                $"min={retornos5s.Min():F1} " +
                $"max={retornos5s.Max():F1} " +
                $"avg={retornos5s.Average():F1} " +
                $"positivos={retornos5s.Count(r => r > 5)} " +
                $"negativos={retornos5s.Count(r => r < -5)}");

            var imbs = dataset.Select(d => d.Features.BookImbalance).ToList();
            Log($"[FEATURES] BookImbalance: " +
                $"min={imbs.Min():F3} max={imbs.Max():F3} avg={imbs.Average():F3}");

            var deltas = dataset.Select(d => d.Features.Delta1s).ToList();
            Log($"[FEATURES] Delta1s: " +
                $"min={deltas.Min():F0} max={deltas.Max():F0} avg={deltas.Average():F0}");

            var ofis = dataset.Select(d => d.Features.Ofi1s).ToList();
            Log($"[FEATURES] Ofi1s: " +
                $"min={ofis.Min():F3} max={ofis.Max():F3} avg={ofis.Average():F3}");

            var testCond1 = dataset.Where(d => d.Features.Delta1s > 100).ToList();
            var testWins1 = testCond1.Count(d => d.Labels.FutureReturn1s > 5);
            Log($"[TEST] Delta1s>100: amostras={testCond1.Count} wins={testWins1} " +
                $"winrate={(testCond1.Count > 0 ? (double)testWins1 / testCond1.Count * 100 : 0):F1}%");
            var testWins1_2s = testCond1.Count(d => d.Labels.FutureReturn2s > 5);
            Log($"[TEST-2s] Delta1s>100: amostras={testCond1.Count} wins={testWins1_2s} " +
                $"winrate={(testCond1.Count > 0 ? (double)testWins1_2s / testCond1.Count * 100 : 0):F1}%");
            var testWins1_5s = testCond1.Count(d => d.Labels.FutureReturn5s > 5);
            Log($"[TEST-5s] Delta1s>100: amostras={testCond1.Count} wins={testWins1_5s} " +
                $"winrate={(testCond1.Count > 0 ? (double)testWins1_5s / testCond1.Count * 100 : 0):F1}%");

            var testCond2 = dataset.Where(d => d.Features.BookImbalance > 0.3).ToList();
            var testWins2 = testCond2.Count(d => d.Labels.FutureReturn1s > 5);
            Log($"[TEST] BookImb>0.3: amostras={testCond2.Count} wins={testWins2} " +
                $"winrate={(testCond2.Count > 0 ? (double)testWins2 / testCond2.Count * 100 : 0):F1}%");

            var discovery = new PatternDiscovery
            {
                MinSamples      = 20,
                MinExpectancy   = 0.2,
                MinProfitFactor = 1.02,
                MinWinRate      = 0.40
            };

            var novos = discovery.DescubrirAsync(dataset).GetAwaiter().GetResult();

            // Acumular: só adiciona padrões que ainda não existem
            int adicionados = 0;
            foreach (var p in novos)
            {
                var jaExiste = _registry.PadroesAtivos()
                    .Any(existing =>
                        existing.Conditions.Count == p.Conditions.Count &&
                        existing.Conditions.All(c =>
                            p.Conditions.Any(nc =>
                                nc.Feature   == c.Feature &&
                                nc.Operator  == c.Operator &&
                                Math.Abs(nc.Threshold - c.Threshold) < 0.001)));
                if (!jaExiste)
                {
                    _registry.AdicionarIntradayAsync(p).GetAwaiter().GetResult(); // [FASE 3] persiste no SQLite
                    adicionados++;
                }
            }

            // Monitorar decay dos padrões acumulados
            _registry.MonitorarDecayAsync(dataset).GetAwaiter().GetResult();

            var total = _registry.PadroesAtivos().Count;
            Log($"[LIVE-PATTERN] Ciclo concluído — {novos.Count} descobertos, {adicionados} adicionados, {total} ativos ({DateTime.Now:HH:mm})");
            OnPadroesAtualizados?.Invoke(total);
        }
        catch (Exception ex)
        {
            Log($"[LIVE-PATTERN] Erro: {ex.Message}");
        }
    }

    private List<DatasetRecord> CalcularLabels(List<FeatureSnapshot> snapshots)
    {
        const long ticksPerMs = 10_000L;   // 1ms = 10.000 ticks (DateTime.Now.Ticks)

        var dataset = new List<DatasetRecord>();
        for (int i = 0; i < snapshots.Count; i++)
        {
            var snap = snapshots[i];
            if (snap.Price <= 0) continue;          // [FIX] ignora snapshots pré-trade (Price=0)

            var t0 = snap.Timestamp;

            // [FASE 3] O(log n) binary search — era O(n) linear scan (O(n²) total)
            int idx1s  = FindClosestIndex(snapshots, t0 + 1000  * ticksPerMs);
            int idx2s  = FindClosestIndex(snapshots, t0 + 2000  * ticksPerMs);
            int idx5s  = FindClosestIndex(snapshots, t0 + 5000  * ticksPerMs);
            int idx10s = FindClosestIndex(snapshots, t0 + 10000 * ticksPerMs);

            var after1s  = idx1s  >= 0 ? snapshots[idx1s]  : null;
            var after2s  = idx2s  >= 0 ? snapshots[idx2s]  : null;
            var after5s  = idx5s  >= 0 ? snapshots[idx5s]  : null;
            var after10s = idx10s >= 0 ? snapshots[idx10s] : null;

            if (after1s == null || after2s == null) continue;
            if (after1s.Price <= 0 || after2s.Price <= 0) continue;  // [FIX] preço futuro inválido

            // [FASE 3] Mfe/Mae: janela (t0, t0+5s] — O(log n) bounds + scan linear limitado a 5s
            int bStart = FindClosestIndex(snapshots, t0 + 1);
            int bEnd   = FindClosestIndex(snapshots, t0 + 5000 * ticksPerMs + 1);
            if (bStart < 0) bStart = snapshots.Count;
            if (bEnd   < 0) bEnd   = snapshots.Count;

            double mfe = 0, mae = 0;
            for (int j = bStart; j < bEnd; j++)
            {
                var diff = snapshots[j].Price - snap.Price;
                if (diff > mfe) mfe = diff;
                if (diff < mae) mae = diff;
            }

            dataset.Add(new DatasetRecord
            {
                Features = snap.ToMarketSnapshot(),
                Labels   = new LabelRecord
                {
                    Timestamp       = t0,
                    FutureReturn1s  = after1s.Price  - snap.Price,
                    FutureReturn2s  = after2s.Price  - snap.Price,
                    FutureReturn5s  = after5s  != null ? after5s.Price  - snap.Price : 0,
                    FutureReturn10s = after10s != null ? after10s.Price - snap.Price : 0,
                    Mfe5s           = mfe,
                    Mae5s           = mae,
                }
            });
        }
        return dataset;
    }

    /// <summary>
    /// Busca binária: retorna o índice do primeiro elemento com Timestamp >= targetTimestamp.
    /// Retorna -1 se nenhum elemento satisfizer a condição. O(log n).
    /// </summary>
    private static int FindClosestIndex(List<FeatureSnapshot> list, long targetTimestamp)
    {
        int lo = 0, hi = list.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (list[mid].Timestamp < targetTimestamp)
                lo = mid + 1;
            else
                hi = mid;
        }
        return (lo < list.Count && list[lo].Timestamp >= targetTimestamp) ? lo : -1;
    }

    public void Dispose()
    {
        Parar();
        _timer?.Dispose();
    }
}
