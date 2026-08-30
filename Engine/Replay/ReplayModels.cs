using System;

namespace MarketCore.Engine.Replay;

/// <summary>Velocidade de reprodução do replay.</summary>
public enum ReplaySpeed
{
    RealTime   =   1,
    X2         =   2,
    X5         =   5,
    X10        =  10,
    X100       = 100,
    MaxSpeed   =   0,   // sem throttle — velocidade máxima do CPU
    StepByStep =  -1    // avança 1 evento por vez
}

/// <summary>Estado atual do replay.</summary>
public enum ReplayStatus
{
    Idle     = 0,
    Running  = 1,
    Paused   = 2,
    Stepping = 3,
    Finished = 4,
    Error    = 5
}

/// <summary>Evento de trade lido do arquivo binário bruto _trades.bin.</summary>
public class RawTradeEvent
{
    public long    Timestamp      { get; set; }  // DateTime.Ticks
    public long    SequenceNumber { get; set; }
    public decimal Price          { get; set; }
    public int     Volume         { get; set; }
    public byte    Aggressor      { get; set; }  // cast de TradeAggressor
    public string  Broker         { get; set; } = string.Empty;
}

/// <summary>Evento de book lido do arquivo binário bruto _book.bin (272 bytes fixos).</summary>
public class RawBookEvent
{
    public long     ExchangeTimestamp { get; set; }  // DateTime.Ticks
    public long     ReceiveTimestamp  { get; set; }
    public long     SequenceNumber    { get; set; }
    public double   Price             { get; set; }  // best bid
    public double[] BidPrices         { get; set; } = new double[10];
    public int[]    BidVolumes        { get; set; } = new int[10];
    public double[] AskPrices         { get; set; } = new double[10];
    public int[]    AskVolumes        { get; set; } = new int[10];
}

/// <summary>Sessão de replay com progresso e metadata.</summary>
public class ReplaySession
{
    public Guid        SessionId       { get; set; }
    public DateTime    Date            { get; set; }
    public ReplaySpeed Speed           { get; set; }
    public ReplayStatus Status         { get; set; }
    public long        TotalEvents     { get; set; }
    public long        ProcessedEvents { get; set; }
    public DateTime    CurrentTime     { get; set; }

    public double ProgressPct
        => TotalEvents > 0
           ? (double)ProcessedEvents / TotalEvents * 100.0
           : 0.0;
}

/// <summary>Resultado ao fim de uma sessão de replay.</summary>
public class ReplayResult
{
    public ReplaySession Session              { get; set; } = null!;
    public TimeSpan      Duration            { get; set; }
    public long          TradesReplayed      { get; set; }
    public long          BookUpdatesReplayed { get; set; }
    public bool          IsDeterministic     { get; set; }
    public string        ChecksumHash        { get; set; } = string.Empty;
}
