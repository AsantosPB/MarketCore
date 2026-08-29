namespace MarketCore.HistoricalImporter;

internal static class DllDateTime
{
    /// <summary>Data DLL <c>YYYYMMDD</c> + hora <c>HHmmssmmm</c> (9 dígitos, ms no fim).</summary>
    public static DateTime ParseTradeTimestamp(string yyyymmdd, string hhmmssmmm)
    {
        if (string.IsNullOrWhiteSpace(yyyymmdd) || yyyymmdd.Length < 8)
            throw new FormatException($"Data inválida: '{yyyymmdd}'");

        int y = int.Parse(yyyymmdd.AsSpan(0, 4));
        int mo = int.Parse(yyyymmdd.AsSpan(4, 2));
        int d = int.Parse(yyyymmdd.AsSpan(6, 2));

        string t = (hhmmssmmm ?? "").Trim();
        if (t.Length == 0) t = "000000000";
        t = t.PadLeft(9, '0');
        if (t.Length > 9) t = t[^9..];

        int h = int.Parse(t[..2]);
        int mi = int.Parse(t.Substring(2, 2));
        int sec = int.Parse(t.Substring(4, 2));
        int ms = int.Parse(t.Substring(6, 3));

        return new DateTime(y, mo, d, h, mi, sec, ms, DateTimeKind.Unspecified);
    }

    public static DateTime ParseSessionDate(string yyyymmdd)
    {
        if (string.IsNullOrWhiteSpace(yyyymmdd) || yyyymmdd.Length < 8)
            return DateTime.MinValue.Date;
        int y = int.Parse(yyyymmdd.AsSpan(0, 4));
        int mo = int.Parse(yyyymmdd.AsSpan(4, 2));
        int d = int.Parse(yyyymmdd.AsSpan(6, 2));
        return new DateTime(y, mo, d);
    }
}
