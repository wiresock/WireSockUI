using System;
using System.Collections.Generic;

namespace WireSockUI.Forms
{
    /// <summary>
    ///     Fixed-capacity storage that overwrites the oldest item without shifting retained entries.
    /// </summary>
    internal sealed class BoundedRingBuffer<T>
    {
        private readonly T[] _items;
        private int _count;
        private int _start;

        internal BoundedRingBuffer(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _items = new T[capacity];
        }

        internal int Capacity => _items.Length;
        internal int Count => _count;

        internal T this[int index]
        {
            get
            {
                if (index < 0 || index >= _count)
                    throw new ArgumentOutOfRangeException(nameof(index));
                return _items[(_start + index) % _items.Length];
            }
        }

        internal void Add(T item)
        {
            if (_count < _items.Length)
            {
                _items[(_start + _count) % _items.Length] = item;
                _count++;
                return;
            }

            _items[_start] = item;
            _start = (_start + 1) % _items.Length;
        }

        internal void AddRange(IEnumerable<T> items)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));
            foreach (var item in items)
                Add(item);
        }

        internal void Clear()
        {
            Array.Clear(_items, 0, _items.Length);
            _count = 0;
            _start = 0;
        }
    }
}
