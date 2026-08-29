using System;
using System.Linq;
using MarketCore.Engine.Detectors;
using MarketCore.Models;

namespace MarketCore.Engine.Features;

/// <summary>
/// Motor principal de cálculo de features de microestrutura.
/// Recebe eventos brutos de trade e book e mantém o estado de todas as features em memória.
/// Nenhuma operação de I/O no caminho de cálculo — 100 % em RAM.
/// </summary>
public class FeatureEngine
{
    // ── Evento ────────────────────────────────────────────────────────────
    /// <summary>Disparado a cada snapshot calculado (pelo SnapshotTimer a cada 100 ms).</summary>
    public event Action<FeatureSnapshot>? OnSnapshot;
    public event Action<MarketEvent>?    OnMarketEvent;   // [FASE 6]
    public event Action<RegimeState>?    OnRegimeChange;  // [FASE 6]

    // ── Regime atual ─────────────────────────────────────────────────────
    public RegimeState? RegimeAtual => _regimeDetector?.Estado;  // [FASE 6]

    // ── Ring buffers ─────────────────────────────────────────────────────
    // Capacidade: ~60 s de dados em cenário de alta frequência do WINFUT.
    private RingBuffer<TradeEvent>    _trades    = null!;  // 60 s de trades
    private RingBuffer<BookSnapshot>  _bookSnaps = null!;  // 60 s de book (20 Hz)
    private RingBuffer<PricePoint>    _prices    = null!;  // 60 s de preços

    // ── Estado escalar (protegido por _stateLock) ─────────────────────────
    private readonly object _stateLock = new();

    // ── Detectores (Fase 6) ──────────────────────────────────────────────
    private EventDetector?  _eventDetector;   // [FASE 6]
    private RegimeDetector? _regimeDetector;  // [FASE 6]

    private double _lastPrice;
    private double _lastBid;
    private double _lastAsk;
    private double _vwap;
    private double _sessionHigh;
    private double _sessionLow  = double.MaxValue;
    private double _lastVelocity;
    private double _lastMicroprice;
    private long   _aggBuyTotal;
    private long   _aggSellTotal;
    private double _cumulativeVwapNum;  // Σ (price × volume)
    private long   _cumulativeVolume;

    // ── Ponto de preço com timestamp ─────────────────────────────────────
    private record struct PricePoint(DateTime Time, double Price);

    // ── Inicialização ─────────────────────────────────────────────────────

    public void Inicializar()
    {
        _trades    = new RingBuffer<TradeEvent>(10_000);
        _bookSnaps = new RingBuffer<BookSnapshot>(3_600);
        _prices    = new RingBuffer<PricePoint>(10_000);

        // [FASE 6] Detectores de evento e regime
        _eventDetector  = new EventDetector();
        _regimeDetector = new RegimeDetector();
        _eventDetector.OnEvent         += ev => OnMarketEvent?.Invoke(ev);
        _regimeDetector.OnRegimeChange += rs => OnRegimeChange?.Invoke(rs);

        ResetarSessao();
    }

    public void ResetarSessao()
    {
        lock (_stateLock)
        {
            _lastPrice         = 0;
            _lastBid           = 0;
            _lastAsk           = 0;
            _vwap              = 0;
            _sessionHigh       = 0;
            _sessionLow        = double.MaxValue;
            _lastVelocity      = 0;
            _lastMicroprice    = 0;
            _aggBuyTotal       = 0;
            _aggSellTotal      = 0;
            _cumulativeVwapNum = 0;
            _cumulativeVolume  = 0;
        }
    }

    // ── Ingestão de eventos ───────────────────────────────────────────────

    /// <summary>Chamado a cada novo trade — atualiza features de flow.</summary>
    public void OnTrade(TradeEvent trade)
    {
        _trades.Push(trade);
        var price = (double)trade.Price;
        _prices.Push(new PricePoint(trade.Time, price));

        lock (_stateLock)
        {
            _lastPrice = price;
            if (price > _sessionHigh) _sessionHigh = price;
            if (price < _sessionLow)  _sessionLow  = price;

            _cumulativeVwapNum += price * trade.Volume;
            _cumulativeVolume  += trade.Volume;
            _vwap = _cumulativeVolume > 0
                        ? _cumulativeVwapNum / _cumulativeVolume
                        : price;

            if (trade.Aggressor == TradeAggressor.Buy)
                _aggBuyTotal  += trade.Volume;
            else if (trade.Aggressor == TradeAggressor.Sell)
                _aggSellTotal += trade.Volume;
        }
    }

