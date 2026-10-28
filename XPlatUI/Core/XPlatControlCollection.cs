using System.Collections;

namespace XPlatUI;

/// <summary>
/// Ordered child control collection. Adding or removing a control updates the parent
/// link and triggers layout on the owning control.
/// </summary>
public sealed class XPlatControlCollection : IList<XPlatControl>
{
    private readonly XPlatControl _owner;
    private readonly List<XPlatControl> _items = new();

    internal XPlatControlCollection(XPlatControl owner) => _owner = owner;

    public XPlatControl this[int index]
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

            existing.Parent = null;
            _owner.OnControlRemoved(existing);
            _items[index] = value;
            value.Parent = _owner;
            _owner.OnControlAdded(value);
        }
    }

    public int Count => _items.Count;

    public bool IsReadOnly => false;

    public void Add(XPlatControl item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Add(item);
        item.Parent = _owner;
        _owner.OnControlAdded(item);
    }

    public void AddRange(params XPlatControl[] items)
    {
        foreach (var item in items)
        {
            Add(item);
        }
    }

    public void Insert(int index, XPlatControl item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Insert(index, item);
        item.Parent = _owner;
        _owner.OnControlAdded(item);
    }

    public bool Remove(XPlatControl item)
    {
        if (item is null || !_items.Remove(item))
        {
            return false;
        }

        item.Parent = null;
        _owner.OnControlRemoved(item);
        return true;
    }

    public void RemoveAt(int index)
    {
        var item = _items[index];
        _items.RemoveAt(index);
        item.Parent = null;
        _owner.OnControlRemoved(item);
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
            item.Parent = null;
            _owner.OnControlRemoved(item);
        }
    }

    public bool Contains(XPlatControl item) => _items.Contains(item);

    public int IndexOf(XPlatControl item) => _items.IndexOf(item);

    public void CopyTo(XPlatControl[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

    public IEnumerator<XPlatControl> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
