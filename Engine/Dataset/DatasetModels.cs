using System;
using MarketCore.Engine.Storage;

namespace MarketCore.Engine.Dataset;

/// <summary>Labels calculados offline pelo DatasetBuilder após o fechamento do pregão.</summary>
public class LabelRecord
{
    public long   Timestamp         { get; set; }  // T0 (DateTime.Ticks)

    // ── Retornos futuros em múltiplos horizontes ──────────────────────────
    public double FutureReturn100ms { get; set; }  // price(T0+100ms) - price(T0)
    public double FutureReturn250ms { get; set; }
    public double FutureReturn500ms { get; set; }
    public double FutureReturn1s    { get; set; }
    public double FutureReturn2s    { get; set; }
    public double FutureReturn5s    { get; set; }
    public double FutureReturn10s   { get; set; }

    // ── Excursões máximas na janela de 5 s ───────────────────────────────
    public double Mfe5s             { get; set; }  // max favorable excursion (positivo)
    public double Mae5s             { get; set; }  // max adverse excursion (negativo)

    // ── Tempos até alvos (ms; -1 = não atingido em 30 s) ─────────────────
    public int    TimeTo20Pts       { get; set; }  // ms até price >= T0 + 20 pts
    public int    TimeToStop        { get; set; }  // ms até price <= T0 - 15 pts
}

/// <summary>Par de features + labels para um instante T0.</summary>
public class DatasetRecord
{
    public MarketSnapshot Features { get; set; } = null!;
    public LabelRecord    Labels   { get; set; } = null!;
}

/// <summary>Estatísticas do dataset gerado para um pregão.</summary>
public class DatasetStats
{
    public DateTime Date             { get; set; }
    public int      TotalSnapshots   { get; set; }
    public int      LabeledSnapshots { get; set; }
    public double   AvgReturn1s      { get; set; }
    public double   StdReturn1s      { get; set; }
    public double   SkewnessReturn1s { get; set; }
    public int      UpMoves          { get; set; }  // FutureReturn1s > +5 pts
    public int      DownMoves        { get; set; }  // FutureReturn1s < -5 pts
    public int      Neutral          { get; set; }  // |FutureReturn1s| <= 5 pts
    public DateTime BuildTime        { get; set; }
}
