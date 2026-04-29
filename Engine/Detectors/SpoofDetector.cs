using MarketCore.Models;

namespace MarketCore.Engine.Detectors;

public sealed class SpoofDetector
{
    public event Action<SpoofEvent>? OnSpoofDetected;

    private readonly int _minVolume;
    private readonly int _maxHistory;

    private readonly Dictionary<string, LevelSnapshot> _prevLevels = new();
    private readonly LinkedList<SpoofEvent> _history = new();
    private readonly Dictionary<decimal, SpoofMarker> _activeMarkers = new();

    public IReadOnlyCollection<SpoofEvent> History => _history;

    public SpoofDetector(
        int minVolume  = 200,
        int maxHistory = 500)
    {
        _minVolume  = minVolume;
        _maxHistory = maxHistory;
    }

    public void ProcessLevel(BookLevel current, decimal lastPrice)
    {
        var key = $"{current.Ticker}_{current.Side}_{current.Price}";

        if (_prevLevels.TryGetValue(key, out var prev))
        {
            // ═══ REGRA DO SPOOF ═══
            // 1. O nível anterior tinha volume >= mínimo (ordem grande)
            // 2. O nível atual ZEROU completamente (cancelamento total)
            if (prev.Volume >= _minVolume && current.Volume == 0)
            {
                var evt = new SpoofEvent(
                    Time:          DateTime.Now,
                    Ticker:        current.Ticker,
                    Side:          current.Side == BookSide.Bid ? "COMPRA" : "VENDA",
                    Broker:        prev.Broker,
                    Price:         current.Price,
                    VolumeBefore:  prev.Volume,
                    VolumeAfter:   0,
                    PriceDistance: Math.Abs(current.Price - lastPrice)
                );

                AddHistory(evt);
                OnSpoofDetected?.Invoke(evt);

                _activeMarkers[current.Price] = new SpoofMarker
                {
                    Side       = current.Side,
                    Broker     = prev.Broker,
                    DetectedAt = DateTime.Now
                };
            }
        }

        // Só salva no histórico se o nível tem volume > 0
        // Se zerou, remove do histórico para não re-detectar
        if (current.Volume > 0)
            _prevLevels[key] = new LevelSnapshot(current.Volume, current.Broker);
        else
            _prevLevels.Remove(key);
    }

    public bool HasSpoofMarker(decimal price, BookSide side)
    {
        if (_activeMarkers.TryGetValue(price, out var marker))
        {
            if ((DateTime.Now - marker.DetectedAt).TotalSeconds > 30)
            {
                _activeMarkers.Remove(price);
                return false;
            }
            return marker.Side == side;
        }
        return false;
    }

    public void Clear()
    {
        _prevLevels.Clear();
        _history.Clear();
        _activeMarkers.Clear();
    }

    private void AddHistory(SpoofEvent evt)
    {
        _history.AddFirst(evt);
        if (_history.Count > _maxHistory)
            _history.RemoveLast();
    }

    private record LevelSnapshot(int Volume, string Broker);

    private class SpoofMarker
    {
        public BookSide Side       { get; set; }
        public string   Broker     { get; set; } = "";
        public DateTime DetectedAt { get; set; }
    }
}
