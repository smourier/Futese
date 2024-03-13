using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace Futese
{
    public class ThreadSafeIndex<TKey>(ITokenizer? tokenizer = null, IEqualityComparer<TKey>? keyEqualityComparer = null) : BaseIndex<TKey>(tokenizer, keyEqualityComparer) where TKey : IParsable<TKey>
    {
        protected override INodeDictionary CreateChildren() => new KeyDictionary();
        protected override IKeyCollection CreateKeys() => new KeyCollection();

        // difference with ConcurrentDictionary is this uses much less memory
        private sealed class KeyDictionary : INodeDictionary
        {
            private readonly Dictionary<byte[], INode> _inner = [];
            private readonly object _lock = new();

            public int Count => _inner.Count; // thread-safe
            public void Add(byte[] token, INode item) { lock (_lock) { _inner[token] = item; } }
            public bool Remove(byte[] token) { lock (_lock) { return _inner.Remove(token); } }
            public bool TryGetValue(byte[] token, [MaybeNullWhen(false)] out INode item) { lock (_lock) { return _inner.TryGetValue(token, out item); } }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            public IEnumerator<INode> GetEnumerator() { lock (_lock) { return ((IEnumerable<INode>)_inner.Values.ToArray()).GetEnumerator(); } }
        }

        private sealed class KeyCollection : IKeyCollection
        {
            private readonly List<TKey> _inner = [];
            private readonly object _lock = new();

            public int Count => _inner.Count; // thread-safe
            public void Add(TKey item) { lock (_lock) { _inner.Add(item); } }
            public bool Remove(TKey item) { lock (_lock) { return _inner.Remove(item); } }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            public IEnumerator<TKey> GetEnumerator() { lock (_lock) { return ((IEnumerable<TKey>)_inner.ToArray()).GetEnumerator(); } }
        }
    }
}
