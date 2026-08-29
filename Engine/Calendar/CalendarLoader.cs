using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading.Tasks;
using HtmlAgilityPack;
using MarketCore.Engine.Storage;

namespace MarketCore.Engine.Calendar;

/// <summary>
/// Carrega e interpreta o calendário econômico do dia via Investing.com.
/// Detecta horário de verão USA, converte NY → Brasília e classifica impacto.
/// Não lança exceções — falhas de rede retornam CalendarDay vazio.
/// </summary>
public sealed class CalendarLoader
{
    private readonly StorageManager? _storage;

    // ── Eventos públicos ──────────────────────────────────────────────────

    /// <summary>Disparado após carregamento bem-sucedido do calendário.</summary>
    public event Action<CalendarDay>? OnCalendarLoaded;

    /// <summary>Disparado quando um bloqueio se aproxima (minutosRestantes &lt; 5).</summary>
    public event Action<EconomicEvent, int>? OnBlockApproaching;

    /// <summary>Disparado quando um bloqueio começa (sistema entra na janela).</summary>
    public event Action<EconomicEvent>? OnBlockStart;

    /// <summary>Disparado quando um bloqueio termina (sistema sai da janela).</summary>
    public event Action<EconomicEvent>? OnBlockEnd;

    // ── Estado interno ────────────────────────────────────────────────────

    private CalendarDay? _calendarHoje;

    // ── Eventos críticos — forçam ImpactLevel.Critical independente dos tomates ──

    private static readonly HashSet<string> _keywordsCritical = new(StringComparer.OrdinalIgnoreCase)
    {
        "payroll", "nonfarm", "non-farm", "cpi", "fomc", "fed funds", "copom", "selic",
        "ppi", "gdp", "pib", "unemployment rate", "taxa de desemprego",
        "interest rate decision", "decisao de juros"
    };

    public CalendarLoader(StorageManager? storage = null)
    {
        _storage = storage;
    }

    // ── DST detection ─────────────────────────────────────────────────────

    /// <summary>
    /// Retorna true se a data está dentro do período de horário de verão USA.
    /// DST 2026: 8 março → 1 novembro.
    /// Regra geral: segunda semana de março → primeira semana de novembro.
    /// </summary>
    public static bool IsDstUsa(DateTime date)
    {
        var year = date.Year;

        // Segunda semana de março: 2.º domingo
        var marchStart = SegundoDomingo(year, 3);

        // Primeira semana de novembro: 1.º domingo
        var novEnd = PrimeiroDomingo(year, 11);

        return date.Date >= marchStart && date.Date < novEnd;
    }

    private static DateTime SegundoDomingo(int year, int month)
    {
        var primeiro = new DateTime(year, month, 1);
        int diasAteDomingo = ((int)DayOfWeek.Sunday - (int)primeiro.DayOfWeek + 7) % 7;
        return primeiro.AddDays(diasAteDomingo + 7); // +7 = segunda ocorrência
    }

    private static DateTime PrimeiroDomingo(int year, int month)
    {
        var primeiro = new DateTime(year, month, 1);
        int diasAteDomingo = ((int)DayOfWeek.Sunday - (int)primeiro.DayOfWeek + 7) % 7;
        return primeiro.AddDays(diasAteDomingo);
    }

    /// <summary>
    /// Converte horário NY (Eastern) para Brasília.
    /// Com DST: Brasília = NY + 1h. Sem DST: Brasília = NY + 2h.
    /// </summary>
    public static DateTime NyParaBrasilia(DateTime nyTime, bool isDst)
        => nyTime.AddHours(isDst ? 1 : 2);

    // ── Carregamento ──────────────────────────────────────────────────────

