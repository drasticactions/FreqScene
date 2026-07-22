using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace FreqScene;

public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var added = false;
        foreach (var item in items)
        {
            Items.Add(item); // protected backing list: no notification per item
            added = true;
        }

        if (!added)
        {
            return;
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