    /// <summary>Chamado a cada novo book snapshot — atualiza features de book.</summary>
    public void OnBook(BookSnapshot book)
    {
        _bookSnaps.Push(book);
        lock (_stateLock)
        {
            if (book.Bids.Count > 0) _lastBid = (double)book.Bids[0].Price;
            if (book.Asks.Count > 0) _lastAsk = (double)book.Asks[0].Price;
        }
    }

    // ── Snapshot ──────────────────────────────────────────────────────────

    /// <summary>
    /// Calcula e retorna o snapshot completo de features no momento atual.
    /// Thread-safe. Chamado pelo SnapshotTimer a cada 100 ms.
    /// </summary>
    public FeatureSnapshot CalcularSnapshot()
    {
        // Captura estado escalar atomicamente para não manter o lock durante o LINQ.
        double price, bid, ask, vwap, sessionHigh, sessionLow;
        long aggBuy, aggSell;
        lock (_stateLock)
        {
            price       = _lastPrice;
            bid         = _lastBid;
            ask         = _lastAsk;
            vwap        = _vwap;
            sessionHigh = _sessionHigh;
            sessionLow  = _sessionLow;
            aggBuy      = _aggBuyTotal;
            aggSell     = _aggSellTotal;
        }

        if (price == 0) return new FeatureSnapshot
        {
            Timestamp   = DateTime.Now.Ticks,
            SessionDate = DateTime.Today.ToString("yyyy-MM-dd"),
            TimeWindow  = CalcularTimeWindow(),
            Regime      = "INDEFINIDO",
        };

        var lastBook = _bookSnaps.GetLast(1).FirstOrDefault();

        double bookImbalance = lastBook != null ? CalcularBookImbalance(lastBook) : 0;
        double microprice    = lastBook != null ? CalcularMicroprice(lastBook) : price;
        double bidDepth      = lastBook?.Bids.Count > 0 ? lastBook.Bids[0].Volume : 0;
        double askDepth      = lastBook?.Asks.Count > 0 ? lastBook.Asks[0].Volume : 0;
        double depthImb      = (bidDepth + askDepth) > 0
                                   ? (bidDepth - askDepth) / (bidDepth + askDepth)
                                   : 0;

        double mpDelta;
        lock (_stateLock)
        {
            mpDelta         = microprice - _lastMicroprice;
            _lastMicroprice = microprice;
        }

        double velocity     = CalcularVelocity();
        double acceleration;
        lock (_stateLock)
        {
            acceleration  = velocity - _lastVelocity;
            _lastVelocity = velocity;
        }

        long   aggrTotal = aggBuy + aggSell;
        double aggrRatio = aggrTotal > 0 ? (double)aggBuy / aggrTotal : 0.5;

        var snap = new FeatureSnapshot
        {
            Timestamp        = DateTime.Now.Ticks,
            SessionDate      = DateTime.Today.ToString("yyyy-MM-dd"),
            Price            = price,
            Bid              = bid,
            Ask              = ask,
            Spread           = ask > 0 && bid > 0 ? ask - bid : 0,
            BookImbalance    = bookImbalance,
            Microprice       = microprice,
            MicropriceDelta  = mpDelta,
            BidDepth         = bidDepth,
            AskDepth         = askDepth,
            DepthImbalance   = depthImb,
            StackingScore    = CalcularStacking(),
            PullingScore     = CalcularPulling(),
            Delta100ms       = CalcularDelta(100),
            Delta500ms       = CalcularDelta(500),
            Delta1s          = CalcularDelta(1000),
            Delta2s          = CalcularDelta(2000),
            Delta5s          = CalcularDelta(5000),
            Ofi100ms         = CalcularOfi(100),
            Ofi500ms         = CalcularOfi(500),
            Ofi1s            = CalcularOfi(1000),
            TradeRate        = CalcularTradeRate(),
            VolumeRate       = CalcularVolumeRate(),
            AggressionRatio  = aggrRatio,
            Velocity         = velocity,
            Acceleration     = acceleration,
            Volatility30s    = CalcularVolatility30s(),
            Vwap             = vwap,
            DistanceVwap     = price - vwap,
            DistanceHigh     = sessionHigh > 0 ? sessionHigh - price : 0,
            DistanceLow      = sessionLow < double.MaxValue ? price - sessionLow : 0,
            AbsorptionScore  = CalcularAbsorption(),
            Regime           = "INDEFINIDO",   // preenchido pelo Regime Detector (Fase 6)
            TimeWindow       = CalcularTimeWindow(),
            HasEconomicEvent = false,           // preenchido pelo MarketEngine com CalendarLoader
            EventImpact      = 0,
        };

        // [FASE 6] Detectores de evento e regime
        if (_eventDetector != null && _regimeDetector != null)
        {
            var eventos = _eventDetector.Avaliar(snap);
            var regime  = _regimeDetector.Avaliar(snap);
            snap.Regime     = regime.Regime.ToString();
            snap.Confidence = regime.Confidence;
            foreach (var ev in eventos)
                OnMarketEvent?.Invoke(ev);
        }

        return snap;
    }

