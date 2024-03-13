using System;
using System.Collections.Generic;

namespace Futese
{
    public class Index<TKey>(ITokenizer? tokenizer = null, IEqualityComparer<TKey>? keyEqualityComparer = null) : BaseIndex<TKey>(tokenizer, keyEqualityComparer) where TKey : IParsable<TKey>
    {
        protected override INodeDictionary CreateChildren() => new KeyDictionary();
        protected override IKeyCollection CreateKeys() => new KeyCollection();

        private sealed class KeyCollection : List<TKey>, IKeyCollection { }
        private sealed class KeyDictionary : Dictionary<byte[], INode>, INodeDictionary
        {
            IEnumerator<INode> IEnumerable<INode>.GetEnumerator() => Values.GetEnumerator();
        }
    }
}
