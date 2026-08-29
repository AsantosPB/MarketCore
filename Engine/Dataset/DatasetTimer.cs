using System;
using System.Threading;
using System.Threading.Tasks;

namespace MarketCore.Engine.Dataset;

/// <summary>
/// Dispara o DatasetBuilder automaticamente às 18h05 após o fechamento do pregão.
/// Verifica o horário a cada minuto; evita duplicação por dia.
/// </summary>
public sealed class DatasetTimer : IDisposable
{
    private readonly DatasetBuilder _builder;
    private Timer?                  _timer;
    private bool                    _rodouHoje;
    private DateTime                _ultimaData = DateTime.MinValue;
    private bool                    _disposed;

    public event Action<DatasetStats>? OnDatasetPronto;

    public DatasetTimer(DatasetBuilder builder)
        => _builder = builder;

    // ── Ciclo de vida ─────────────────────────────────────────────────────

    /// <summary>Inicia o timer de verificação (verifica a cada 60 s).</summary>
    public void Iniciar()
    {
        _timer = new Timer(HandleTimer, null,
            TimeSpan.Zero, TimeSpan.FromSeconds(60));
    }

    /// <summary>Para e descarta o timer interno.</summary>
    public void Parar()
    {
        _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Parar();
    }

    // ── Disparo manual ────────────────────────────────────────────────────

    /// <summary>
    /// Dispara o DatasetBuilder imediatamente para a data informada.
    /// Útil para testes ou recuperação de um pregão anterior.
    /// </summary>
    public async Task DispararManualAsync(DateTime date)
    {
        try
        {
            Console.WriteLine($"[DatasetTimer] Disparo manual: {date:yyyy-MM-dd}...");
            var stats = await _builder.BuildAsync(date);
            OnDatasetPronto?.Invoke(stats);
            Console.WriteLine(
                $"[DatasetTimer] Manual concluído: {stats.LabeledSnapshots} rotulados. " +
                $"Up={stats.UpMoves} Down={stats.DownMoves} Neutral={stats.Neutral}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DatasetTimer] Erro manual: {ex.Message}");
        }
    }

    // ── Timer interno ─────────────────────────────────────────────────────

    private void HandleTimer(object? state)
    {
        if (_disposed) return;
        try
        {
            var agora = DateTime.Now;

            // 18:04 a 18:06 — janela de 2 min centrada nas 18:05
            int t = agora.Hour * 60 + agora.Minute;
            if (t < 18 * 60 + 4 || t > 18 * 60 + 6) return;

            // Evita disparar mais de uma vez por dia
            if (_rodouHoje && _ultimaData.Date == agora.Date) return;

            _rodouHoje  = true;
            _ultimaData = agora;
            _ = DispararInternoAsync(agora.Date);
        }
        catch { }
    }

    private async Task DispararInternoAsync(DateTime date)
    {
        try
        {
            Console.WriteLine($"[DatasetTimer] Iniciando dataset automático de {date:yyyy-MM-dd}...");
            var stats = await _builder.BuildAsync(date);
            OnDatasetPronto?.Invoke(stats);
            Console.WriteLine(
                $"[DatasetTimer] Dataset pronto: {stats.LabeledSnapshots} snapshots rotulados. " +
                $"Up={stats.UpMoves} Down={stats.DownMoves} Neutral={stats.Neutral} " +
                $"AvgReturn1s={stats.AvgReturn1s:F2} pts");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DatasetTimer] Erro automático: {ex.Message}");
        }
    }
}
