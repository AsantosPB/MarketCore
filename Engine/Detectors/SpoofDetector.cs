using MarketCore.Models;
namespace MarketCore.Engine.Detectors;

/// <summary>
/// Detecta ordens que ficam ENTRANDO E SAINDO repetidamente no mesmo preço.
/// 
/// LÓGICA:
/// Para cada nível de preço, conta quantas vezes o volume aparece e some.
/// Se o mesmo preço alternar entre "com volume" e "zerado" N vezes 
/// dentro de uma janela de tempo → SPOOF CÍCLICO detectado.
/// 
/// Enquanto o ciclo está ativo, o nível fica marcado com "S" no book.
/// </summary>
public sealed class SpoofDetector
{
    public event Action<SpoofEvent>? OnSpoofDetected;

    private readonly int      _cyclesRequired;  // quantos ciclos para confirmar spoof
    private readonly TimeSpan _window;          // janela de tempo para contar ciclos
    private readonly int      _maxHistory;

    // Considerar qualquer ordem com volume > 0 (filtro de volume fica na UI)
    private const int MinVolumeInternal = 1;

    // Estado por nível de preço
    private readonly Dictionary<string, CycleTracker> _trackers = new();
    private readonly Dictionary<decimal, SpoofMarker> _activeMarkers = new();
    private readonly LinkedList<SpoofEvent> _history = new();

    public IReadOnlyCollection<SpoofEvent> History => _history;

    public SpoofDetector(
        int cyclesRequired = 2,       // 2 ciclos coloca/tira para confirmar
        int windowSeconds  = 60,
        int maxHistory     = 500)
    {
        _cyclesRequired = cyclesRequired;
        _window         = TimeSpan.FromSeconds(windowSeconds);
        _maxHistory     = maxHistory;
    }

    public void ProcessLevel(BookLevel current, decimal lastPrice)
    {
        var key = $"{current.Ticker}_{current.Side}_{current.Price}";

        if (!_trackers.TryGetValue(key, out var tracker))
        {
            tracker = new CycleTracker();
            _trackers[key] = tracker;
        }

        var now = DateTime.Now;

        // Limpar ciclos antigos fora da janela de tempo
        tracker.PurgeBefore(now - _window);

        bool hadVolume   = tracker.LastVolume >= MinVolumeInternal;
        bool hasVolume   = current.Volume     >= MinVolumeInternal;

        // Detectar transição: tinha volume → zerou (RETIRADA)
        if (hadVolume && current.Volume == 0)
        {
            tracker.RecordWithdrawal(now, tracker.LastVolume, current.Broker.Length > 0 ? current.Broker : tracker.LastBroker);
        }
        // Detectar transição: não tinha → apareceu (COLOCAÇÃO)
        else if (!hadVolume && hasVolume)
        {
            tracker.RecordPlacement(now, current.Volume, current.Broker);
        }

        tracker.LastVolume = current.Volume;
        if (!string.IsNullOrEmpty(current.Broker))
            tracker.LastBroker = current.Broker;

        // Verificar se atingiu o número de ciclos
        int cycles = tracker.CountCycles();
        if (cycles >= _cyclesRequired && !tracker.AlreadyFired)
        {
            tracker.AlreadyFired = true;

            var evt = new SpoofEvent(
                Time:          now,
                Ticker:        current.Ticker,
                Side:          current.Side == BookSide.Bid ? "COMPRA" : "VENDA",
                Broker:        tracker.LastBroker,
                Price:         current.Price,
                VolumeBefore:  tracker.MaxVolumeObserved,
                VolumeAfter:   current.Volume,
                PriceDistance: Math.Abs(current.Price - lastPrice)
            );

            AddHistory(evt);
            OnSpoofDetected?.Invoke(evt);

            // Marcar como ativo no book por 60 segundos
            _activeMarkers[current.Price] = new SpoofMarker
            {
                Side       = current.Side,
                Broker     = tracker.LastBroker,
                DetectedAt = now,
                Cycles     = cycles
            };
        }

        // Re-habilitar disparo se ciclos acumularam novamente após reset
        if (cycles < _cyclesRequired && tracker.AlreadyFired)
        {
            // Reset após janela de tempo — permite re-detectar no futuro
            if (tracker.OldestCycleTime.HasValue &&
                (now - tracker.OldestCycleTime.Value) > _window)
            {
                tracker.AlreadyFired = false;
                tracker.Clear();
            }
        }
    }

    /// <summary>
    /// Retorna true se há marcador de spoof ativo para este preço/lado.
    /// Usado pelo book para exibir "S".
    /// </summary>
    public bool HasSpoofMarker(decimal price, BookSide side)
    {
        if (_activeMarkers.TryGetValue(price, out var marker))
        {
            if ((DateTime.Now - marker.DetectedAt).TotalSeconds > 60)
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
        _trackers.Clear();
        _history.Clear();
        _activeMarkers.Clear();
    }

    private void AddHistory(SpoofEvent evt)
    {
        _history.AddFirst(evt);
        if (_history.Count > _maxHistory)
            _history.RemoveLast();
    }

    // ══════════════════════════════════════════════════════════════════
    // CLASSES AUXILIARES
    // ══════════════════════════════════════════════════════════════════

    private class CycleTracker
    {
        public int      LastVolume         { get; set; }
        public string   LastBroker         { get; set; } = "";
        public int      MaxVolumeObserved  { get; set; }
        public bool     AlreadyFired       { get; set; }
        public DateTime? OldestCycleTime   => _placements.Count > 0 ? _placements[0].Time : null;

        // Listas de eventos de colocação e retirada
        private readonly List<CycleEvent> _placements  = new();
        private readonly List<CycleEvent> _withdrawals = new();

        public void RecordPlacement(DateTime time, int volume, string broker)
        {
            _placements.Add(new CycleEvent(time, volume, broker));
            if (volume > MaxVolumeObserved) MaxVolumeObserved = volume;
            if (!string.IsNullOrEmpty(broker)) LastBroker = broker;
        }

        public void RecordWithdrawal(DateTime time, int volume, string broker)
        {
            _withdrawals.Add(new CycleEvent(time, volume, broker));
        }

        /// <summary>
        /// Conta ciclos completos: uma colocação seguida de uma retirada = 1 ciclo.
        /// </summary>
        public int CountCycles()
        {
            return Math.Min(_placements.Count, _withdrawals.Count);
        }

        public void PurgeBefore(DateTime cutoff)
        {
            _placements.RemoveAll(e => e.Time < cutoff);
            _withdrawals.RemoveAll(e => e.Time < cutoff);
        }

        public void Clear()
        {
            _placements.Clear();
            _withdrawals.Clear();
            MaxVolumeObserved = 0;
        }
    }

    private record CycleEvent(DateTime Time, int Volume, string Broker);

    private class SpoofMarker
    {
        public BookSide Side       { get; set; }
        public string   Broker     { get; set; } = "";
        public DateTime DetectedAt { get; set; }
        public int      Cycles     { get; set; }
    }
}
