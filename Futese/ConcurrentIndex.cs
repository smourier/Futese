using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Futese
{
    public class ConcurrentIndex<TKey>(ITokenizer? tokenizer = null, IEqualityComparer<TKey>? keyEqualityComparer = null) : BaseIndex<TKey>(tokenizer, keyEqualityComparer) where TKey : IParsable<TKey>
    {
        protected override INodeDictionary CreateChildren() => new KeyDictionary();
        protected override IKeyCollection CreateKeys() => new KeyCollection();

        private sealed class KeyDictionary : ConcurrentDictionary<byte[], INode>, INodeDictionary
        {
            void INodeDictionary.Add(byte[] token, INode node) => this[token] = node;
            IEnumerator<INode> IEnumerable<INode>.GetEnumerator() => Values.GetEnumerator();
            bool INodeDictionary.Remove(byte[] token) => TryRemove(token, out _);
        }

        private sealed class KeyCollection : ConcurrentDictionary<TKey, object?>, IKeyCollection
        {
            void IKeyCollection.Add(TKey key) => this[key] = null;
            IEnumerator<TKey> IEnumerable<TKey>.GetEnumerator() => Keys.GetEnumerator();
            bool IKeyCollection.Remove(TKey key) => TryRemove(key, out _);
        }
    }
}
