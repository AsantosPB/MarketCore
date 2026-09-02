using System;
using System.Threading;
using MarketCore.Engine.Storage;

namespace MarketCore.Engine.Features;

/// <summary>
/// Timer de 100 ms que aciona o cálculo de snapshot e persiste o resultado no DuckDB.
/// Usa fire-and-forget com try/catch para garantir que erros de gravação
/// nunca derrubem o timer nem bloqueiem o caminho de cálculo.
/// </summary>
public sealed class SnapshotTimer : IDisposable
{
    private readonly FeatureEngine  _featureEngine;
    private readonly StorageManager? _storage;  // [FASE 16] nullable — lite mode

    private Timer? _timer;
    private long   _snapshotCount;
    private bool   _disposed;

    public long SnapshotCount => Interlocked.Read(ref _snapshotCount);

    public SnapshotTimer(FeatureEngine featureEngine, StorageManager? storage = null)  // [FASE 16]
    {
        _featureEngine = featureEngine ?? throw new ArgumentNullException(nameof(featureEngine));
        _storage       = storage;
    }

    // ── Ciclo de vida ─────────────────────────────────────────────────────

    /// <summary>Inicia o timer de 100 ms. Idempotente.</summary>
    public void Iniciar()
    {
        Parar(); // descarta timer anterior se houver
        _timer = new Timer(HandleTimer, state: null,
                           dueTime: TimeSpan.FromMilliseconds(100),
                           period:  TimeSpan.FromMilliseconds(100));
    }

    /// <summary>Para o timer sem descartá-lo.</summary>
    public void Parar()
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _timer?.Dispose();
        _timer = null;
    }

    // ── Handler do timer ──────────────────────────────────────────────────

    private void HandleTimer(object? state)
    {
        if (_disposed) return;

        try
        {
            // 1. Calcula snapshot e dispara OnSnapshot no FeatureEngine.
            var snap = _featureEngine.TriggerSnapshot();

            // 2. Grava no DuckDB apenas se storage disponível (fire-and-forget). [FASE 16]
            if (_storage != null)
                _ = _storage.GravarSnapshotAsync(snap.ToMarketSnapshot())
                             .ContinueWith(t =>
                             {
                                 if (t.IsFaulted)
                                     _ = t.Exception;
                             });

            // 3. Contagem de snapshots gerados.
            Interlocked.Increment(ref _snapshotCount);
        }
        catch
        {
            // Qualquer erro no cálculo é absorvido — o timer continua.
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Parar();
    }
}
