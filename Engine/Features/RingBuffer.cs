using System;

namespace MarketCore.Engine.Features;

/// <summary>
/// Ring buffer de tamanho fixo pré-alocado.
/// Nunca aloca no caminho crítico — sobrescreve o item mais antigo quando cheio.
/// Thread-safe por lock interno.
/// </summary>
public class RingBuffer<T>
{
    private readonly T[]    _buffer;
    private readonly int    _capacity;
    private          int    _head;    // índice do item mais antigo
    private          int    _count;
    private readonly object _lock = new();

    public RingBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _buffer   = new T[capacity];
    }

    /// <summary>Adiciona item — sobrescreve o mais antigo se o buffer estiver cheio.</summary>
    public void Push(T item)
    {
        lock (_lock)
        {
            // O novo item ocupa a próxima posição livre (ou a do mais antigo quando cheio).
            int writeIdx = (_head + _count) % _capacity;
            _buffer[writeIdx] = item;

            if (_count < _capacity)
                _count++;
            else
                _head = (_head + 1) % _capacity; // avança o ponteiro do mais antigo
        }
    }

    /// <summary>
    /// Retorna os últimos <paramref name="count"/> itens — mais recente primeiro.
    /// Retorna Array.Empty se o buffer estiver vazio.
    /// </summary>
    public T[] GetLast(int count)
    {
        lock (_lock)
        {
            if (_count == 0) return Array.Empty<T>();
            int n = Math.Min(count, _count);
            var result = new T[n];
            for (int i = 0; i < n; i++)
            {
                // Mais recente = (_head + _count - 1) % _capacity; vai recuando.
                int idx = (_head + _count - 1 - i) % _capacity;
                result[i] = _buffer[idx];
            }
            return result;
        }
    }

    /// <summary>
    /// Retorna todos os itens ordenados do mais antigo ao mais recente.
    /// Retorna Array.Empty se o buffer estiver vazio.
    /// </summary>
    public T[] GetAll()
    {
        lock (_lock)
        {
            if (_count == 0) return Array.Empty<T>();
            var result = new T[_count];
            for (int i = 0; i < _count; i++)
                result[i] = _buffer[(_head + i) % _capacity];
            return result;
        }
    }

    public int  Count   { get { lock (_lock) return _count; } }
    public bool IsEmpty { get { lock (_lock) return _count == 0; } }
}
