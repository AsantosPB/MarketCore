using MarketCore.Models;

namespace MarketCore.Engine.Detectors;

public sealed class IcebergDetector
{
    public event Action<IcebergEvent>? OnIcebergDetected;

    private readonly int    _minVolume;
    private readonly int    _maxLevelGap;
    private readonly double _similarityPct;
    private readonly int    _maxHistory;

    private readonly Dictionary<string, BigLevelEntry> _activeLevels = new();
    private readonly LinkedList<IcebergEvent> _history = new();

    public IReadOnlyCollection<IcebergEvent> History => _history;

    public IcebergDetector(
     int    minVolume     = 200,
     int    maxLevelGap   = 10,
     double similarityPct = 0.75,
     int    maxHistory    = 500)
    {
        _minVolume     = minVolume;
        _maxLevelGap   = maxLevelGap;
        _similarityPct = similarityPct;
        _maxHistory    = maxHistory;
    }

    public void ProcessSnapshot(BookSnapshot snapshot)
    {
        ProcessSide(snapshot, snapshot.Bids, BookSide.Bid);
        ProcessSide(snapshot, snapshot.Asks, BookSide.Ask);
    }

    private void ProcessSide(BookSnapshot snap, IReadOnlyList<BookLevel> levels, BookSide side)
    {
        foreach (var level in levels)
        {
            if (level.Volume < _minVolume) continue;

            var key = $"{snap.Ticker}_{side}_{level.Broker}";

            if (_activeLevels.TryGetValue(key, out var prev))
            {
                var gap        = Math.Abs(level.Price - prev.Price);
                var volRatio   = (double)Math.Min(level.Volume, prev.Volume) /
                                          Math.Max(level.Volume, prev.Volume);
                var priceChanged = level.Price != prev.Price;

                if (priceChanged && gap <= _maxLevelGap && volRatio >= _similarityPct)
                {
                    var direction = side == BookSide.Bid
                        ? (level.Price > prev.Price ? "subindo" : "descendo")
                        : (level.Price < prev.Price ? "descendo" : "subindo");

                    var evt = new IcebergEvent(
                        Time:      DateTime.Now,
                        Ticker:    snap.Ticker,
                        Side:      side == BookSide.Bid ? "COMPRA" : "VENDA",
                        Broker:    level.Broker,
                        FromPrice: prev.Price,
                        ToPrice:   level.Price,
                        Volume:    level.Volume,
                        Direction: direction
                    );

                    AddHistory(evt);
                    OnIcebergDetected?.Invoke(evt);
                }
            }

            _activeLevels[key] = new BigLevelEntry(level.Price, level.Volume);
        }
    }

    public void Clear() { _activeLevels.Clear(); _history.Clear(); }

    private void AddHistory(IcebergEvent evt)
    {
        _history.AddFirst(evt);
        if (_history.Count > _maxHistory)
            _history.RemoveLast();
    }

    private record BigLevelEntry(decimal Price, int Volume);
}