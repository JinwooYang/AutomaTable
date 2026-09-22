using System.Collections.Generic;

namespace DataTable.Runtime.Internal
{
    internal sealed class PreloadedFindIndex<TKey, TValue> where TKey : notnull
    {
        private readonly Dictionary<TKey, TValue> _entries = new Dictionary<TKey, TValue>();

        public void Add(TKey key, TValue value)
        {
            if (!_entries.ContainsKey(key))
                _entries.Add(key, value);
        }

        public bool TryGet(TKey key, out TValue value)
        {
            if (_entries.TryGetValue(key, out var found))
            {
                value = found;
                return true;
            }

            value = default!;
            return false;
        }
    }

    internal sealed class PreloadedFindAllIndex<TKey, TValue> where TKey : notnull
    {
        private static readonly IReadOnlyList<TValue> Empty = new TValue[0];
        private readonly Dictionary<TKey, IReadOnlyList<TValue>> _entries =
            new Dictionary<TKey, IReadOnlyList<TValue>>();
        private Dictionary<TKey, List<TValue>>? _building =
            new Dictionary<TKey, List<TValue>>();

        public void Add(TKey key, TValue value)
        {
            var building = _building ?? throw new System.InvalidOperationException("The preload index is already sealed.");
            if (!building.TryGetValue(key, out var values))
            {
                values = new List<TValue>();
                building.Add(key, values);
            }

            values.Add(value);
        }

        public void Seal()
        {
            var building = _building;
            if (building == null)
                return;

            foreach (var pair in building)
                _entries.Add(pair.Key, pair.Value.AsReadOnly());

            _building = null;
        }

        public IReadOnlyList<TValue> Find(TKey key)
        {
            return _entries.TryGetValue(key, out var values) ? values : Empty;
        }
    }
}