    /// <summary>
    /// Carrega o calendário econômico do dia.
    /// Tenta download do Investing.com; em caso de falha, tenta SQLite;
    /// em último caso retorna CalendarDay vazio.
    /// </summary>
    public async Task<CalendarDay> CarregarAsync(DateTime date)
    {
        CalendarDay? day = null;

        try
        {
            day = await BaixarDoInvestingAsync(date);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[CALENDAR] Falha no download: {ex.Message} — tentando SQLite...");
            Console.ResetColor();
        }

        // Fallback: eventos salvos anteriormente
        if ((day == null || day.Events.Count == 0) && _storage != null)
        {
            try
            {
                day = await _storage.CarregarEventosSalvosAsync(date);
                if (day != null && day.Events.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"[CALENDAR] Usando eventos salvos do SQLite para {date:yyyy-MM-dd}.");
                    Console.ResetColor();
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[CALENDAR] Falha no fallback SQLite: {ex.Message}");
                Console.ResetColor();
            }
        }

        day ??= new CalendarDay { Date = date };
        _calendarHoje = day;

        // Persistir no SQLite
        if (_storage != null && day.Events.Count > 0)
        {
            try { await _storage.SalvarEventosAsync(day); }
            catch { /* não bloquear por falha de persistência */ }
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[CALENDAR] {date:dd/MM/yyyy}: {day.Events.Count} evento(s) carregado(s). " +
                          $"Critical={day.HasCritical} High={day.HasHigh}");
        Console.ResetColor();

        OnCalendarLoaded?.Invoke(day);
        return day;
    }

    private async Task<CalendarDay> BaixarDoInvestingAsync(DateTime date)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.Add("Accept-Language", "pt-BR,pt;q=0.9,en;q=0.8");
        http.DefaultRequestHeaders.Add("Referer", "https://br.investing.com/");
        http.Timeout = TimeSpan.FromSeconds(30);

        var html = await http.GetStringAsync("https://br.investing.com/economic-calendar/");

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var day = new CalendarDay { Date = date };
        bool isDst = IsDstUsa(date);

        // Linhas da tabela de eventos
        var rows = doc.DocumentNode.SelectNodes(
            "//table[@id='economicCalendarData']//tr[contains(@class,'js-event-item')]");

        if (rows == null) return day;

        int seq = 0;
        foreach (var row in rows)
        {
            try
            {
                var ev = ParseRow(row, date, isDst, ++seq);
                if (ev != null) day.Events.Add(ev);
            }
            catch { /* pular linha malformada */ }
        }

