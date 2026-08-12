using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace MarketCore.WPF;

/// <summary>
/// Tape: mutações agrupadas com um só <see cref="NotifyCollectionChangedAction.Reset"/>.
/// (<see cref="NotifyCollectionChangedAction.Add"/> com lista de vários itens quebra o
/// <c>ListCollectionView</c> do WPF → “ItemsControl inconsistente”.)
/// </summary>
public sealed class TapeObservableCollection : ObservableCollection<TapeRecord>
{
    private bool _bulk;

    /// <summary>
    /// Substitui todo o conteúdo (troca de workspace / cenários raros). Dispara Reset.
    /// </summary>
    public void ResetContents(IReadOnlyList<TapeRecord> items)
    {
        CheckReentrancy();
        _bulk = true;
        try
        {
            Clear();
            for (int i = 0; i < items.Count; i++)
                Items.Add(items[i]);
        }
        finally
        {
            _bulk = false;
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>
    /// Mesmo contrato da tape antiga: <paramref name="oldestToNewestInBatch"/> está na ordem do dequeue FIFO;
    /// o topo da lista (= índice 0) deve ser o negócio mais recente desse lote.
    /// Um único <see cref="NotifyCollectionChangedAction.Reset"/> ao fim mantém estado alinhado com o ItemsControl.
    /// </summary>
    public void PrependDequeBatchAndTrim(IReadOnlyList<TapeRecord> oldestToNewestInBatch, int maxCount)
    {
        CheckReentrancy();

        int n = oldestToNewestInBatch.Count;
        if (n == 0)
            return;

        _bulk = true;
        try
        {
            for (int i = 0; i < n; i++)
                Items.Insert(0, oldestToNewestInBatch[i]);

            while (Count > maxCount)
                Items.RemoveAt(Count - 1);
        }
        finally
        {
            _bulk = false;
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_bulk)
            return;
        base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_bulk)
            return;
        base.OnPropertyChanged(e);
    }
}
