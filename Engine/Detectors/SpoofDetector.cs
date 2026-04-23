using MarketCore.Models;

namespace MarketCore.Engine.Detectors;

public sealed class SpoofDetector
{
    public event Action<SpoofEvent>? OnSpoofDetected;

    private readonly int    _minVolume;
    private readonly int    _priceDistance;
    private readonly double _dropThreshold;
    private readonly int    _maxHistory;

    private readonly Dictionary<string, LevelSnapshot> _prevLevels = new();
    private readonly LinkedList<SpoofEvent> _history = new();

    public IReadOnlyCollection<SpoofEvent> History => _history;

    public SpoofDetector(
        int    minVolume     = 200,
        int    priceDistance = 30,
        double dropThreshold = 0.65,
        int    maxHistory    = 500)
    {
        _minVolume     = minVolume;
        _priceDistance = priceDistance;
        _dropThreshold = dropThreshold;
        _maxHistory    = maxHistory;
    }

    public void ProcessLevel(BookLevel current, decimal lastPrice)
    {
        var key = $"{current.Ticker}_{current.Side}_{current.Price}";

        if (_prevLevels.TryGetValue(key, out var prev))
        {
            if (prev.Volume >= _minVolume)
            {
                var distance = Math.Abs(current.Price - lastPrice);
                var dropped  = prev.Volume - current.Volume;
                var dropPct  = (double)dropped / prev.Volume;

                if (dropPct >= _dropThreshold && distance <= _priceDistance)
                {
                    var evt = new SpoofEvent(
                        Time:          DateTime.Now,
                        Ticker:        current.Ticker,
                        Side:          current.Side == BookSide.Bid ? "COMPRA" : "VENDA",
                        Broker:        prev.Broker,
                        Price:         current.Price,
                        VolumeBefore:  prev.Volume,
                        VolumeAfter:   current.Volume,
                        PriceDistance: distance
                    );
                    AddHistory(evt);
                    OnSpoofDetected?.Invoke(evt);
                }
            }
        }

        _prevLevels[key] = new LevelSnapshot(current.Volume, current.Broker);
    }

    public void Clear() { _prevLevels.Clear(); _history.Clear(); }

    private void AddHistory(SpoofEvent evt)
    {
        _history.AddFirst(evt);
        if (_history.Count > _maxHistory)
            _history.RemoveLast();
    }

    private record LevelSnapshot(int Volume, string Broker);
}