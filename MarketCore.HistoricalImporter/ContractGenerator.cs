namespace MarketCore.HistoricalImporter;

/// <summary>Gera a lista de contratos WIN (vencimentos em meses pares) no intervalo solicitado.</summary>
public static class ContractGenerator
{
    /// <summary>Meses pares com vencimento WIN: Fev, Abr, Jun, Ago, Out, Dez.</summary>
    private static readonly int[] EvenExpirationMonths = [2, 4, 6, 8, 10, 12];

    /// <summary>Código mês → letra B3 (WIN).</summary>
    private static readonly IReadOnlyDictionary<int, char> MonthCode = new Dictionary<int, char>
    {
        [2] = 'G',
        [4] = 'J',
        [6] = 'M',
        [8] = 'Q',
        [10] = 'V',
        [12] = 'Z'
    };

    /// <summary>
    /// Vencimento WIN: quarta-feira mais próxima ao dia 15 do mês de vencimento.
    /// </summary>
    public static DateTime GetExpirationWednesday(int year, int month)
    {
        if (!MonthCode.ContainsKey(month))
            throw new ArgumentOutOfRangeException(nameof(month), "WIN vence apenas em meses pares (2,4,6,8,10,12).");

        var anchor = new DateTime(year, month, 15);
        DateTime best = anchor;
        int bestDist = int.MaxValue;

        for (int d = 1; d <= DateTime.DaysInMonth(year, month); d++)
        {
            var dt = new DateTime(year, month, d);
            if (dt.DayOfWeek != DayOfWeek.Wednesday) continue;
            int dist = Math.Abs((dt - anchor).Days);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = dt;
            }
        }

        return best.Date;
    }

    /// <summary>
    /// Monta o símbolo WIN (ex.: <c>WINJ26</c> para abril de 2026).
    /// </summary>
    public static string BuildWinSymbol(int year, int month)
    {
        if (!MonthCode.TryGetValue(month, out char letter))
            throw new ArgumentOutOfRangeException(nameof(month));
        int yy = year % 100;
        return $"WIN{letter}{yy:D2}";
    }

    /// <summary>
    /// Gera todos os contratos WIN cujo período <see cref="ContractPeriod.StartDate"/>–<see cref="ContractPeriod.EndDate"/>
    /// intersecta <paramref name="start"/>–<paramref name="end"/>.
    /// Cada contrato: 60 dias antes do vencimento até o vencimento (inclusive).
    /// </summary>
    public static List<ContractPeriod> GenerateWINContracts(DateTime start, DateTime end)
    {
        if (end < start)
            throw new ArgumentException("Data fim anterior à data início.");

        start = start.Date;
        end = end.Date;

        var list = new List<ContractPeriod>();

        // Margem de ano para capturar vencimentos nas bordas do intervalo
        int y0 = start.Year - 1;
        int y1 = end.Year + 1;

        for (int y = y0; y <= y1; y++)
        {
            foreach (int month in EvenExpirationMonths)
            {
                var exp = GetExpirationWednesday(y, month);
                var periodStart = exp.AddDays(-60);
                var periodEnd = exp;

                if (periodEnd < start || periodStart > end)
                    continue;

                string symbol = BuildWinSymbol(y, month);
                list.Add(new ContractPeriod(symbol, periodStart, periodEnd, exp));
            }
        }

        list.Sort((a, b) => a.ExpirationDate.CompareTo(b.ExpirationDate));
        return list;
    }
}