    /// <summary>Calcula snapshot, dispara OnSnapshot e retorna o resultado (chamado pelo SnapshotTimer).</summary>
    internal FeatureSnapshot TriggerSnapshot()
    {
        var snap = CalcularSnapshot();
        OnSnapshot?.Invoke(snap);
        return snap;
    }

    // ── Cálculos internos — Book ──────────────────────────────────────────

    /// <summary>(bidVol1 - askVol1) / (bidVol1 + askVol1) → -1 a +1</summary>
    private static double CalcularBookImbalance(BookSnapshot book)
    {
        if (book.Bids.Count == 0 || book.Asks.Count == 0) return 0;
        double bv = book.Bids[0].Volume;
        double av = book.Asks[0].Volume;
        double total = bv + av;
        return total > 0 ? (bv - av) / total : 0;
    }

    /// <summary>Microprice = (bestAsk × bidVol1 + bestBid × askVol1) / (bidVol1 + askVol1)</summary>
    private static double CalcularMicroprice(BookSnapshot book)
    {
        if (book.Bids.Count == 0 || book.Asks.Count == 0) return 0;
        double bestBid = (double)book.Bids[0].Price;
        double bestAsk = (double)book.Asks[0].Price;
        double bv      = book.Bids[0].Volume;
        double av      = book.Asks[0].Volume;
        double total   = bv + av;
        return total > 0 ? (bestAsk * bv + bestBid * av) / total : (bestBid + bestAsk) / 2;
    }

    /// <summary>Stacking: acúmulo de volume no melhor bid. +100 = máximo acúmulo.</summary>
    private double CalcularStacking()
    {
        var snaps = _bookSnaps.GetAll();
        if (snaps.Length < 2) return 0;
        var curr = snaps[^1];
        var prev = snaps[^2];
        if (curr.Bids.Count == 0 || prev.Bids.Count == 0) return 0;
        double delta = curr.Bids[0].Volume - prev.Bids[0].Volume;
        return Math.Max(-100, Math.Min(100, delta * 5.0));
    }

    /// <summary>Pulling: remoção de volume do melhor bid. +100 = máxima retirada.</summary>
    private double CalcularPulling()
    {
        var snaps = _bookSnaps.GetAll();
        if (snaps.Length < 2) return 0;
        var curr = snaps[^1];
        var prev = snaps[^2];
        if (curr.Bids.Count == 0 || prev.Bids.Count == 0) return 0;
        double delta = prev.Bids[0].Volume - curr.Bids[0].Volume; // positivo = volume retirado
        return Math.Max(-100, Math.Min(100, delta * 5.0));
    }

    // ── Cálculos internos — Flow ──────────────────────────────────────────

    /// <summary>Agressão líquida (compra - venda) nos últimos X milissegundos.</summary>
    private long CalcularDelta(int milliseconds)
    {
        var cutoff = DateTime.Now.AddMilliseconds(-milliseconds);
        var trades = _trades.GetAll().Where(t => t.Time >= cutoff).ToArray();
        long buy  = trades.Where(t => t.Aggressor == TradeAggressor.Buy).Sum(t => (long)t.Volume);
        long sell = trades.Where(t => t.Aggressor == TradeAggressor.Sell).Sum(t => (long)t.Volume);
        return buy - sell;
    }

    /// <summary>Order Flow Imbalance normalizado → -1 a +1 (0 = equilíbrio).</summary>
    private double CalcularOfi(int milliseconds)
    {
        var cutoff = DateTime.Now.AddMilliseconds(-milliseconds);
        var trades = _trades.GetAll().Where(t => t.Time >= cutoff).ToArray();
        if (trades.Length == 0) return 0;
        long buy   = trades.Where(t => t.Aggressor == TradeAggressor.Buy).Sum(t => (long)t.Volume);
        long sell  = trades.Where(t => t.Aggressor == TradeAggressor.Sell).Sum(t => (long)t.Volume);
        long total = buy + sell;
        return total > 0 ? (double)(buy - sell) / total : 0;
    }

