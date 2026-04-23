using MarketCore.Models;

namespace MarketCore.Engine.Detectors;

public sealed class RenewableDetector
{
    public event Action<RenewableEvent>? OnRenewableDetected;

    private readonly int    _minVolume;
    private readonly double _dropThreshold;
    private readonly double _renewThreshold;
    private readonly int    _minRenewals;
    private readonly int    _maxHistory;
    private readonly int    _expirySeconds;

    private readonly Dictionary<string, RenewTracker> _trackers = new();
    private readonly LinkedList<RenewableEvent> _history = new();

    public IReadOnlyCollection<RenewableEvent> History => _history;

    public RenewableDetector(
        int    minVolume      = 80,
        double dropThreshold  = 0.60,
        double renewThreshold = 0.55,
        int    minRenewals    = 2,
        int    maxHistory     = 500,
        int    expirySeconds  = 60)
    {
        _minVolume      = minVolume;
        _dropThreshold  = dropThreshold;
        _renewThreshold = renewThreshold;
        _minRenewals    = minRenewals;
        _maxHistory     = maxHistory;
        _expirySeconds  = expirySeconds;
    }

    public void ProcessLevel(BookLevel current)
    {
        var key = $"{current.Ticker}_{current.Side}_{current.Price}_{current.Broker}";

        CleanExpired();

        if (!_trackers.TryGetValue(key, out var tracker))
        {
            if (current.Volume >= _minVolume)
                _trackers[key] = new RenewTracker(current.Volume, current.Volume);
            return;
        }

        var dropPct = tracker.PeakVolume > 0
            ? (double)(tracker.PeakVolume - current.Volume) / tracker.PeakVolume
            : 0;

        var renewPct = tracker.ValleyVolume > 0 && tracker.ValleyVolume < tracker.PeakVolume
            ? (double)current.Volume / tracker.PeakVolume
            : 0;

        if (dropPct >= _dropThreshold && tracker.State == TrackerState.Active)
        {
            tracker.ValleyVolume   = current.Volume;
            tracker.TotalExecuted += tracker.PeakVolume - current.Volume;
            tracker.State          = TrackerState.WaitingRenew;
        }
        else if (renewPct >= _renewThreshold && tracker.State == TrackerState.WaitingRenew)
        {
            tracker.Renewals++;
            tracker.PeakVolume   = current.Volume;
            tracker.State        = TrackerState.Active;
            tracker.LastActivity = DateTime.Now;

            if (tracker.Renewals >= _minRenewals)
            {
                var evt = new RenewableEvent(
                    Time:           DateTime.Now,
                    Ticker:         current.Ticker,
                    Side:           current.Side == BookSide.Bid ? "COMPRA" : "VENDA",
                    Broker:         current.Broker,
                    Price:          current.Price,
                    VolumePerCycle: current.Volume,
                    Renewals:       tracker.Renewals,
                    TotalExecuted:  tracker.TotalExecuted
                );
                AddHistory(evt);
                OnRenewableDetected?.Invoke(evt);
            }
        }
        else if (current.Volume > tracker.PeakVolume)
        {
            tracker.PeakVolume = current.Volume;
        }

        tracker.LastActivity = DateTime.Now;
        _trackers[key] = tracker;
    }

    public void Clear() { _trackers.Clear(); _history.Clear(); }

    private void CleanExpired()
    {
        var expiry  = DateTime.Now.AddSeconds(-_expirySeconds);
        var expired = _trackers
            .Where(kv => kv.Value.LastActivity < expiry)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var k in expired)
            _trackers.Remove(k);
    }

    private void AddHistory(RenewableEvent evt)
    {
        _history.AddFirst(evt);
        if (_history.Count > _maxHistory)
            _history.RemoveLast();
    }

    private enum TrackerState { Active, WaitingRenew }

    private class RenewTracker
    {
        public int          PeakVolume    { get; set; }
        public int          ValleyVolume  { get; set; }
        public int          TotalExecuted { get; set; }
        public int          Renewals      { get; set; }
        public TrackerState State         { get; set; } = TrackerState.Active;
        public DateTime     LastActivity  { get; set; } = DateTime.Now;

        public RenewTracker(int peak, int valley)
        {
            PeakVolume   = peak;
            ValleyVolume = valley;
        }
    }
}