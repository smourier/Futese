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
    public class Index<TKey>(ITokenizer? tokenizer = null, IEqualityComparer<TKey>? keyEqualityComparer = null) where TKey : IParsable<TKey>
    {
        private const string _fileSig = "FTS0";
        private static readonly bool _isKeyString = typeof(TKey) == typeof(string);
        private static readonly Encoding _encoding = Encoding.UTF8;
        private readonly NoKeysBranch _root = new([]);

        public ITokenizer Tokenizer { get; } = tokenizer ?? new DefaultTokenizer();
        public IEqualityComparer<TKey> KeyEqualityComparer { get; } = keyEqualityComparer ?? EqualityComparer<TKey>.Default;
        public int KeysCount { get; private set; }

        public virtual void Add(TKey key, string text)
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
            if (stream.Read(sig, 0, 4) != sig.Length)
                throw new InvalidDataException();

            if (!sig.SequenceEqual(Encoding.ASCII.GetBytes(_fileSig)))
                throw new InvalidDataException();

            KeysCount = 0;
            using var gz = new GZipStream(stream, CompressionMode.Decompress);
            using var ms = new MemoryStream(); // otherwise it's very slow
            gz.CopyTo(ms);
            ms.Position = 0;
            var reader = new BinaryReader(ms);

            // root has no token
            _ = reader.ReadInt32();
            var keys = reader.ReadInt32();
            var children = reader.ReadInt32();
            var uniqueKeys = new HashSet<TKey>(KeyEqualityComparer);
            AddChildren(reader, uniqueKeys, _root, keys, children);
            KeysCount = uniqueKeys.Count;
            GC.Collect();
        }

        private static void AddChildren(BinaryReader reader, HashSet<TKey> uniqueKeys, INode node, int keys, int children)
        {
            for (var i = 0; i < keys; i++)
            {
                var k = reader.ReadString();
                if (!_isKeyString)
                {
                    var key = TKey.Parse(k, CultureInfo.InvariantCulture);
                    node.Keys!.Add(key);
                    uniqueKeys.Add(key);
                }
                else
                {
                    var key = (TKey)(object)k;
                    node.Keys!.Add(key);
                    uniqueKeys.Add(key);
                }
            }

            for (var i = 0; i < children; i++)
            {
                var child = Read(reader, uniqueKeys);
                node.Children!.Add(child.Token, child);
            }
        }

        private static INode Read(BinaryReader reader, HashSet<TKey> uniqueKeys)
        {
            var tokenLength = reader.ReadInt32();
            var token = reader.ReadBytes(tokenLength);
            var keys = reader.ReadInt32();

            INode node;
            var children = reader.ReadInt32();
            if (children == 0)
            {
                node = new Leaf(token);
            }
            else if (keys == 0)
            {
                node = new NoKeysBranch(token);
            }
            else
            {
                node = new KeysBranch(token);
            }

            AddChildren(reader, uniqueKeys, node, keys, children);
            return node;
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
            using var gz = new GZipStream(stream, compressionLevel, true);
            var writer = new BinaryWriter(gz);
            Write(writer, _root);
        }

        private static void Write(BinaryWriter writer, INode node)
        {
            writer.Write(node.Token.Length);
            writer.Write(node.Token);
            writer.Write(node.Keys?.Count ?? 0);
            writer.Write(node.Children?.Count ?? 0);
            if (node.Keys != null)
            {
                foreach (var key in node.Keys)
                {
                    writer.Write(string.Format(CultureInfo.InvariantCulture, "{0}", key));
                }
            }

            if (node.Children != null)
            {
                foreach (var kv in node.Children)
                {
                    Write(writer, kv.Value);
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
                    foreach (var key in Search(child.Value, bytes, offset))
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
                    foreach (var childKey in GetAllKeys(kv.Value))
                    {
                        yield return childKey;
                    }
                }
            }
        }

        private static void Add(TKey key, ReadOnlySpan<byte> text, NoKeysBranch branch)
        {
            var textBytes = text.ToArray();
            if (branch.Children.TryGetValue(textBytes, out var child))
            {
                if (child is NoKeysBranch nkb)
                {
                    var withKeys = new KeysBranch(nkb.Token);
                    foreach (var kv in nkb.Children)
                    {
                        withKeys.Children.Add(kv);
                    }

                    branch.Children.Remove(textBytes);
                    branch.Children[textBytes] = withKeys;

                    withKeys.Keys.Add(key);
                }
                else
                {
                    child.Keys!.Add(key);
                }
                return;
            }

            var match = GetLongestMatch(branch.Children, text);
            if (match != null)
            {
                var matchNode = match.Value.Node;
                var matchKey = matchNode.Key;
                var matchLength = match.Value.Length;
                if (matchLength == matchKey.Length)
                {
                    if (matchNode.Value is NoKeysBranch childBranch)
                    {
                        Add(key, text[matchLength..], childBranch);
                        return;
                    }

                    var newLeaf = new Leaf(text[matchLength..]);
                    newLeaf.Keys.Add(key);
                    var newBranch = new KeysBranch(matchKey);
                    foreach (var oldKey in matchNode.Value.Keys!)
                    {
                        newBranch.Keys.Add(oldKey);
                    }

                    branch.Children.Remove(matchKey);
                    branch.Children.Add(newBranch.Token, newBranch);
                    newBranch.Children.Add(newLeaf.Token, newLeaf);
                    return;
                }

                // make a split
                branch.Children.Remove(matchNode);
                var top = new NoKeysBranch(text[..matchLength]);
                branch.Children.Add(top.Token, top);

                Span<byte> keySpan = matchKey;
                INode existingChild;
                if (matchNode.Value.Children != null)
                {
                    if (matchNode.Value.Keys != null)
                    {
                        existingChild = new KeysBranch(keySpan[matchLength..]);
                    }
                    else
                    {
                        existingChild = new NoKeysBranch(keySpan[matchLength..]);
                    }

                    foreach (var oldChildren in matchNode.Value.Children)
                    {
                        existingChild.Children!.Add(oldChildren);
                    }
                }
                else
                {
                    existingChild = new Leaf(keySpan[matchLength..]);
                }

                if (matchNode.Value.Keys != null)
                {
                    foreach (var oldKey in matchNode.Value.Keys)
                    {
                        existingChild.Keys!.Add(oldKey);
                    }
                }

                top.Children.Add(existingChild.Token, existingChild);
                var newChild = new Leaf(text[matchLength..]);
                newChild.Keys.Add(key);
                top.Children.Add(newChild.Token, newChild);
                return;
            }

            // not found create a leaf
            var leaf = new Leaf(text);
            leaf.Keys.Add(key);
            branch.Children.Add(leaf.Token, leaf);
        }

        private static (KeyValuePair<byte[], INode> Node, int Length)? GetLongestMatch(IDictionary<byte[], INode> children, ReadOnlySpan<byte> text)
        {
            // search longest match in each children
            foreach (var child in children)
            {
                var match = GetLongestMatch(child.Key, 0, text);
                if (match > 0)
                    return (child, match);
            }
            return null;
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

        private interface INode
        {
            byte[] Token { get; }
            ICollection<TKey>? Keys { get; }
            IDictionary<byte[], INode>? Children { get; }
        }

        private class NoKeysBranch(ReadOnlySpan<byte> token) : INode
        {
            public byte[] Token { get; } = token.ToArray();
            public virtual ICollection<TKey>? Keys => null;
            public IDictionary<byte[], INode> Children { get; } = new Dictionary<byte[], INode>(ByteArrayEqualityComparer.Instance);

            public override string ToString() => _encoding.GetString(Token) + ":" + string.Join(',', Children.Select(kv => _encoding.GetString(kv.Key) + "=" + kv.Value));
        }

        private class KeysBranch(ReadOnlySpan<byte> token) : NoKeysBranch(token)
        {
            public override ICollection<TKey> Keys { get; } = [];

            public override string ToString() => base.ToString() + ":" + string.Join(',', Keys);
        }

        private sealed class Leaf(ReadOnlySpan<byte> token) : INode
        {
            public byte[] Token { get; } = token.ToArray();
            public ICollection<TKey> Keys { get; } = [];
            public IDictionary<byte[], INode>? Children => null;

            public override string ToString() => _encoding.GetString(Token) + ":" + string.Join(',', Keys);
        }

        private sealed class ByteArrayEqualityComparer : IEqualityComparer<byte[]>
        {
            public static readonly ByteArrayEqualityComparer Instance = new();
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
    }
}
