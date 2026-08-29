using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MarketCore.Engine.Storage;

namespace MarketCore.Engine.Dataset;

/// <summary>
/// Motor offline de geração de labels.
/// Calcula retornos futuros e excursões sobre os snapshots já gravados no DuckDB.
/// NÃO roda durante o pregão — é acionado às 18:05 pelo DatasetTimer
/// ou manualmente pelo operador.
/// </summary>
public class DatasetBuilder
{
    private readonly StorageManager _storage;

    public DatasetBuilder(StorageManager storage)
        => _storage = storage;

    // ── API pública ───────────────────────────────────────────────────────

    /// <summary>
    /// Constrói o dataset para uma data específica.
    /// Carrega snapshots do DuckDB, calcula labels e persiste o resultado.
    /// </summary>
    public async Task<DatasetStats> BuildAsync(DateTime date)
    {
        // PASSO 1 — Carregar snapshots do dia e ordenar por Timestamp ASC
        var snapshots = await _storage.ConsultarSnapshotsAsync(
            date.Date, date.Date.AddDays(1));
        snapshots = snapshots.OrderBy(s => s.Timestamp).ToList();

        if (snapshots.Count < 2)
            return new DatasetStats { Date = date.Date, BuildTime = DateTime.Now };

        var labels = new List<LabelRecord>(snapshots.Count);

        for (int i = 0; i < snapshots.Count; i++)
        {
            var    t0 = snapshots[i];
            double p0 = t0.Price;

            // ─────────────────────────────────────────────────────────────
            // REGRA ABSOLUTA DE LOOK-AHEAD BIAS:
            // labels só usam dados com index > i (timestamp > T0)
            // NUNCA usar snapshots[j] onde j <= i para calcular labels
            // ─────────────────────────────────────────────────────────────

            var label = new LabelRecord { Timestamp = t0.Timestamp };

            // a-g. Retornos futuros em múltiplos horizontes
            label.FutureReturn100ms = RetornoFuturo(snapshots, i, t0.Timestamp,   100, p0);
            label.FutureReturn250ms = RetornoFuturo(snapshots, i, t0.Timestamp,   250, p0);
            label.FutureReturn500ms = RetornoFuturo(snapshots, i, t0.Timestamp,   500, p0);
            label.FutureReturn1s    = RetornoFuturo(snapshots, i, t0.Timestamp,  1000, p0);
            label.FutureReturn2s    = RetornoFuturo(snapshots, i, t0.Timestamp,  2000, p0);
            label.FutureReturn5s    = RetornoFuturo(snapshots, i, t0.Timestamp,  5000, p0);
            label.FutureReturn10s   = RetornoFuturo(snapshots, i, t0.Timestamp, 10000, p0);

            // h-i. MFE e MAE nos 5 s futuros (somente index > i — REGRA)
            long t0Plus5s = t0.Timestamp + TimeSpan.FromSeconds(5).Ticks;
            var moves5s   = snapshots
                .Skip(i + 1)              // REGRA: somente index > i
                .TakeWhile(s => s.Timestamp <= t0Plus5s)
                .Select(s => s.Price - p0)
                .ToList();
            label.Mfe5s = moves5s.Count > 0 ? moves5s.Max() : 0;  // max favorable
            label.Mae5s = moves5s.Count > 0 ? moves5s.Min() : 0;  // max adverse (negativo)

            // j-k. Tempos até alvo e stop nos 30 s futuros (somente index > i — REGRA)
            long t0Plus30s = t0.Timestamp + TimeSpan.FromSeconds(30).Ticks;
            label.TimeTo20Pts = -1;
            label.TimeToStop  = -1;
            for (int j = i + 1; j < snapshots.Count; j++)  // REGRA: j > i
            {
                var sj = snapshots[j];
                if (sj.Timestamp > t0Plus30s) break;

                if (label.TimeTo20Pts < 0 && sj.Price >= p0 + 20)
                    label.TimeTo20Pts = Ms(sj.Timestamp - t0.Timestamp);

                if (label.TimeToStop < 0 && sj.Price <= p0 - 15)
                    label.TimeToStop = Ms(sj.Timestamp - t0.Timestamp);

                if (label.TimeTo20Pts >= 0 && label.TimeToStop >= 0)
                    break;
            }

            labels.Add(label);
        }

        // PASSO 3 — Salvar labels no DuckDB
        await _storage.SalvarLabelsAsync(labels);

        // PASSO 4 — Calcular e retornar DatasetStats
        var r1s = labels.Select(l => l.FutureReturn1s).ToList();
        var stats = new DatasetStats
        {
            Date             = date.Date,
            TotalSnapshots   = snapshots.Count,
            LabeledSnapshots = labels.Count,
            AvgReturn1s      = r1s.Count > 0 ? r1s.Average() : 0,
            StdReturn1s      = DesvioPadrao(r1s),
            SkewnessReturn1s = Assimetria(r1s),
            UpMoves          = r1s.Count(r => r >  5),
            DownMoves        = r1s.Count(r => r < -5),
            Neutral          = r1s.Count(r => Math.Abs(r) <= 5),
            BuildTime        = DateTime.Now
        };

        await _storage.SalvarDatasetStatsAsync(stats);
        return stats;
    }

    /// <summary>
    /// Constrói o dataset para um intervalo de datas,
    /// pulando dias que já têm labels calculados.
    /// </summary>
    public async Task<List<DatasetStats>> BuildRangeAsync(DateTime inicio, DateTime fim)
    {
        var resultados = new List<DatasetStats>();
        for (var d = inicio.Date; d <= fim.Date; d = d.AddDays(1))
        {
            if (await _storage.DiaTemLabelsAsync(d)) continue;
            resultados.Add(await BuildAsync(d));
        }
        return resultados;
    }

    // ── Auxiliares de cálculo ─────────────────────────────────────────────

    /// <summary>
    /// Retorna price(T0 + offsetMs) - price(T0) usando apenas snapshots com index > i.
    /// REGRA: j começa em i+1, nunca em j <= i.
    /// </summary>
    private static double RetornoFuturo(
        List<MarketSnapshot> snaps, int i, long t0Ticks, int offsetMs, double p0)
    {
        long alvo = t0Ticks + TimeSpan.FromMilliseconds(offsetMs).Ticks;
        for (int j = i + 1; j < snaps.Count; j++)  // REGRA: j > i
        {
            if (snaps[j].Timestamp >= alvo)
                return snaps[j].Price - p0;
        }
        return 0; // horizonte além do fim do pregão
    }

    /// <summary>Converte ticks para milissegundos inteiros.</summary>
    private static int Ms(long ticks)
        => (int)TimeSpan.FromTicks(ticks).TotalMilliseconds;

    private static double DesvioPadrao(List<double> valores)
    {
        if (valores.Count < 2) return 0;
        double media  = valores.Average();
        double somaSq = valores.Sum(v => (v - media) * (v - media));
        return Math.Sqrt(somaSq / valores.Count);
    }

    private static double Assimetria(List<double> valores)
    {
        if (valores.Count < 3) return 0;
        double media = valores.Average();
        double sigma = DesvioPadrao(valores);
        if (sigma < 1e-10) return 0;
        double n = valores.Count;
        double soma = valores.Sum(v => Math.Pow((v - media) / sigma, 3));
        return n / ((n - 1) * (n - 2)) * soma;
    }
}
