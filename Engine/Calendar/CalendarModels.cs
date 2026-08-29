using System;
using System.Collections.Generic;
using System.Linq;

namespace MarketCore.Engine.Calendar;

/// <summary>Nível de impacto de um evento econômico sobre o mercado.</summary>
public enum ImpactLevel
{
    Low      = 0,   // 1 tomate — bloqueio: nenhum
    Medium   = 1,   // 2 tomates — bloqueio: 5 min antes / 2 s depois
    High     = 2,   // 3 tomates — bloqueio: 15 min antes / 3 s depois
    Critical = 3    // Payroll, CPI, FOMC, Copom — bloqueio: 30 min antes / 5 s depois
}

/// <summary>
/// Evento econômico do calendário — horário em Brasília, já com conversão DST aplicada.
/// </summary>
public class EconomicEvent
{
    public string     EventId            { get; set; } = string.Empty;
    public DateTime   TimeBrasilia       { get; set; }
    public string     Name               { get; set; } = string.Empty;
    public string     Country            { get; set; } = string.Empty;
    public ImpactLevel Impact            { get; set; }
    public double?    Forecast           { get; set; }
    public double?    Previous           { get; set; }

    /// <summary>Minutos antes do evento em que o sistema deve bloquear novas entradas.</summary>
    public int        BlockMinutesBefore { get; set; }

    /// <summary>Segundos após o evento antes de liberar novas entradas.</summary>
    public int        WaitSecondsAfter   { get; set; }

    /// <summary>true enquanto o sistema está dentro da janela de bloqueio deste evento.</summary>
    public bool       IsActive           { get; set; }

    /// <summary>Início da janela de bloqueio (TimeBrasilia - BlockMinutesBefore).</summary>
    public DateTime   BloqueioInicio     => TimeBrasilia.AddMinutes(-BlockMinutesBefore);

    /// <summary>Fim da janela de bloqueio (TimeBrasilia + WaitSecondsAfter).</summary>
    public DateTime   BloqueioFim        => TimeBrasilia.AddSeconds(WaitSecondsAfter);
}

/// <summary>
/// Calendário de um pregão — lista de eventos do dia com helpers de consulta rápida.
/// </summary>
public class CalendarDay
{
    public DateTime             Date   { get; set; }
    public List<EconomicEvent>  Events { get; set; } = new();

    public bool HasCritical => Events.Any(e => e.Impact == ImpactLevel.Critical);
    public bool HasHigh     => Events.Any(e => e.Impact >= ImpactLevel.High);
}
