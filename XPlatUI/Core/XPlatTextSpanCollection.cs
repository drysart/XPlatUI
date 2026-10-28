using System.Collections;

namespace XPlatUI;

/// <summary>
/// Ordered span collection of an <see cref="XPlatRichLabel"/>. Adding or removing a span
/// links it to the owning label, syncs the inline controls it contributes and re-runs layout.
/// </summary>
public sealed class XPlatTextSpanCollection : IList<XPlatTextSpan>
{
    private readonly XPlatRichLabel _owner;
    private readonly List<XPlatTextSpan> _items = new();

    internal XPlatTextSpanCollection(XPlatRichLabel owner) => _owner = owner;

    public XPlatTextSpan this[int index]
    {
        get => _items[index];
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            var existing = _items[index];
            if (ReferenceEquals(existing, value))
            {
                return;
            }

            existing.Owner = null;
            _owner.OnSpanRemoved(existing);
            _items[index] = value;
            value.Owner = _owner;
            _owner.OnSpanAdded(value);
        }
    }

    public int Count => _items.Count;

    public bool IsReadOnly => false;

    public void Add(XPlatTextSpan item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (ReferenceEquals(item.Owner, _owner) && _items.Contains(item))
        {
            return;
        }

        _items.Add(item);
        item.Owner = _owner;
        _owner.OnSpanAdded(item);
    }

    public void AddRange(params XPlatTextSpan[] items)
    {
        foreach (var item in items)
        {
            Add(item);
        }
    }

    public void Insert(int index, XPlatTextSpan item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Insert(index, item);
        item.Owner = _owner;
        _owner.OnSpanAdded(item);
    }

    public bool Remove(XPlatTextSpan item)
    {
        if (item is null || !_items.Remove(item))
        {
            return false;
        }

        item.Owner = null;
        _owner.OnSpanRemoved(item);
        return true;
    }

    public void RemoveAt(int index)
    {
        var item = _items[index];
        _items.RemoveAt(index);
        item.Owner = null;
        _owner.OnSpanRemoved(item);
    }

    public void Clear()
    {
        if (_items.Count == 0)
        {
            return;
        }

        var removed = _items.ToArray();
        _items.Clear();
        foreach (var item in removed)
        {
            item.Owner = null;
            _owner.OnSpanRemoved(item);
        }
    }

    public bool Contains(XPlatTextSpan item) => _items.Contains(item);

    public int IndexOf(XPlatTextSpan item) => _items.IndexOf(item);

    public void CopyTo(XPlatTextSpan[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

    public IEnumerator<XPlatTextSpan> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
