namespace Framex.Core.Monitoring;

/// <summary>Buffer circular de capacidade fixa: memória constante em sessões longas.</summary>
public sealed class RingBuffer<T>
{
    private readonly T[] _items;
    private int _start;

    public RingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;

    public int Count { get; private set; }

    public void Add(T item)
    {
        if (Count < _items.Length)
        {
            _items[(_start + Count) % _items.Length] = item;
            Count++;
        }
        else
        {
            _items[_start] = item;
            _start = (_start + 1) % _items.Length;
        }
    }

    public T this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)Count);
            return _items[(_start + index) % _items.Length];
        }
    }

    public void Clear()
    {
        _start = 0;
        Count = 0;
        Array.Clear(_items);
    }

    public T[] ToArray()
    {
        var result = new T[Count];
        for (var i = 0; i < Count; i++)
        {
            result[i] = this[i];
        }

        return result;
    }

    public void CopyTo(List<T> target)
    {
        target.Clear();
        for (var i = 0; i < Count; i++)
        {
            target.Add(this[i]);
        }
    }
}
