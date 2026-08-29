using System;
using System.Threading;
using System.Threading.Tasks;

namespace MarketCore.Engine.Calendar;

/// <summary>
/// Gerencia dois timers do calendário econômico:
///   1. Carga automática às 08:30 (horário de Brasília) — dispara CalendarLoader.CarregarAsync
///   2. Loop de monitoramento a cada 30 s — chama CalendarLoader.VerificarBloqueios
///
/// Instanciar APÓS criar o CalendarLoader; chamar Iniciar() em ConnectAsync
/// e Parar()/Dispose() em DisconnectAsync / Dispose do engine.
/// </summary>
public sealed class CalendarTimer : IDisposable
{
    private readonly CalendarLoader _loader;

    private Timer? _timerCarga;
    private Timer? _timerMonitor;

    private bool _disposed;

    public CalendarTimer(CalendarLoader loader)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
    }

    // ── Ciclo de vida ─────────────────────────────────────────────────────

    /// <summary>
    /// Agenda os dois timers. Deve ser chamado em ConnectAsync, após o loader
    /// estar configurado. Idempotente se chamado mais de uma vez (descarta o anterior).
    /// </summary>
    public void Iniciar()
    {
        Parar(); // garante que não há timer órfão em re-init

        // ── Timer de monitoramento (30 s) ──────────────────────────────
        // Cada disparo verifica se algum evento está próximo ou iniciou/encerrou
        // e aciona os eventos correspondentes no CalendarLoader.
        _timerMonitor = new Timer(
            _ => _loader.VerificarBloqueios(DateTime.Now),
            state: null,
            dueTime:  TimeSpan.FromSeconds(30),
            period:   TimeSpan.FromSeconds(30));

        // ── Timer de carga automática (08:30, repetição diária) ────────
        var agora     = DateTime.Now;
        var cargaHoje = agora.Date.AddHours(8).AddMinutes(30);

        // Se as 08:30 de hoje já passaram, agenda para amanhã.
        var delay = cargaHoje > agora
                        ? cargaHoje - agora
                        : cargaHoje.AddDays(1) - agora;

        _timerCarga = new Timer(
            _ => _ = CarregarCalendarioAsync(),
            state: null,
            dueTime: delay,
            period:  TimeSpan.FromHours(24));
    }

    /// <summary>
    /// Para os dois timers sem descartá-los (mantém a referência para re-uso).
    /// Chame em FinalizarPregaoAsync ou antes de Dispose.
    /// </summary>
    public void Parar()
    {
        _timerCarga?.Change(Timeout.Infinite, Timeout.Infinite);
        _timerMonitor?.Change(Timeout.Infinite, Timeout.Infinite);
        _timerCarga?.Dispose();
        _timerMonitor?.Dispose();
        _timerCarga   = null;
        _timerMonitor = null;
    }

    // ── Carga assíncrona ──────────────────────────────────────────────────

    private async Task CarregarCalendarioAsync()
    {
        try
        {
            await _loader.CarregarAsync(DateTime.Today);
        }
        catch
        {
            // Erros são silenciados aqui; o CalendarLoader já os propaga
            // via evento OnCalendarLoaded (ou simplesmente retorna um dia vazio).
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