    /// <summary>Negócios por segundo nos últimos 1 s.</summary>
    private double CalcularTradeRate()
    {
        var cutoff = DateTime.Now.AddSeconds(-1);
        return _trades.GetAll().Count(t => t.Time >= cutoff);
    }

    /// <summary>Contratos por segundo nos últimos 1 s.</summary>
    private double CalcularVolumeRate()
    {
        var cutoff = DateTime.Now.AddSeconds(-1);
        return _trades.GetAll().Where(t => t.Time >= cutoff).Sum(t => (long)t.Volume);
    }

    // ── Cálculos internos — Aceleração ────────────────────────────────────

    /// <summary>Variação de preço por segundo nos últimos 5 s (positivo = alta).</summary>
    private double CalcularVelocity()
    {
        var cutoff = DateTime.Now.AddSeconds(-5);
        var pts    = _prices.GetAll().Where(p => p.Time >= cutoff).ToArray();
        if (pts.Length < 2) return 0;
        return (pts[^1].Price - pts[0].Price) / 5.0;  // newest - oldest, ordenado do mais antigo
    }

    /// <summary>Desvio padrão dos retornos dos últimos 30 s.</summary>
    private double CalcularVolatility30s()
    {
        var cutoff = DateTime.Now.AddSeconds(-30);
        var pts    = _prices.GetAll().Where(p => p.Time >= cutoff).ToArray();
        if (pts.Length < 2) return 0;

        var returns = new double[pts.Length - 1];
        for (int i = 1; i < pts.Length; i++)
        {
            if (pts[i - 1].Price > 0)
                returns[i - 1] = (pts[i].Price - pts[i - 1].Price) / pts[i - 1].Price;
        }

        double mean  = returns.Average();
        double sumSq = returns.Sum(r => (r - mean) * (r - mean));
        return Math.Sqrt(sumSq / returns.Length);
    }

    // ── Cálculos internos — Absorção ──────────────────────────────────────

    /// <summary>
    /// Score de absorção: alta agressão + preço não move.
    /// Negativo = absorção vendedora. Positivo = absorção compradora. Range: -100..+100.
    /// </summary>
    private double CalcularAbsorption()
    {
        long delta1s = CalcularDelta(1000);
        if (delta1s == 0) return 0;

        var cutoff = DateTime.Now.AddSeconds(-1);
        var pts    = _prices.GetAll().Where(p => p.Time >= cutoff).ToArray();
        double priceMove = pts.Length >= 2 ? pts[^1].Price - pts[0].Price : 0;

        const long   aggrThreshold  = 20;    // contratos mínimos para considerar agressão
        const double priceThreshold = 5.0;   // pontos máximos de movimento (1 tick WINFUT)

        if (delta1s > aggrThreshold && priceMove < priceThreshold)
        {
            // Compradores agressivos, preço não subiu → absorção vendedora
            double intensity = Math.Min((double)delta1s / 100.0, 1.0);
            return -intensity * 100.0;
        }

        if (-delta1s > aggrThreshold && priceMove > -priceThreshold)
        {
            // Vendedores agressivos, preço não caiu → absorção compradora
            double intensity = Math.Min((double)-delta1s / 100.0, 1.0);
            return intensity * 100.0;
        }

        return 0;
    }

    // ── Contexto temporal ─────────────────────────────────────────────────

    /// <summary>
    /// Janela temporal baseada no horário atual.
    /// Referência: horário de verão USA (março a novembro) — NYSE abre 10h30.
    /// </summary>
    private static string CalcularTimeWindow()
    {
        var now = DateTime.Now;
        int t   = now.Hour * 60 + now.Minute;

        return t switch
        {
            >= 9 * 60 + 0  and < 9 * 60 + 5   => "Leilao",
            >= 9 * 60 + 5  and < 10 * 60 + 30  => "Abertura",
            >= 10 * 60 + 30 and < 11 * 60 + 0  => "AberturaUSA",
            >= 11 * 60 + 0 and < 13 * 60 + 0   => "Manha",
            >= 13 * 60 + 0 and < 14 * 60 + 30  => "Almoco",
            >= 14 * 60 + 30 and < 15 * 60 + 30 => "DadosUSA",
            >= 15 * 60 + 30 and < 17 * 60 + 0  => "Tarde",
            >= 17 * 60 + 0 and < 17 * 60 + 55  => "PreFechamento",
            >= 17 * 60 + 55 and < 18 * 60 + 0  => "Leilao",
            _ => "ForaPregao",
        };
    }
}
