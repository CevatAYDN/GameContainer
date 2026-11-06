using System;
using System.Collections.Generic;
using UnityEngine.Scripting;

namespace Nexus.Core
{
    /// <summary>
    /// General-purpose, type-safe binder (Strange-style "Bind(anything).To(anything)").
    ///
    /// Use it for mappings that live OUTSIDE the MVCS container: entity catalogs,
    /// config registries, multi-loaders, UI-theme tables — any key→value table that
    /// deserves a discoverable registration point instead of scattered if/switch code.
    ///
    /// <code>
    /// // In OnConfigure:
    /// builder.BindBinder&lt;UnitType, UnitDefinition&gt;();
    ///
    /// // Anywhere (injected):
    /// [Inject] public IBinder&lt;UnitType, UnitDefinition&gt; Units { get; set; }
    ///
    /// // Register entries at startup:
    /// Units.Bind(UnitType.Warrior).To(new WarriorDefinition());
    /// Units.Bind(UnitType.Wizard).ToName("elite").To(new EliteWizardDefinition());
    ///
    /// // Read:
    /// var def = Units.Get(UnitType.Warrior);
    /// </code>
    /// </summary>
    [Preserve]
    public interface IBinder<TKey, TValue>
    {
        /// <summary>Starts a binding for <paramref name="key"/>. Chain .To/.ToName/.ToFactory.</summary>
        IBinderBindingBuilder<TKey, TValue> Bind(TKey key);

        /// <summary>True when a default (unnamed) binding exists for the key.</summary>
        bool Has(TKey key);
        /// <summary>True when a named binding exists for the key.</summary>
        bool Has(TKey key, string name);

        /// <summary>Returns the default binding; throws <see cref="KeyNotFoundException"/> when absent.</summary>
        TValue Get(TKey key);
        /// <summary>Returns the named binding; throws <see cref="KeyNotFoundException"/> when absent.</summary>
        TValue Get(TKey key, string name);

        /// <summary>Attempts to read the default binding.</summary>
        bool TryGet(TKey key, out TValue value);
        /// <summary>Attempts to read the named binding.</summary>
        bool TryGet(TKey key, string name, out TValue value);

        /// <summary>Removes the default binding (and any named bindings for the key).</summary>
        void Unbind(TKey key);

        /// <summary>Total number of stored entries (all names).</summary>
        int Count { get; }
    }

    /// <summary>Fluent builder returned by <see cref="IBinder{TKey,TValue}.Bind"/>.</summary>
    [Preserve]
    public interface IBinderBindingBuilder<TKey, TValue>
    {
        /// <summary>Binds a concrete value (single instance, returned as-is on Get).</summary>
        IBinderBindingBuilder<TKey, TValue> To(TValue value);
        /// <summary>Binds a factory (fresh instance per Get).</summary>
        IBinderBindingBuilder<TKey, TValue> ToFactory(Func<TValue> factory);
        /// <summary>
        /// Binds an implementation type resolved through the owning context's DI container.
        /// Requires the binder to have been created via <see cref="IContextBuilder.BindBinder{TKey,TValue}"/>.
        /// </summary>
        IBinderBindingBuilder<TKey, TValue> To<TImplementation>() where TImplementation : class, TValue;
        /// <summary>
        /// Qualifies the binding with a name (can be chained before or after .To). Before .To only the named entry is
        /// written. After .To the named entry is added and the key's default is kept: a default bound earlier stays, and
        /// a key without one keeps the value as its default too.
        /// </summary>
        IBinderBindingBuilder<TKey, TValue> ToName(string name);
    }