        return day;
    }

    private static EconomicEvent? ParseRow(HtmlNode row, DateTime date, bool isDst, int seq)
    {
        // Horário
        var timeNode = row.SelectSingleNode(".//td[contains(@class,'time')]");
        if (timeNode == null) return null;

        var timeText = timeNode.InnerText.Trim();
        if (!DateTime.TryParseExact(timeText, "HH:mm",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var nyTime))
            return null;

        var nyDateTime   = date.Date.Add(nyTime.TimeOfDay);
        var timeBrasilia = NyParaBrasilia(nyDateTime, isDst);

        // Só eventos do pregão (09:00 – 18:00 Brasília)
        if (timeBrasilia.Hour < 6 || timeBrasilia.Hour > 20) return null;

        // Nome e país
        var nameNode    = row.SelectSingleNode(".//td[contains(@class,'event')]");
        var countryNode = row.SelectSingleNode(".//td[contains(@class,'flagCur')]//span");
        var name        = nameNode?.InnerText.Trim() ?? string.Empty;
        var country     = countryNode?.GetAttributeValue("title", string.Empty) ?? string.Empty;

        // Filtrar apenas eventos BR e US
        var lowerCountry = country.ToLowerInvariant();
        if (!lowerCountry.Contains("brasil") && !lowerCountry.Contains("estados unidos") &&
            !lowerCountry.Contains("united states") && !lowerCountry.Contains("brazil"))
            return null;

        // Impacto (ícones "bull" = tomates)
        var bullIcons = row.SelectNodes(".//td[contains(@class,'sentiment')]//i[contains(@class,'bull')]");
        int bullCount = bullIcons?.Count ?? 0;

        var impact = ClassificarImpacto(name, bullCount);
        var (blockMin, waitSec) = ImpactToBlock(impact);

        // Forecast / Previous
        double? forecast = null, previous = null;
        var forecastNode = row.SelectSingleNode(".//td[contains(@class,'fore')]");
        var previousNode = row.SelectSingleNode(".//td[contains(@class,'prev')]");
        if (double.TryParse(forecastNode?.InnerText.Trim().Replace(",", "."),
                NumberStyles.Any, CultureInfo.InvariantCulture, out var fv)) forecast = fv;
        if (double.TryParse(previousNode?.InnerText.Trim().Replace(",", "."),
                NumberStyles.Any, CultureInfo.InvariantCulture, out var pv)) previous = pv;

        return new EconomicEvent
        {
            EventId            = $"{date:yyyyMMdd}-{seq:D3}",
            TimeBrasilia       = timeBrasilia,
            Name               = name,
            Country            = country,
            Impact             = impact,
            Forecast           = forecast,
            Previous           = previous,
            BlockMinutesBefore = blockMin,
            WaitSecondsAfter   = waitSec,
            IsActive           = false
        };
    }

    private static ImpactLevel ClassificarImpacto(string name, int bullCount)
    {
        var lower = name.ToLowerInvariant();
        foreach (var kw in _keywordsCritical)
            if (lower.Contains(kw.ToLowerInvariant())) return ImpactLevel.Critical;

        return bullCount switch
        {
            >= 3 => ImpactLevel.High,
            2    => ImpactLevel.Medium,
            _    => ImpactLevel.Low
        };
    }

    private static (int blockMin, int waitSec) ImpactToBlock(ImpactLevel impact) => impact switch
    {
        ImpactLevel.Critical => (30, 5),
        ImpactLevel.High     => (15, 3),
        ImpactLevel.Medium   => (5,  2),
        _                    => (0,  0)
    };

    // ── Consultas em tempo real ───────────────────────────────────────────

    /// <summary>Retorna true se o momento atual está dentro da janela de bloqueio de algum evento.</summary>
    public bool EstaBloqueado(DateTime momento)
    {
        if (_calendarHoje == null) return false;
        foreach (var ev in _calendarHoje.Events)
        {
            if (ev.BlockMinutesBefore == 0 && ev.WaitSecondsAfter == 0) continue;
            if (momento >= ev.BloqueioInicio && momento <= ev.BloqueioFim)
                return true;
        }
        return false;
    }

    /// <summary>Retorna o próximo evento do dia a partir de <paramref name="agora"/>.</summary>
    public EconomicEvent? ProximoEvento(DateTime agora)
    {
        if (_calendarHoje == null) return null;
        EconomicEvent? proximo = null;
        foreach (var ev in _calendarHoje.Events)
        {
            if (ev.TimeBrasilia <= agora) continue;
            if (proximo == null || ev.TimeBrasilia < proximo.TimeBrasilia)
                proximo = ev;
        }
        return proximo;
    }

    /// <summary>
    /// Retorna minutos até o próximo início de janela de bloqueio.
    /// Retorna -1 se não há bloqueio iminente no dia.
    /// </summary>
    public int MinutosAteProximoBloqueio(DateTime agora)
    {
        if (_calendarHoje == null) return -1;
        int menor = int.MaxValue;
        foreach (var ev in _calendarHoje.Events)
        {
            if (ev.BlockMinutesBefore == 0) continue;
            var inicio = ev.BloqueioInicio;
            if (inicio <= agora) continue;
            int mins = (int)(inicio - agora).TotalMinutes;
            if (mins < menor) menor = mins;
        }
        return menor == int.MaxValue ? -1 : menor;
    }

    // ── Monitoramento (chamado pelo CalendarTimer a cada 30s) ─────────────

    internal void VerificarBloqueios(DateTime agora)
    {
        if (_calendarHoje == null) return;

        foreach (var ev in _calendarHoje.Events)
        {
            if (ev.BlockMinutesBefore == 0 && ev.WaitSecondsAfter == 0) continue;

            bool dentroJanela = agora >= ev.BloqueioInicio && agora <= ev.BloqueioFim;
            int  minsAte      = (int)(ev.BloqueioInicio - agora).TotalMinutes;

            // Aproximação (1-5 min antes do início)
            if (!ev.IsActive && minsAte > 0 && minsAte <= 5)
                OnBlockApproaching?.Invoke(ev, minsAte);

            // Início do bloqueio
            if (!ev.IsActive && dentroJanela)
            {
                ev.IsActive = true;
                OnBlockStart?.Invoke(ev);
            }

            // Fim do bloqueio
            if (ev.IsActive && !dentroJanela && agora > ev.BloqueioFim)
            {
                ev.IsActive = false;
                OnBlockEnd?.Invoke(ev);
            }
        }
    }
}
