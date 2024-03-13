using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Futese
{
    public abstract class BaseIndex<TKey> where TKey : IParsable<TKey>
    {
        private const string _fileSig = "FTS0";
        private static readonly Encoding _encoding = Encoding.UTF8;
        private readonly NoKeysBranch _root;

        protected BaseIndex(ITokenizer? tokenizer = null, IEqualityComparer<TKey>? keyEqualityComparer = null)
        {
            _root = new([], CreateChildren());
            Tokenizer = tokenizer ?? new DefaultTokenizer();
            KeyEqualityComparer = keyEqualityComparer ?? EqualityComparer<TKey>.Default;
        }

        protected interface INode
        {
            byte[] Token { get; }
            IKeyCollection? Keys { get; }
            INodeDictionary? Children { get; }
        }

        protected interface IKeyCollection : IEnumerable<TKey>
        {
            int Count { get; }

            void Add(TKey key);
            bool Remove(TKey key);
        }

        protected interface INodeDictionary : IEnumerable<INode>
        {
            int Count { get; }

            void Add(byte[] token, INode node);
            bool Remove(byte[] token);
            bool TryGetValue(byte[] token, [MaybeNullWhen(false)] out INode node);
        }

        protected sealed class ByteArrayEqualityComparer : IEqualityComparer<byte[]>
        {
            public static ByteArrayEqualityComparer Instance { get; } = new();

            public bool Equals(byte[]? x, byte[]? y) => x!.SequenceEqual(y!);
            public int GetHashCode([DisallowNull] byte[] bytes)
            {
                var hashCode = 0;
                foreach (var b in bytes)
                {
                    hashCode ^= b.GetHashCode();
                }
                return hashCode;
            }
        }

        protected abstract INodeDictionary CreateChildren();
        protected abstract IKeyCollection CreateKeys();

        public ITokenizer Tokenizer { get; }
        public IEqualityComparer<TKey> KeyEqualityComparer { get; }
        public int KeysCount { get; private set; }
        public IEnumerable<TKey> Keys => GetAllKeys(_root).Distinct();

        public void Add(TKey key) => Add(key, key is IStringable s ? s.ToString() : key.ToString()!);
        public virtual void Add(TKey key, string? text)
        {
            ArgumentNullException.ThrowIfNull(key);
            foreach (var token in Tokenizer.EnumerateTokens(text))
            {
                AddToken(key, token.Text);
            }
            KeysCount++;
        }

        public virtual void AddToken(TKey key, string token)
        {
            if (string.IsNullOrEmpty(token))
                return;

            var bytes = _encoding.GetBytes(token).AsSpan();
            Add(key, bytes, _root);
        }

        public int Remove(TKey key) => Remove([key]);
        public virtual int Remove(IEnumerable<TKey> keys)
        {
            if (keys == null)
                return 0;

            var uniqueKeys = keys.Select(k => new RemovedKey(k)).ToHashSet(new RemovedKeyEqualityComparer(KeyEqualityComparer));
            if (uniqueKeys.Count == 0)
                return 0;

            Remove(uniqueKeys, _root);
            var count = uniqueKeys.Count(k => k.Removed);
            KeysCount -= count;
            return count;
        }

        // note these methods don't guarantee distinct nor ordered results
        public IEnumerable<TKey> Search(string text, ITokenizer? tokenizer = null) => Search(new Query(text, tokenizer));
        public virtual IEnumerable<TKey> Search(Query query)
        {
            ArgumentNullException.ThrowIfNull(query);
            if (query.Tokens.Count == 0)
                yield break;

            bool allCombined()
            {
                if (query.Tokens[0] is QueryToken qt && qt.Type == QueryTokenType.Not)
                    return false;

                return query.Tokens.Skip(1).All(t => t is QueryToken qt && qt.Type == QueryTokenType.Or);
            }

            if (allCombined())
            {
                foreach (var token in query.Tokens)
                {
                    foreach (var key in SearchToken(token.Text))
                    {
                        yield return key;
                    }
                }
                yield break;
            }

            if (query.Tokens.Count == 1)
            {
                if (query.Tokens[0] is QueryToken qt) // if it's a qt, it's a Not
                {
                    var not = SearchToken(qt.Text).ToHashSet(KeyEqualityComparer);
                    foreach (var key in GetAllKeys(_root))
                    {
                        if (not.Contains(key))
                            continue;

                        yield return key;
                    }
                    yield break;
                }

                foreach (var key in SearchToken(query.Tokens[0].Text))
                {
                    yield return key;
                }
                yield break;
            }

            var set = new HashSet<TKey>(KeyEqualityComparer);
            foreach (var token in query.Tokens.Where(t => t is QueryToken qt && qt.Type == QueryTokenType.Or))
            {
                foreach (var key in SearchToken(token.Text))
                {
                    set.Add(key);
                }
            }

            var i = 0;
            foreach (var token in query.Tokens.Where(t => t is not QueryToken qt || qt.Type == QueryTokenType.And))
            {
                if (i == 0)
                {
                    if (set.Count == 0)
                    {
                        foreach (var key in SearchToken(token.Text))
                        {
                            set.Add(key);
                        }
                        continue;
                    }
                }

                i++;
                set = set.Intersect(SearchToken(token.Text)).ToHashSet(KeyEqualityComparer);
            }

            if (set.Count == 0)
                yield break;

            foreach (var token in query.Tokens.Where(t => t is QueryToken qt && qt.Type == QueryTokenType.Not))
            {
                foreach (var key in SearchToken(token.Text))
                {
                    set.Remove(key);
                }
            }

            foreach (var key in set)
            {
                yield return key;
            }
        }

        public virtual IEnumerable<TKey> SearchToken(string token)
        {
            if (string.IsNullOrEmpty(token))
                yield break;

            var bytes = _encoding.GetBytes(token);
            foreach (var key in Search(_root, bytes, 0))
            {
                yield return key;
            }
        }

        public void Load(string filePath)
        {
            ArgumentNullException.ThrowIfNull(filePath);
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Load(stream);
        }

        public virtual void Load(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);
            var sig = new byte[_fileSig.Length];
            if (stream.Read(sig, 0, sig.Length) != sig.Length || !sig.SequenceEqual(Encoding.ASCII.GetBytes(_fileSig)))
                throw new InvalidDataException();

            KeysCount = 0;
            var reader = new BinaryReader(stream);
            var compressionLevel = (CompressionLevel)reader.ReadInt32();
            MemoryStream? ms = null;
            GZipStream? gz = null;
            BufferedStream? bs = null;
            if (compressionLevel != CompressionLevel.NoCompression)
            {
                gz = new GZipStream(stream, CompressionMode.Decompress);
                bs = new BufferedStream(gz); // check this https://github.com/dotnet/runtime/issues/39233
                reader = new BinaryReader(bs);
            }

            var uniqueKeysCount = reader.ReadInt32();
            var uniqueKeys = new List<TKey>();
            for (var i = 0; i < uniqueKeysCount; i++)
            {
                var skey = reader.ReadString();
                var key = TKey.Parse(skey, CultureInfo.InvariantCulture);
                uniqueKeys.Add(key);
            }

            _ = reader.ReadInt32(); // root has no token
            var keys = reader.ReadInt32();
            var children = reader.ReadInt32();
            AddChildren(reader, uniqueKeys, _root, keys, children);
            KeysCount = uniqueKeys.Count;
            ms?.Dispose();
            bs?.Dispose();
            gz?.Dispose();
        }

        public void Save(string filePath, CompressionLevel compressionLevel = CompressionLevel.Optimal)
        {
            ArgumentNullException.ThrowIfNull(filePath);
            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            Save(stream, compressionLevel);
        }

        public virtual void Save(Stream stream, CompressionLevel compressionLevel = CompressionLevel.Optimal)
        {
            ArgumentNullException.ThrowIfNull(stream);
            stream.Write(Encoding.ASCII.GetBytes(_fileSig));
            stream.Write(BitConverter.GetBytes((int)compressionLevel)); // leave room for other options

            var childrenStream = new MemoryStream();
            var childrenWriter = new BinaryWriter(childrenStream);
            var uniqueKeys = new Dictionary<TKey, int>(KeyEqualityComparer);
            Write(childrenWriter, uniqueKeys, _root);

            BinaryWriter writer;
            GZipStream? gz = null;
            if (compressionLevel == CompressionLevel.NoCompression)
            {
                writer = new BinaryWriter(stream);
            }
            else
            {
                gz = new GZipStream(stream, compressionLevel, true);
                writer = new BinaryWriter(gz);
            }
            writer.Write(uniqueKeys.Count);
            foreach (var kv in uniqueKeys.OrderBy(k => k.Value))
            {
                string skey;
                if (kv.Key is IStringable stringable)
                {
                    skey = stringable.ToString();
                }
                else
                {
                    skey = string.Format(CultureInfo.InvariantCulture, "{0}", kv.Key);
                }

                writer.Write(skey);
            }
            childrenStream.Position = 0;
            childrenStream.CopyTo(gz ?? stream);
            gz?.Dispose();
        }

        private static void Remove(ISet<RemovedKey> keys, INode node)
        {
            if (node.Keys != null)
            {
                foreach (var key in keys)
                {
                    if (node.Keys.Remove(key.Key))
                    {
                        key.Removed = true;
                    }
                }
            }

            if (node.Children != null)
            {
                foreach (var kv in node.Children)
                {
                    Remove(keys, kv);
                }
            }
        }

        private void AddChildren(BinaryReader reader, List<TKey> uniqueKeys, INode node, int keys, int children)
        {
            for (var i = 0; i < keys; i++)
            {
                var keyIndex = reader.ReadInt32();
                node.Keys!.Add(uniqueKeys[keyIndex]);
            }

            for (var i = 0; i < children; i++)
            {
                var child = Read(reader, uniqueKeys);
                node.Children!.Add(child.Token, child);
            }
        }

        private INode Read(BinaryReader reader, List<TKey> uniqueKeys)
        {
            var tokenLength = reader.ReadInt32();
            var token = reader.ReadBytes(tokenLength);
            var keys = reader.ReadInt32();

            INode node;
            var children = reader.ReadInt32();
            if (children == 0)
            {
                node = new Leaf(token, CreateKeys());
            }
            else if (keys == 0)
            {
                node = new NoKeysBranch(token, CreateChildren());
            }
            else
            {
                node = new KeysBranch(token, CreateChildren(), CreateKeys());
            }

            AddChildren(reader, uniqueKeys, node, keys, children);
            return node;
        }

        private static void Write(BinaryWriter writer, Dictionary<TKey, int> keys, INode node)
        {
            writer.Write(node.Token.Length);
            writer.Write(node.Token);
            writer.Write(node.Keys?.Count ?? 0);
            writer.Write(node.Children?.Count ?? 0);
            if (node.Keys != null)
            {
                foreach (var key in node.Keys)
                {
                    if (!keys.TryGetValue(key, out var index))
                    {
                        index = keys.Count;
                        keys.Add(key, index);
                    }
                    writer.Write(index);
                }
            }

            if (node.Children != null)
            {
                foreach (var kv in node.Children)
                {
                    Write(writer, keys, kv);
                }
            }
        }

        private static IEnumerable<TKey> Search(INode node, byte[] bytes, int offset)
        {
            if (node.Token.Length > 0)
            {
                var match = GetLongestMatch(bytes, offset, node.Token);
                if (match == 0)
                    yield break;

                if (match == bytes.Length - offset)
                {
                    foreach (var key in GetAllKeys(node))
                    {
                        yield return key;
                    }
                    yield break;
                }

                offset += match;
            }

            if (node.Children != null)
            {
                foreach (var child in node.Children)
                {
                    foreach (var key in Search(child, bytes, offset))
                    {
                        yield return key;
                    }
                }
            }
        }

        private static IEnumerable<TKey> GetAllKeys(INode node)
        {
            if (node == null)
                yield break;

            if (node.Keys != null)
            {
                foreach (var key in node.Keys)
                {
                    yield return key;
                }
            }

            if (node.Children != null)
            {
                foreach (var kv in node.Children)
                {
                    foreach (var childKey in GetAllKeys(kv))
                    {
                        yield return childKey;
                    }
                }
            }
        }

        private void Add(TKey key, ReadOnlySpan<byte> text, NoKeysBranch branch)
        {
            var textBytes = text.ToArray();
            if (branch.Children.TryGetValue(textBytes, out var child))
            {
                if (child is NoKeysBranch nkb)
                {
                    var withKeys = new KeysBranch(nkb.Token, CreateChildren(), CreateKeys());
                    foreach (var kv in nkb.Children)
                    {
                        withKeys.Children.Add(kv.Token, kv);
                    }

                    branch.Children.Remove(textBytes);
                    branch.Children.Add(textBytes, withKeys);

                    withKeys.Keys.Add(key);
                }
                else
                {
                    child.Keys!.Add(key);
                }
                return;
            }

            var (matchNode, length) = GetLongestMatch(branch.Children, text);
            if (matchNode != null)
            {
                var matchKey = matchNode.Token;
                var matchLength = length;
                if (matchLength == matchKey.Length)
                {
                    if (matchNode is NoKeysBranch childBranch)
                    {
                        Add(key, text[matchLength..], childBranch);
                        return;
                    }

                    var newLeaf = new Leaf(text[matchLength..], CreateKeys());
                    newLeaf.Keys.Add(key);
                    var newBranch = new KeysBranch(matchKey, CreateChildren(), CreateKeys());
                    foreach (var oldKey in matchNode.Keys!)
                    {
                        newBranch.Keys.Add(oldKey);
                    }

                    branch.Children.Remove(matchKey);
                    branch.Children.Add(newBranch.Token, newBranch);
                    newBranch.Children.Add(newLeaf.Token, newLeaf);
                    return;
                }

                // make a split
                branch.Children.Remove(matchNode.Token);
                var top = new NoKeysBranch(text[..matchLength], CreateChildren());
                branch.Children.Add(top.Token, top);

                Span<byte> keySpan = matchKey;
                INode existingChild;
                if (matchNode.Children != null)
                {
                    if (matchNode.Keys != null)
                    {
                        existingChild = new KeysBranch(keySpan[matchLength..], CreateChildren(), CreateKeys());
                    }
                    else
                    {
                        existingChild = new NoKeysBranch(keySpan[matchLength..], CreateChildren());
                    }

                    foreach (var oldChildren in matchNode.Children)
                    {
                        existingChild.Children!.Add(oldChildren.Token, oldChildren);
                    }
                }
                else
                {
                    existingChild = new Leaf(keySpan[matchLength..], CreateKeys());
                }

                if (matchNode.Keys != null)
                {
                    foreach (var oldKey in matchNode.Keys)
                    {
                        existingChild.Keys!.Add(oldKey);
                    }
                }

                top.Children.Add(existingChild.Token, existingChild);
                var newChild = new Leaf(text[matchLength..], CreateKeys());
                newChild.Keys.Add(key);
                top.Children.Add(newChild.Token, newChild);
                return;
            }

            // not found create a leaf
            var leaf = new Leaf(text, CreateKeys());
            leaf.Keys.Add(key);
            branch.Children.Add(leaf.Token, leaf);
        }

        private static (INode? Node, int Length) GetLongestMatch(INodeDictionary children, ReadOnlySpan<byte> text)
        {
            // search longest match in each children
            foreach (var child in children)
            {
                var match = GetLongestMatch(child.Token, 0, text);
                if (match > 0)
                    return (child, match);
            }
            return (null, 0);
        }

        private static int GetLongestMatch(byte[] bytes, int bytesOffset, ReadOnlySpan<byte> token)
        {
            var min = Math.Min(bytes.Length - bytesOffset, token.Length);
            for (var i = 0; i < min; i++)
            {
                if (bytes[i + bytesOffset] != token[i])
                    return i;
            }
            return min;
        }

        private class NoKeysBranch(ReadOnlySpan<byte> token, INodeDictionary children) : INode
        {
            public byte[] Token { get; } = token.ToArray();
            public virtual IKeyCollection? Keys => null;
            public INodeDictionary Children => children;

            public override string ToString() => _encoding.GetString(Token) + ":" + string.Join(',', Children);
        }

        private class KeysBranch(ReadOnlySpan<byte> token, INodeDictionary children, IKeyCollection keys) : NoKeysBranch(token, children)
        {
            public override IKeyCollection Keys => keys;

            public override string ToString() => base.ToString() + ":" + string.Join(',', Keys);
        }

        private sealed class Leaf(ReadOnlySpan<byte> token, IKeyCollection keys) : INode
        {
            public byte[] Token { get; } = token.ToArray();
            public IKeyCollection Keys => keys;
            public INodeDictionary? Children => null;

            public override string ToString() => _encoding.GetString(Token) + ":" + string.Join(',', Keys);
        }

        private sealed class RemovedKey(TKey key)
        {
            public TKey Key = key;
            public bool Removed;
        }

        private sealed class RemovedKeyEqualityComparer(IEqualityComparer<TKey> keyEqualityComparer) : IEqualityComparer<RemovedKey>
        {
            public bool Equals(RemovedKey? x, RemovedKey? y) => keyEqualityComparer.Equals(x!.Key, y!.Key);
            public int GetHashCode([DisallowNull] RemovedKey obj) => obj.Key.GetHashCode();
        }
    }
}