    /// <summary>
    /// Default <see cref="IBinder{TKey,TValue}"/> implementation. Reads are dictionary
    /// lookups keyed by a struct — steady-state Get/TryGet allocate nothing.
    /// Thread-safe: a reader-writer lock lets reads run concurrently (each takes the read
    /// lock) while a write is exclusive.
    /// </summary>
    [Preserve]
    public sealed class NexusBinder<TKey, TValue> : IBinder<TKey, TValue>
    {
        private readonly struct BinderKey : IEquatable<BinderKey>
        {
            public readonly TKey Key;
            public readonly string Name;

            public BinderKey(TKey key, string name)
            {
                Key = key;
                Name = name;
            }

            public bool Equals(BinderKey other)
            {
                return EqualityComparer<TKey>.Default.Equals(Key, other.Key)
                    && string.Equals(Name, other.Name, StringComparison.Ordinal);
            }

            public override bool Equals(object obj) => obj is BinderKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = Key != null ? EqualityComparer<TKey>.Default.GetHashCode(Key) : 0;
                    return (hash * 397) ^ (Name != null ? Name.GetHashCode() : 0);
                }
            }
        }

        internal enum EntryKind { Value, Factory, Type }

        internal sealed class BinderEntry
        {
            public EntryKind Kind;
            public TValue Value;
            public Func<TValue> Factory;
            public Type ConcreteType;
            public BinderEntry Copy() => (BinderEntry)MemberwiseClone();
        }

        private readonly Dictionary<BinderKey, BinderEntry> _entries = new();
        // Secondary key index (key -> its BinderKeys, default + all named variants) so
        // Unbind is O(#bindings for the key) instead of an O(total entries) scan under
        // the exclusive write lock (which blocked every concurrent reader/Get). Reads are
        // untouched; only the rare write paths pay for the index.
        private readonly Dictionary<TKey, List<BinderKey>> _keyIndex = new();
        private readonly System.Threading.ReaderWriterLockSlim _rwLock = new(System.Threading.LockRecursionPolicy.NoRecursion);
        private readonly NexusDI _container;

        /// <summary>Creates a standalone binder. Pass a container to enable .To&lt;T&gt;() type mappings.</summary>
        public NexusBinder(NexusDI container = null)
        {
            _container = container;
        }

        public int Count
        {
            get
            {
                _rwLock.EnterReadLock();
                try { return _entries.Count; }
                finally { _rwLock.ExitReadLock(); }
            }
        }

        public IBinderBindingBuilder<TKey, TValue> Bind(TKey key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            return new BinderBindingBuilder(this, key);
        }

        public bool Has(TKey key)
        {
            _rwLock.EnterReadLock();
            try { return _entries.ContainsKey(new BinderKey(key, null)); }
            finally { _rwLock.ExitReadLock(); }
        }

        public bool Has(TKey key, string name)
        {
            _rwLock.EnterReadLock();
            try { return _entries.ContainsKey(new BinderKey(key, name)); }
            finally { _rwLock.ExitReadLock(); }
        }

        public TValue Get(TKey key)
        {
            if (TryGet(key, out var value)) return value;
            throw new KeyNotFoundException($"Binder<{typeof(TKey).Name}, {typeof(TValue).Name}> has no default binding for key '{key}'.");
        }

        public TValue Get(TKey key, string name)
        {
            if (TryGet(key, name, out var value)) return value;
            throw new KeyNotFoundException($"Binder<{typeof(TKey).Name}, {typeof(TValue).Name}> has no binding named '{name}' for key '{key}'.");
        }

        public bool TryGet(TKey key, out TValue value) => TryGet(key, null, out value);

        public bool TryGet(TKey key, string name, out TValue value)
        {
            _rwLock.EnterReadLock();
            BinderEntry entry;
            bool found;
            try { found = _entries.TryGetValue(new BinderKey(key, name), out entry); }
            finally { _rwLock.ExitReadLock(); }

            if (found)
            {
                value = ResolveEntry(entry);
                return true;
            }
            value = default;
            return false;
        }

        public void Unbind(TKey key)
        {
            _rwLock.EnterWriteLock();
            try
            {
                // Index lookup replaces the previous O(n) full-table scan. Removing from
                // _entries never touches _keyIndex, so iterating the key's own list while
                // removing entries is safe (no dictionary-in-enumeration mutation).
                if (!_keyIndex.TryGetValue(key, out var keysToRemove) || keysToRemove.Count == 0)
                    return;

                for (int i = 0; i < keysToRemove.Count; i++)
                    _entries.Remove(keysToRemove[i]);
                _keyIndex.Remove(key);
            }
            finally { _rwLock.ExitWriteLock(); }
        }

        private TValue ResolveEntry(BinderEntry entry)
        {
            switch (entry.Kind)
            {
                case EntryKind.Value:
                    return entry.Value;
                case EntryKind.Factory:
                    return entry.Factory();
                case EntryKind.Type:
                    if (_container == null)
                        throw new InvalidOperationException(
                            $"Binder<{typeof(TKey).Name}, {typeof(TValue).Name}> cannot resolve type mapping '{entry.ConcreteType.Name}': " +
                            "the binder was created without a container. Use builder.BindBinder<,>() to enable type mappings.");
                    return (TValue)_container.Resolve(entry.ConcreteType);
                default:
                    return default;
            }
        }

        // Internal write method used by the fluent builder — takes write lock. Returns the entry it replaced, or null.
        internal BinderEntry CommitEntry(TKey key, string name, BinderEntry entry)
        {
            _rwLock.EnterWriteLock();
            try
            {
                var binderKey = new BinderKey(key, name);
                _entries.TryGetValue(binderKey, out var replaced);
                _entries[binderKey] = entry;
                if (!_keyIndex.TryGetValue(key, out var list))
                {
                    list = new List<BinderKey>(1);
                    _keyIndex[key] = list;
                }
                // Re-binding the same (key, name) must not accumulate duplicate index entries
                // (removal is idempotent, but the list would grow on every rebind).
                if (!list.Contains(binderKey))
                    list.Add(binderKey);
                return replaced;
            }
            finally { _rwLock.ExitWriteLock(); }
        }

        // ─── Fluent builder ──────────────────────────────────────────────────
        private sealed class BinderBindingBuilder : IBinderBindingBuilder<TKey, TValue>
        {
            private readonly NexusBinder<TKey, TValue> _owner;
            private readonly TKey _key;
            private string _name;
            private BinderEntry _entry;
            private bool _committed;
            // the key's default before this chain first wrote it (null: the key had none)
            private bool _wroteDefault;
            private BinderEntry _replacedDefault;

            public BinderBindingBuilder(NexusBinder<TKey, TValue> owner, TKey key)
            {
                _owner = owner;
                _key = key;
                _entry = new BinderEntry();
            }

            public IBinderBindingBuilder<TKey, TValue> To(TValue value)
            {
                _entry.Kind = EntryKind.Value;
                _entry.Value = value;
                Commit();
                return this;
            }

            public IBinderBindingBuilder<TKey, TValue> ToFactory(Func<TValue> factory)
            {
                _entry.Kind = EntryKind.Factory;
                _entry.Factory = factory ?? throw new ArgumentNullException(nameof(factory));
                Commit();
                return this;
            }

            public IBinderBindingBuilder<TKey, TValue> To<TImplementation>() where TImplementation : class, TValue
            {
                _entry.Kind = EntryKind.Type;
                _entry.ConcreteType = typeof(TImplementation);
                Commit();
                return this;
            }

            public IBinderBindingBuilder<TKey, TValue> ToName(string name)
            {
                // Allow .To(x).ToName("n") as well as .ToName("n").To(x). After .To(v) the chain
                // already wrote v as the key's default: put back the default it replaced (#292),
                // or keep v as the default when the key had none, then register v under the name
                // as its own entry, so a later .To on this chain changes the named entry only.
                _name = name;
                if (_committed)
                {
                    if (_replacedDefault != null) _owner.CommitEntry(_key, null, _replacedDefault);
                    _entry = _entry.Copy();
                    Commit();
                }
                return this;
            }

            private void Commit()
            {
                var replaced = _owner.CommitEntry(_key, _name, _entry);
                if (_name == null && !_wroteDefault)
                {
                    _wroteDefault = true;
                    _replacedDefault = replaced;
                }
                _committed = true;
            }
        }
    }
}
